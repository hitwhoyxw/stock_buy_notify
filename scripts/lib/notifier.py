"""对外推送：企业微信群机器人 / 飞书自定义机器人 / 邮箱 SMTP。

设计原则：
- 三通道独立开关；任一通道 secret 缺失就跳过，不影响其它。
- P0 必推、P1 企微+邮件、P2/P3 只写日报由 caller 决定，本模块只负责"发"。
- 幂等：同一 signal_id 30 分钟内不重推（KV 状态存 data/cache/notify_seen.json）。
- CLI 用法（Actions step 末尾调用）：
    python -m lib.notifier --report data/report_2026-07-31_T1.md
    python -m lib.notifier --latest
"""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
import smtplib
import sys
import time
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText
from email.utils import formataddr
from pathlib import Path
from typing import Any, Dict, List, Optional

import requests

from lib.paths import CACHE_DIR, DATA_DIR
from lib.report import latest_report

_SEEN_FILE = CACHE_DIR / "notify_seen.json"
_DEDUP_TTL_SEC = 30 * 60  # 30 分钟


# ============================================================
# 幂等去重
# ============================================================

def _load_seen() -> Dict[str, float]:
    if not _SEEN_FILE.exists():
        return {}
    try:
        return json.loads(_SEEN_FILE.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return {}


def _save_seen(seen: Dict[str, float]) -> None:
    CACHE_DIR.mkdir(parents=True, exist_ok=True)
    now = time.time()
    seen = {k: v for k, v in seen.items() if now - v < _DEDUP_TTL_SEC * 2}
    _SEEN_FILE.write_text(json.dumps(seen, ensure_ascii=False), encoding="utf-8")


def _dedup_key(content: str) -> str:
    return hashlib.sha1(content.encode("utf-8")).hexdigest()[:16]


def _is_duplicate(key: str) -> bool:
    seen = _load_seen()
    now = time.time()
    ts = seen.get(key, 0)
    return (now - ts) < _DEDUP_TTL_SEC


def _mark_seen(key: str) -> None:
    seen = _load_seen()
    seen[key] = time.time()
    _save_seen(seen)


# ============================================================
# 企业微信
# ============================================================

def send_wecom(markdown: str, title: str = "") -> bool:
    key = os.environ.get("WECOM_BOT_KEY", "").strip()
    if not key:
        return False
    url = f"https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key={key}"
    # 企微 markdown 有 4096 字节限制
    body = markdown[:3800]
    if title:
        body = f"### {title}\n\n{body}"
    payload = {"msgtype": "markdown", "markdown": {"content": body}}
    try:
        r = requests.post(url, json=payload, timeout=10)
        r.raise_for_status()
        data = r.json()
        if data.get("errcode") != 0:
            print(f"[notifier] 企微推送错误：{data}", file=sys.stderr)
            return False
        return True
    except Exception as e:
        print(f"[notifier] 企微请求失败：{e}", file=sys.stderr)
        return False


# ============================================================
# 飞书
# ============================================================

def send_lark(markdown: str, title: str = "") -> bool:
    url = os.environ.get("LARK_WEBHOOK", "").strip()
    if not url:
        return False
    # 飞书 rich_text 需要行内数组结构；此处用 post 类型的简单文本卡片
    elements = []
    if title:
        elements.append({"tag": "div", "text": {"tag": "lark_md", "content": f"**{title}**"}})
    elements.append({"tag": "div", "text": {"tag": "lark_md", "content": markdown[:8000]}})
    payload = {
        "msg_type": "interactive",
        "card": {
            "config": {"wide_screen_mode": True},
            "elements": elements,
        },
    }
    try:
        r = requests.post(url, json=payload, timeout=10)
        r.raise_for_status()
        data = r.json()
        if data.get("StatusCode", data.get("code", 0)) != 0:
            print(f"[notifier] 飞书推送错误：{data}", file=sys.stderr)
            return False
        return True
    except Exception as e:
        print(f"[notifier] 飞书请求失败：{e}", file=sys.stderr)
        return False


# ============================================================
# 邮箱
# ============================================================

def send_email(body: str, subject: str) -> bool:
    host = os.environ.get("SMTP_HOST", "").strip()
    port_str = os.environ.get("SMTP_PORT", "465").strip()
    user = os.environ.get("SMTP_USER", "").strip()
    pwd = os.environ.get("SMTP_PASS", "").strip()
    to = os.environ.get("SMTP_TO", user).strip()
    if not (host and user and pwd and to):
        return False

    try:
        port = int(port_str)
    except ValueError:
        port = 465

    msg = MIMEMultipart("alternative")
    msg["From"] = formataddr(("三桶策略系统", user))
    msg["To"] = to
    msg["Subject"] = subject
    # HTML 版本：简单把 markdown 换行转成 <br>，保留代码块字体
    html = "<pre style='font-family: -apple-system, monospace; white-space: pre-wrap;'>" + \
           body.replace("<", "&lt;").replace(">", "&gt;") + "</pre>"
    msg.attach(MIMEText(body, "plain", "utf-8"))
    msg.attach(MIMEText(html, "html", "utf-8"))

    try:
        if port == 465:
            server = smtplib.SMTP_SSL(host, port, timeout=15)
        else:
            server = smtplib.SMTP(host, port, timeout=15)
            server.starttls()
        server.login(user, pwd)
        server.sendmail(user, [t.strip() for t in to.split(",") if t.strip()], msg.as_string())
        server.quit()
        return True
    except Exception as e:
        print(f"[notifier] 邮件发送失败：{e}", file=sys.stderr)
        return False


# ============================================================
# 统一入口
# ============================================================

def notify(markdown: str, title: str, level: str = "P1",
           channels: Optional[List[str]] = None,
           dedup_key: Optional[str] = None) -> Dict[str, bool]:
    """按 level 推送到相应通道。

    level -> 默认通道：
        P0  → wecom + lark + email
        P1  → wecom + email
        P2  → email
        P3  → 不推
    显式指定 channels 会覆盖默认。
    """
    if channels is None:
        channels = {
            "P0": ["wecom", "lark", "email"],
            "P1": ["wecom", "email"],
            "P2": ["email"],
            "P3": [],
        }.get(level.upper(), ["email"])

    if not channels:
        return {}

    key = dedup_key or _dedup_key(markdown[:200])
    if _is_duplicate(key):
        print(f"[notifier] dedup hit ({key})，跳过", file=sys.stderr)
        return {c: False for c in channels}

    results: Dict[str, bool] = {}
    for ch in channels:
        if ch == "wecom":
            results[ch] = send_wecom(markdown, title)
        elif ch == "lark":
            results[ch] = send_lark(markdown, title)
        elif ch == "email":
            results[ch] = send_email(markdown, title)
        else:
            print(f"[notifier] 未知通道：{ch}", file=sys.stderr)
            results[ch] = False

    if any(results.values()):
        _mark_seen(key)
    return results


# ============================================================
# CLI
# ============================================================

def main() -> int:
    parser = argparse.ArgumentParser(description="推送 Markdown 报告到企微/飞书/邮箱")
    parser.add_argument("--report", type=Path, help="报告文件路径")
    parser.add_argument("--latest", action="store_true", help="推送 data/ 目录下最新报告")
    parser.add_argument("--title", type=str, default="", help="标题（默认取报告首行）")
    parser.add_argument("--level", type=str, default="P2",
                        choices=["P0", "P1", "P2", "P3"], help="等级决定通道")
    parser.add_argument("--channels", type=str, default="",
                        help="强制通道 (逗号分隔 wecom,lark,email)")
    parser.add_argument("--dry-run", action="store_true", help="只打印，不真发")
    args = parser.parse_args()

    path: Optional[Path] = args.report
    if args.latest and not path:
        path = latest_report()
    if not path or not path.exists():
        print("[notifier] 没有可用报告", file=sys.stderr)
        return 1

    content = path.read_text(encoding="utf-8")
    title = args.title or content.splitlines()[0].lstrip("# ").strip()

    channels = [c.strip() for c in args.channels.split(",") if c.strip()] or None

    if args.dry_run:
        print(f"[dry-run] title={title!r} level={args.level} channels={channels or 'default'}")
        print(f"---\n{content[:500]}...")
        return 0

    results = notify(content, title=title, level=args.level, channels=channels)
    print(json.dumps(results, ensure_ascii=False))
    return 0 if any(results.values()) or not results else 1


if __name__ == "__main__":
    sys.exit(main())
