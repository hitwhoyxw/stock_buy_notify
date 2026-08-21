"""LLM 分析结果页：渲染 data/skill_output_*.md（三档全量分析等）。

功能：
- 左侧文件列表：T4C/T5/T6A/T6B/T6C 等全部 skill_output 产出（含未生成标记）
- 右侧 QTextBrowser：Markdown 渲染成 HTML（标题/表格/加粗/引用/列表），
  推荐/中立/不推荐三档标题自动着色
- 刷新按钮
"""
from __future__ import annotations

import os
import re
import time

from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QSplitter, QListWidget,
    QListWidgetItem, QTextBrowser, QPushButton, QLabel,
)

from engine import DataManager


def _esc(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def _inline(s: str) -> str:
    """行内格式：加粗 / 行内代码。"""
    s = _esc(s)
    s = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", s)
    s = re.sub(r"`(.+?)`", r"<code>\1</code>", s)
    return s


def _tier_color(text: str) -> str:
    """三档标题着色：不推荐红 / 推荐 绿 / 中立 橙。"""
    if "不推荐" in text or "REJECT" in text.upper():
        return "color:#c0392b"
    if "推荐" in text:
        return "color:#1e8449"
    if "中立" in text:
        return "color:#b9770e"
    return ""


def md_to_html(text: str) -> str:
    """轻量 Markdown → HTML（标题/表格/引用/列表/分隔线），不依赖第三方库。"""
    lines = text.split("\n")
    html: list = []
    in_table = False
    in_list = False

    def close_list():
        nonlocal in_list
        if in_list:
            html.append("</ul>")
            in_list = False

    def close_table():
        nonlocal in_table
        if in_table:
            html.append("</table>")
            in_table = False

    def is_sep_row(s: str) -> bool:
        body = s.strip().replace("|", "").replace(" ", "")
        return bool(body) and set(body) <= set("-:")

    for i, ln in enumerate(lines):
        raw = ln.rstrip()

        # ── 表格行 ──
        if raw.strip().startswith("|"):
            cells = [c.strip() for c in raw.strip().strip("|").split("|")]
            if not in_table:
                close_list()
                html.append("<table>")
                in_table = True
            if is_sep_row(raw):
                continue  # 跳过 |---|---| 分隔行
            # 表头行 = 下一行是分隔行（lookahead，连续相邻表格也正确）
            nxt = lines[i + 1].strip() if i + 1 < len(lines) else ""
            tag = "th" if is_sep_row(nxt) else "td"
            html.append("<tr>" + "".join(
                f"<{tag}>{_inline(c)}</{tag}>" for c in cells) + "</tr>")
            continue
        close_table()

        if not raw.strip():
            close_list()
            continue

        # ── 标题 ──
        m = re.match(r"^(#{1,4})\s+(.*)$", raw)
        if m:
            close_list()
            level = len(m.group(1))
            title = m.group(2)
            color = _tier_color(title)
            style = f" style='{color}'" if color else ""
            html.append(f"<h{level}{style}>{_inline(title)}</h{level}>")
            continue

        # ── 引用 ──
        if raw.startswith(">"):
            close_list()
            html.append(f"<p class='quote'>{_inline(raw.lstrip('> ').strip())}</p>")
            continue

        # ── 分隔线 ──
        if re.match(r"^-{3,}$", raw.strip()):
            close_list()
            html.append("<hr>")
            continue

        # ── 列表 ──
        m = re.match(r"^\s*[-*]\s+(.*)$", raw)
        if m:
            if not in_list:
                html.append("<ul>")
                in_list = True
            html.append(f"<li>{_inline(m.group(1))}</li>")
            continue
        close_list()

        # ── 普通段落 ──
        html.append(f"<p>{_inline(raw)}</p>")

    close_table()
    close_list()
    return "".join(html)


_CSS = """
body { font-family: 'Microsoft YaHei'; font-size: 13px; color: #212529; }
h1 { font-size: 18px; border-bottom: 2px solid #495057; padding-bottom: 4px; }
h2 { font-size: 15px; margin-top: 14px; }
h3 { font-size: 13px; margin-top: 10px; }
h4 { font-size: 12px; }
table { border-collapse: collapse; margin: 6px 0; }
th { background: #e9ecef; border: 1px solid #adb5bd; padding: 2px 6px;
     font-size: 11px; }
td { border: 1px solid #dee2e6; padding: 2px 6px; font-size: 11px; }
p { margin: 4px 0; }
.quote { color: #6c757d; background: #f8f9fa; padding: 4px 8px;
         border-left: 3px solid #adb5bd; }
hr { border: none; border-top: 1px solid #dee2e6; }
ul { margin: 4px 0; }
li { margin: 2px 0 2px 18px; }
code { background: #f1f3f5; padding: 0 3px; }
"""


class AnalysisTab(QWidget):
    """LLM 分析结果查看页。"""

    def __init__(self, dm: DataManager):
        super().__init__()
        self.dm = dm
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)

        # ── 顶部栏 ──
        bar = QHBoxLayout()
        bar.addWidget(QLabel("<h2>🧠 LLM 分析结果</h2>"))
        bar.addStretch()

        self.info_lbl = QLabel("")
        self.info_lbl.setStyleSheet("color:#666;font-size:12px;padding:4px")
        bar.addWidget(self.info_lbl)

        refresh_btn = QPushButton("🔄 刷新")
        refresh_btn.setStyleSheet("padding:6px 14px")
        refresh_btn.clicked.connect(self._load_list)
        bar.addWidget(refresh_btn)
        lay.addLayout(bar)

        # ── 双栏 ──
        splitter = QSplitter(Qt.Horizontal)

        self.file_list = QListWidget()
        self.file_list.setMinimumWidth(230)
        self.file_list.itemClicked.connect(self._on_select)
        splitter.addWidget(self.file_list)

        self.view = QTextBrowser()
        self.view.document().setDefaultStyleSheet(_CSS)
        self.view.setOpenExternalLinks(False)
        splitter.addWidget(self.view)

        splitter.setSizes([260, 900])
        lay.addWidget(splitter)

    def showEvent(self, e):
        super().showEvent(e)
        self._load_list()

    # ── 文件列表 ──

    def _load_list(self):
        self.file_list.clear()
        self.view.setHtml("<p style='color:#6c757d'>← 选择左侧文件查看分析结果</p>")

        # SKILLS 定义顺序优先，再补 glob 到的其他 skill_output 文件
        entries = []          # [(显示名, 文件名)]
        seen = set()
        for key, info in DataManager.SKILLS.items():
            fname = info["output"]
            seen.add(fname)
            entries.append((f"{key} — {info['label']}", fname))

        try:
            for f in sorted(os.listdir(self.dm.data_dir)):
                if f.startswith("skill_output_") and f.endswith(".md") and f not in seen:
                    seen.add(f)
                    entries.append((f, f))
        except OSError:
            pass

        has_any = False
        for label, fname in entries:
            path = os.path.join(self.dm.data_dir, fname)
            if os.path.exists(path):
                mtime = time.strftime("%m-%d %H:%M",
                                      time.localtime(os.path.getmtime(path)))
                item = QListWidgetItem(f"📄 {label}\n   {fname}  |  {mtime}")
                has_any = True
            else:
                item = QListWidgetItem(f"◻ {label}\n   {fname}  |  未生成")
                item.setForeground(Qt.gray)
            item.setData(Qt.UserRole, fname)
            self.file_list.addItem(item)

        if not has_any:
            self.view.setHtml(
                "<p style='color:#6c757d'>还没有任何 skill_output 文件。<br>"
                "先在「任务面板」跑 T4/T5/T6 生成 skill_input，再到"
                "「LLM 桥接」粘贴 LLM 分析结果并保存。</p>")

        # 自动选中第一个已生成的文件
        for i in range(self.file_list.count()):
            it = self.file_list.item(i)
            if os.path.exists(os.path.join(self.dm.data_dir, it.data(Qt.UserRole))):
                self.file_list.setCurrentRow(i)
                self._on_select(it)
                break

    # ── 渲染 ──

    def _on_select(self, item):
        if not item:
            return
        fname = item.data(Qt.UserRole)
        path = os.path.join(self.dm.data_dir, fname)
        if not os.path.exists(path):
            self.view.setHtml(
                f"<p style='color:#6c757d'>{_esc(fname)} 尚未生成。</p>")
            self.info_lbl.setText("")
            return

        try:
            with open(path, "r", encoding="utf-8") as f:
                md = f.read()
        except Exception as e:
            self.view.setHtml(f"<p style='color:#c0392b'>读取失败: {_esc(str(e))}</p>")
            return

        self.view.setHtml(md_to_html(md))
        size = os.path.getsize(path)
        mtime = time.strftime("%Y-%m-%d %H:%M",
                              time.localtime(os.path.getmtime(path)))
        self.info_lbl.setText(f"{fname}  |  {size:,} bytes  |  {mtime}")
