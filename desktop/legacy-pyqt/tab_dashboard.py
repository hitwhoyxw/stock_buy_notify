"""Dashboard 页：任务运行面板。

功能：
- T1-T8 任务卡片网格，每张卡片可输入参数并运行
- 快捷按钮：风控+台账、候选池ABC
- 实时日志面板（终端风格）
"""
from __future__ import annotations

import time

from PyQt5.QtCore import Qt, pyqtSignal
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QGridLayout, QGroupBox,
    QPushButton, QLabel, QTextEdit, QLineEdit, QMessageBox, QFrame,
)

from engine import TASKS, TaskEngine, TaskInfo


# ============================================================
# 任务卡片
# ============================================================

class TaskCard(QFrame):
    """单个任务的卡片控件。"""
    run_requested = pyqtSignal(str, list)  # task_key, args

    def __init__(self, task: TaskInfo):
        super().__init__()
        self.task = task
        self.setFrameStyle(QFrame.StyledPanel | QFrame.Raised)
        self.setLineWidth(1)
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)
        lay.setContentsMargins(12, 8, 12, 8)

        # 标题行
        row = QHBoxLayout()
        title = QLabel(f"<b style='font-size:14px'>{self.task.key}</b>&nbsp; {self.task.name}")
        row.addWidget(title)
        row.addStretch()

        if self.task.needs_llm:
            tag = QLabel("LLM")
            tag.setStyleSheet(
                "background:#e74c3c;color:white;padding:2px 8px;"
                "border-radius:8px;font-size:11px;font-weight:bold")
            row.addWidget(tag)
        lay.addLayout(row)

        # 描述
        desc = QLabel(self.task.description)
        desc.setStyleSheet("color:#555;font-size:12px")
        desc.setWordWrap(True)
        lay.addWidget(desc)

        # 调度
        sched = QLabel(f"📅 {self.task.schedule}")
        sched.setStyleSheet("color:#999;font-size:11px")
        lay.addWidget(sched)

        # 参数输入
        self.args_edit = QLineEdit()
        self.args_edit.setPlaceholderText("参数（如 --bucket A）")
        self.args_edit.setStyleSheet("font-size:12px;padding:4px")
        if self.task.default_args:
            self.args_edit.setText(" ".join(self.task.default_args))
        lay.addWidget(self.args_edit)

        # 运行按钮
        self.btn = QPushButton("▶  运行")
        self.btn.setStyleSheet(
            "QPushButton{background:#3498db;color:white;border:none;"
            "padding:6px 16px;border-radius:4px;font-size:13px}"
            "QPushButton:hover{background:#2980b9}"
            "QPushButton:disabled{background:#bdc3c7}")
        self.btn.clicked.connect(self._on_run)
        lay.addWidget(self.btn)

        # 状态
        self.status_lbl = QLabel("就绪")
        self.status_lbl.setStyleSheet("color:#999;font-size:11px")
        lay.addWidget(self.status_lbl)

    def _on_run(self):
        a = self.args_edit.text().strip().split()
        self.run_requested.emit(self.task.key, a)

    def set_running(self, running: bool):
        self.btn.setEnabled(not running)
        self.status_lbl.setText("运行中…" if running else "就绪")
        self.setStyleSheet("background:#fff3cd" if running else "")

    def set_done(self, ok: bool, msg: str):
        self.status_lbl.setText(msg)
        if ok:
            self.setStyleSheet("background:#d4edda")
        else:
            self.setStyleSheet("background:#f8d7da")


# ============================================================
# Dashboard 页
# ============================================================

class DashboardTab(QWidget):
    """任务运行面板。"""

    def __init__(self, engine: TaskEngine):
        super().__init__()
        self.engine = engine
        self.cards: dict[str, TaskCard] = {}
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)

        # ── 顶部操作栏 ──
        bar = QHBoxLayout()
        bar.addWidget(QLabel("<h2>📊 任务面板</h2>"))
        bar.addStretch()

        b_daily = QPushButton("▶ 风控+台账 (T1→T8)")
        b_daily.setStyleSheet(
            "QPushButton{background:#27ae60;color:white;border:none;"
            "padding:8px 16px;border-radius:4px;font-size:13px}"
            "QPushButton:hover{background:#229954}")
        b_daily.clicked.connect(lambda: self._run("T1"))
        bar.addWidget(b_daily)

        b_t6 = QPushButton("▶ 候选池 ABC")
        b_t6.setStyleSheet(
            "QPushButton{background:#8e44ad;color:white;border:none;"
            "padding:8px 16px;border-radius:4px;font-size:13px}"
            "QPushButton:hover{background:#7d3c98}")
        b_t6.clicked.connect(lambda: self._run("T6"))
        bar.addWidget(b_t6)

        lay.addLayout(bar)

        # ── 任务卡片网格 ──
        grid = QGridLayout()
        grid.setSpacing(10)
        cols = 4
        for i, (key, task) in enumerate(TASKS.items()):
            card = TaskCard(task)
            card.run_requested.connect(self._run)
            grid.addWidget(card, i // cols, i % cols)
            self.cards[key] = card
        lay.addLayout(grid)

        # ── 日志面板 ──
        log_group = QGroupBox("运行日志")
        log_lay = QVBoxLayout(log_group)

        log_bar = QHBoxLayout()
        log_bar.addWidget(QLabel("实时输出"))
        log_bar.addStretch()
        clr_btn = QPushButton("清除")
        clr_btn.setStyleSheet("padding:4px 12px")
        clr_btn.clicked.connect(self.log_clear)
        log_bar.addWidget(clr_btn)
        log_lay.addLayout(log_bar)

        self.log = QTextEdit()
        self.log.setReadOnly(True)
        self.log.setStyleSheet(
            "QTextEdit{background:#1e1e1e;color:#d4d4d4;"
            "font-family:Consolas,'Courier New',monospace;font-size:12px}")
        log_lay.addWidget(self.log)

        lay.addWidget(log_group)

    # ── 运行 ──

    def _run(self, key: str, args: list = None):
        if self.engine.is_running():
            QMessageBox.warning(self, "提示", "有任务正在运行，请等待完成。")
            return
        card = self.cards.get(key)
        if card:
            card.set_running(True)
        self._log(f"===== 启动 {key} =====")
        self.engine.run(key, args, on_output=self._log, on_finished=self._on_done)

    def _on_done(self, key: str, ok: bool, msg: str):
        card = self.cards.get(key)
        if card:
            card.set_running(False)
            card.set_done(ok, msg)
        self._log(f"===== {msg} =====\n")

    # ── 日志 ──

    def _log(self, text: str):
        ts = time.strftime("%H:%M:%S")
        self.log.append(f"[{ts}] {text}")
        c = self.log.textCursor()
        c.movePosition(c.End)
        self.log.setTextCursor(c)

    def log_clear(self):
        self.log.clear()

    def log_append(self, text: str):
        """外部调用：追加日志。"""
        self._log(text)
