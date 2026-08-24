"""报告页：查看 data/report_*.md 和交易/信号日志。"""
from __future__ import annotations

import os
import time

from PyQt5.QtCore import Qt
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QSplitter, QListWidget,
    QListWidgetItem, QTextEdit, QPushButton, QLabel, QComboBox,
)

from engine import DataManager


class ReportsTab(QWidget):
    """报告查看页面。"""

    def __init__(self, dm: DataManager):
        super().__init__()
        self.dm = dm
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)

        # 顶部栏
        bar = QHBoxLayout()
        bar.addWidget(QLabel("<h2>📈 报告与日志</h2>"))
        bar.addStretch()

        self.type_combo = QComboBox()
        self.type_combo.addItems(["策略报告 (report_*.md)", "交易日志 (live_trade_log.csv)",
                                   "信号日志 (live_signal_log.csv)"])
        self.type_combo.currentIndexChanged.connect(self._load_list)
        self.type_combo.setStyleSheet("padding:4px")
        bar.addWidget(self.type_combo)

        refresh_btn = QPushButton("🔄 刷新")
        refresh_btn.setStyleSheet("padding:6px 14px")
        refresh_btn.clicked.connect(self._load_list)
        bar.addWidget(refresh_btn)
        lay.addLayout(bar)

        # 双栏
        splitter = QSplitter(Qt.Horizontal)

        # 左：文件列表
        self.file_list = QListWidget()
        self.file_list.setMinimumWidth(250)
        self.file_list.itemClicked.connect(self._on_select)
        splitter.addWidget(self.file_list)

        # 右：内容
        self.content = QTextEdit()
        self.content.setReadOnly(True)
        self.content.setStyleSheet(
            "QTextEdit{font-family:Consolas,monospace;font-size:12px;padding:8px}")
        splitter.addWidget(self.content)

        splitter.setSizes([300, 800])
        lay.addWidget(splitter)

    def showEvent(self, e):
        super().showEvent(e)
        self._load_list()

    def _load_list(self):
        self.file_list.clear()
        self.content.clear()

        idx = self.type_combo.currentIndex()

        if idx == 0:
            # 策略报告
            files = self.dm.list_reports()
            for f in files:
                name = os.path.basename(f)
                mtime = time.strftime("%m-%d %H:%M", time.localtime(os.path.getmtime(f)))
                item = QListWidgetItem(f"📄 {name}\n   {mtime}")
                item.setData(Qt.UserRole, f)
                self.file_list.addItem(item)

        elif idx == 1:
            # 交易日志
            path = os.path.join(self.dm.data_dir, "live_trade_log.csv")
            if os.path.exists(path):
                item = QListWidgetItem("📊 live_trade_log.csv")
                item.setData(Qt.UserRole, path)
                self.file_list.addItem(item)

        elif idx == 2:
            # 信号日志
            path = os.path.join(self.dm.data_dir, "live_signal_log.csv")
            if os.path.exists(path):
                item = QListWidgetItem("📡 live_signal_log.csv")
                item.setData(Qt.UserRole, path)
                self.file_list.addItem(item)

        # 自动选中第一项
        if self.file_list.count() > 0:
            self.file_list.setCurrentRow(0)
            self._on_select(self.file_list.item(0))

    def _on_select(self, item):
        if not item:
            return
        path = item.data(Qt.UserRole)
        if not path or not os.path.exists(path):
            return

        try:
            with open(path, "r", encoding="utf-8") as f:
                content = f.read()
        except Exception as e:
            content = f"读取失败: {e}"

        # 如果是 CSV，以表格形式展示
        if path.endswith(".csv"):
            self._render_csv(content, path)
        else:
            self.content.setPlainText(content)

    def _render_csv(self, content: str, path: str):
        """简易 CSV 渲染为 HTML 表格。"""
        lines = content.strip().split("\n")
        if not lines:
            self.content.setPlainText("")
            return

        html = ["<table border='1' cellspacing='0' cellpadding='4' style='border-collapse:collapse;font-size:11px'>"]
        for i, line in enumerate(lines):
            cells = line.split(",")
            tag = "th" if i == 0 else "td"
            bg = " style='background:#f0f0f0'" if i == 0 else ""
            html.append("<tr>")
            for cell in cells:
                html.append(f"<{tag}{bg}>{cell.strip()}</{tag}>")
            html.append("</tr>")
        html.append("</table>")
        self.content.setHtml("".join(html))
