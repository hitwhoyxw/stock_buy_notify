"""通道连通性测试。配好 .env / GitHub Secrets 后手动运行，验证三条推送链路。

用法：
    # 本地用 .env 文件
    set -a && source .env && set +a
    python scripts/test_channels.py

    # 只测某个通道
    python scripts/test_channels.py --channel wecom
    python scripts/test_channels.py --channel lark
    python scripts/test_channels.py --channel email
"""
from __future__ import annotations

import argparse
import datetime as dt
import os
import sys

# 确保 scripts/ 在 PYTHONPATH
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from lib.notifier import send_wecom, send_lark, send_email


def _test_body() -> str:
    now = dt.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    return (
        f"### 🔔 三桶策略系统 · 通道测试\n\n"
        f"**时间**：{now}\n\n"
        f"如果你能看到这条消息，说明推送链路连通。\n\n"
        f"---\n"
        f"- P0 级别（紧急止损）→ 企微 + 飞书 + 邮件\n"
        f"- P1 级别（信号触发）→ 企微 + 邮件\n"
        f"- P2 级别（日报）→ 邮件\n"
    )


def test_wecom() -> bool:
    key = os.environ.get("WECOM_BOT_KEY", "")
    if not key:
        print("❌ WECOM_BOT_KEY 未设置，跳过企微测试")
        return False
    print(f"  WECOM_BOT_KEY = {key[:8]}...（前 8 位）")
    ok = send_wecom(_test_body(), title="通道测试")
    print(f"  {'✅ 企微推送成功' if ok else '❌ 企微推送失败'}")
    return ok


def test_lark() -> bool:
    url = os.environ.get("LARK_WEBHOOK", "")
    if not url:
        print("❌ LARK_WEBHOOK 未设置，跳过飞书测试")
        return False
    print(f"  LARK_WEBHOOK = {url[:30]}...（前 30 字符）")
    ok = send_lark(_test_body(), title="通道测试")
    print(f"  {'✅ 飞书推送成功' if ok else '❌ 飞书推送失败'}")
    return ok


def test_email() -> bool:
    host = os.environ.get("SMTP_HOST", "")
    user = os.environ.get("SMTP_USER", "")
    to = os.environ.get("SMTP_TO", user)
    if not (host and user):
        print("❌ SMTP_HOST / SMTP_USER 未设置，跳过邮件测试")
        return False
    print(f"  SMTP_HOST = {host}")
    print(f"  SMTP_USER = {user}")
    print(f"  SMTP_TO   = {to}")
    ok = send_email(_test_body(), subject="[三桶策略] 通道连通性测试")
    print(f"  {'✅ 邮件发送成功' if ok else '❌ 邮件发送失败'}")
    return ok


def main() -> int:
    parser = argparse.ArgumentParser(description="推送通道连通性测试")
    parser.add_argument("--channel", choices=["wecom", "lark", "email"],
                        help="只测试指定通道（不指定则全测）")
    args = parser.parse_args()

    print("=" * 50)
    print("三桶策略系统 · 推送通道连通性测试")
    print("=" * 50)

    results = {}

    if not args.channel or args.channel == "wecom":
        print("\n[1/3] 企业微信群机器人")
        results["wecom"] = test_wecom()

    if not args.channel or args.channel == "lark":
        print("\n[2/3] 飞书自定义机器人")
        results["lark"] = test_lark()

    if not args.channel or args.channel == "email":
        print("\n[3/3] 邮箱 SMTP")
        results["email"] = test_email()

    # 汇总
    print("\n" + "=" * 50)
    print("测试结果汇总：")
    all_ok = True
    for ch, ok in results.items():
        status = "✅ 通过" if ok else "❌ 未通过（请检查密钥配置）"
        print(f"  {ch:8s} → {status}")
        if not ok:
            all_ok = False

    if not results:
        print("  （没有可测试的通道）")
        return 1

    print("\n" + ("🎉 全部通道连通！" if all_ok else "⚠️ 部分通道未通过，请按下方清单检查"))

    if not all_ok:
        print("\n--- 配置检查清单 ---")
        print("""
1. 企业微信群机器人：
   - 在企微群 → 设置 → 添加群机器人 → 复制 webhook URL
   - 取 key=... 后面的 UUID 部分
   - 设置为环境变量 WECOM_BOT_KEY 或 GitHub Secret

2. 飞书机器人：
   - 飞书群 → 设置 → 群机器人 → 自定义机器人 → 复制完整 webhook URL
   - 完整 URL 格式 https://open.feishu.cn/open-apis/bot/v2/hook/xxxx
   - 设置为 LARK_WEBHOOK

3. 邮箱 SMTP：
   - QQ邮箱：SMTP_HOST=smtp.qq.com SMTP_PORT=465 SMTP_PASS=授权码（非登录密码）
   - 163邮箱：SMTP_HOST=smtp.163.com SMTP_PORT=465 SMTP_PASS=授权码
   - SMTP_TO 为收件人，支持逗号分隔多人

4. GitHub Secrets 配置路径：
   仓库 → Settings → Secrets and variables → Actions → New repository secret
   需要配置的 secrets：
     TUSHARE_TOKEN
     WECOM_BOT_KEY
     LARK_WEBHOOK
     SMTP_HOST
     SMTP_PORT
     SMTP_USER
     SMTP_PASS
     SMTP_TO
""")

    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
