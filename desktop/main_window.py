"""主窗口：QMainWindow + QTabWidget + 设置页 + 系统托盘。

Tab 布局：
  1. 📊 任务面板 — 运行 T1-T8 脚本
  2. 📋 候选池 — 三桶 CSV 表格
  3. 🤖 LLM 桥接 — skill_input/output 工作流
  4. 📈 报告 — 策略报告与日志查看
  5. ⚙️ 设置 — 项目路径、Python 路径、LLM API
"""
from __future__ import annotations

import os
import sys

from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtWidgets import (
    QMainWindow, QWidget, QTabWidget, QVBoxLayout, QHBoxLayout,
    QLabel, QLineEdit, QPushButton, QFileDialog, QMessageBox,
    QSystemTrayIcon, QMenu, QAction, QStyle, QGroupBox,
    QFormLayout, QSpinBox, QCheckBox, QTimeEdit,
)

from engine import (
    detect_project_root, detect_python, load_config, save_config,
    TaskEngine, DataManager,
)
from scheduler import TaskScheduler
from tab_dashboard import DashboardTab
from tab_candidates import CandidatesTab
from tab_llm import LLMBridgeTab
from tab_reports import ReportsTab
from tab_portfolio import PortfolioTab


class SettingsTab(QWidget):
    """设置页面。"""
    settings_changed = None  # signal placeholder

    def __init__(self, config: dict, on_save=None):
        super().__init__()
        self.config = config
        self.on_save = on_save
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)
        lay.addWidget(QLabel("<h2>⚙️ 设置</h2>"))

        # ── 路径配置 ──
        path_group = QGroupBox("路径配置")
        form = QFormLayout(path_group)

        self.root_edit = QLineEdit(self.config.get("project_root", detect_project_root()))
        root_btn = QPushButton("浏览…")
        root_btn.clicked.connect(self._browse_root)
        root_row = QHBoxLayout()
        root_row.addWidget(self.root_edit)
        root_row.addWidget(root_btn)
        form.addRow("项目根目录:", _wrap(root_row))

        self.python_edit = QLineEdit(self.config.get("python_exe", detect_python()))
        python_btn = QPushButton("浏览…")
        python_btn.clicked.connect(self._browse_python)
        py_row = QHBoxLayout()
        py_row.addWidget(self.python_edit)
        py_row.addWidget(python_btn)
        form.addRow("Python 路径:", _wrap(py_row))

        self.data_edit = QLineEdit(self.config.get("data_dir", ""))
        data_btn = QPushButton("浏览…")
        data_btn.clicked.connect(self._browse_data)
        d_row = QHBoxLayout()
        d_row.addWidget(self.data_edit)
        d_row.addWidget(data_btn)
        form.addRow("数据目录:", _wrap(d_row))

        lay.addWidget(path_group)

        # ── LLM 配置（预留） ──
        llm_group = QGroupBox("LLM API（预留，当前用 Qoder 对话）")
        llm_form = QFormLayout(llm_group)

        self.api_url = QLineEdit(self.config.get("llm_api_url", ""))
        self.api_url.setPlaceholderText("https://api.openai.com/v1/chat/completions（留空=用 Qoder）")
        llm_form.addRow("API URL:", self.api_url)

        self.api_key = QLineEdit(self.config.get("llm_api_key", ""))
        self.api_key.setPlaceholderText("sk-...（留空=手动模式）")
        self.api_key.setEchoMode(QLineEdit.Password)
        llm_form.addRow("API Key:", self.api_key)

        self.model = QLineEdit(self.config.get("llm_model", "gpt-4o"))
        llm_form.addRow("模型:", self.model)

        lay.addWidget(llm_group)

        # ── 自动刷新 ──
        auto_group = QGroupBox("自动刷新")
        auto_form = QFormLayout(auto_group)

        self.auto_refresh = QCheckBox("切换到候选池/报告页时自动刷新数据")
        self.auto_refresh.setChecked(self.config.get("auto_refresh", True))
        auto_form.addRow("", self.auto_refresh)

        self.refresh_interval = QSpinBox()
        self.refresh_interval.setRange(10, 600)
        self.refresh_interval.setValue(self.config.get("refresh_interval", 60))
        self.refresh_interval.setSuffix(" 秒")
        auto_form.addRow("刷新间隔:", self.refresh_interval)

        lay.addWidget(auto_group)

        # ── 内置定时器 ──
        sched_group = QGroupBox("内置定时器（本地自动跑任务）")
        sched_form = QFormLayout(sched_group)

        self.sched_enabled = QCheckBox("启用定时执行（工作日）")
        self.sched_enabled.setChecked(self.config.get("scheduler_enabled", False))
        sched_form.addRow("", self.sched_enabled)

        self.sched_time = QTimeEdit()
        self.sched_time.setDisplayFormat("HH:mm")
        default_time = self.config.get("scheduler_time", "16:30")
        try:
            h, m = map(int, default_time.split(":"))
            from PyQt5.QtCore import QTime
            self.sched_time.setTime(QTime(h, m))
        except Exception:
            pass
        sched_form.addRow("每日运行时间:", self.sched_time)

        self.sched_tasks = QLineEdit(self.config.get("scheduler_tasks_str", "T1 T8"))
        self.sched_tasks.setPlaceholderText("如 T1 T8")
        sched_form.addRow("运行任务:", self.sched_tasks)

        lay.addWidget(sched_group)

        # ── 保存按钮 ──
        save_btn = QPushButton("💾 保存设置")
        save_btn.setStyleSheet(
            "QPushButton{background:#27ae60;color:white;border:none;"
            "padding:10px 24px;border-radius:4px;font-size:14px}"
            "QPushButton:hover{background:#229954}")
        save_btn.clicked.connect(self._save)
        lay.addWidget(save_btn)

        lay.addStretch()

    def _browse_root(self):
        d = QFileDialog.getExistingDirectory(self, "选择项目根目录", self.root_edit.text())
        if d:
            self.root_edit.setText(d)

    def _browse_python(self):
        f, _ = QFileDialog.getOpenFileName(self, "选择 Python 可执行文件", "",
                                           "Python (python.exe);;All Files (*)")
        if f:
            self.python_edit.setText(f)

    def _browse_data(self):
        d = QFileDialog.getExistingDirectory(self, "选择数据目录", self.data_edit.text())
        if d:
            self.data_edit.setText(d)

    def _save(self):
        root = self.root_edit.text().strip()
        data_dir = self.data_edit.text().strip()
        if not data_dir:
            data_dir = os.path.join(root, "data")

        self.config["project_root"] = root
        self.config["python_exe"] = self.python_edit.text().strip()
        self.config["data_dir"] = data_dir
        self.config["llm_api_url"] = self.api_url.text().strip()
        self.config["llm_api_key"] = self.api_key.text().strip()
        self.config["llm_model"] = self.model.text().strip()
        self.config["auto_refresh"] = self.auto_refresh.isChecked()
        self.config["refresh_interval"] = self.refresh_interval.value()

        # 定时器配置
        self.config["scheduler_enabled"] = self.sched_enabled.isChecked()
        sched_time = self.sched_time.time()
        self.config["scheduler_time"] = f"{sched_time.hour():02d}:{sched_time.minute():02d}"
        tasks_str = self.sched_tasks.text().strip()
        self.config["scheduler_tasks_str"] = tasks_str
        self.config["scheduler_tasks"] = [t.strip() for t in tasks_str.split() if t.strip()] or ["T1", "T8"]

        save_config(self.config)

        if self.on_save:
            self.on_save(self.config)

        QMessageBox.information(self, "已保存", "设置已保存，即将刷新各页面。")

    def get_config(self) -> dict:
        return self.config


def _wrap(layout) -> QWidget:
    """将 QLayout 包装为 QWidget（QFormLayout 需要 Widget）。"""
    w = QWidget()
    w.setLayout(layout)
    return w


class MainWindow(QMainWindow):
    """主窗口。"""

    def __init__(self):
        super().__init__()
        self.config = load_config()

        # 确保关键配置有默认值
        root = self.config.get("project_root", detect_project_root())
        self.config.setdefault("project_root", root)
        self.config.setdefault("python_exe", detect_python())
        self.config.setdefault("data_dir", os.path.join(root, "data"))
        self.config.setdefault("scheduler_enabled", False)
        self.config.setdefault("scheduler_time", "16:30")
        self.config.setdefault("scheduler_tasks", ["T1", "T8"])

        # 初始化引擎和数据管理器
        self.engine = TaskEngine(
            self.config["project_root"], self.config["python_exe"])
        self.dm = DataManager(self.config["data_dir"])

        self._build_ui()
        self._build_tray()

        # 初始化定时器
        self.scheduler = TaskScheduler(self.engine, self.config)
        self.scheduler.status_message.connect(self._on_scheduler_status)
        self.scheduler.task_finished.connect(self._on_scheduler_task_done)

    def _build_ui(self):
        self.setWindowTitle("三桶策略系统 — 桌面端")
        self.resize(1280, 860)
        self.setMinimumSize(960, 640)

        # 全局样式
        self.setStyleSheet("""
            QMainWindow { background: #f5f6fa; }
            QTabWidget::pane { border: 1px solid #ddd; border-radius: 4px; }
            QTabBar::tab {
                padding: 8px 20px; font-size: 13px;
                border: 1px solid #ddd; border-bottom: none;
                border-top-left-radius: 4px; border-top-right-radius: 4px;
                background: #e9ecef; margin-right: 2px;
            }
            QTabBar::tab:selected { background: #fff; font-weight: bold; }
            QGroupBox { font-weight: bold; margin-top: 8px; }
            QGroupBox::title { left: 12px; padding: 0 4px; }
        """)

        # TabWidget
        tabs = QTabWidget()

        self.tab_dashboard = DashboardTab(self.engine)
        self.tab_portfolio = PortfolioTab(self.dm)
        self.tab_candidates = CandidatesTab(self.dm)
        self.tab_llm = LLMBridgeTab(self.dm, self.engine)
        self.tab_reports = ReportsTab(self.dm)
        self.tab_settings = SettingsTab(self.config, on_save=self._on_settings_saved)

        tabs.addTab(self.tab_dashboard, "📊 任务面板")
        tabs.addTab(self.tab_portfolio, "💼 持仓总览")
        tabs.addTab(self.tab_candidates, "📋 候选池")
        tabs.addTab(self.tab_llm, "🤖 LLM 桥接")
        tabs.addTab(self.tab_reports, "📈 报告")
        tabs.addTab(self.tab_settings, "⚙️ 设置")

        self.setCentralWidget(tabs)
        self.tabs = tabs

        # 状态栏
        self.statusBar().showMessage(
            f"项目: {self.config['project_root']}  |  "
            f"数据: {self.config['data_dir']}  |  "
            f"Python: {self.config['python_exe']}")

    def _build_tray(self):
        """系统托盘图标。"""
        self.tray = QSystemTrayIcon(self)
        self.tray.setIcon(self.style().standardIcon(QStyle.SP_ComputerIcon))
        self.tray.setToolTip("三桶策略系统")

        menu = QMenu()
        show_action = QAction("显示主窗口", self)
        show_action.triggered.connect(self.showNormal)
        menu.addAction(show_action)

        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self.close)
        menu.addAction(quit_action)

        self.tray.setContextMenu(menu)
        self.tray.activated.connect(
            lambda r: self.showNormal() if r == QSystemTrayIcon.DoubleClick else None)

    def show_tray(self):
        self.tray.show()

    def _on_settings_saved(self, cfg: dict):
        """设置保存后刷新引擎和数据管理器。"""
        self.engine.project_root = cfg["project_root"]
        self.engine.python_exe = cfg["python_exe"]
        self.dm.set_data_dir(cfg["data_dir"])

        # 重载定时器配置
        self.scheduler.reload_config(cfg)

        self.statusBar().showMessage(
            f"项目: {cfg['project_root']}  |  "
            f"数据: {cfg['data_dir']}  |  "
            f"Python: {cfg['python_exe']}")

        # 刷新当前页面
        idx = self.tabs.currentIndex()
        if idx == 1:
            self.tab_portfolio._load()
        elif idx == 2:
            self.tab_candidates._load()
        elif idx == 3:
            self.tab_llm._load()
        elif idx == 4:
            self.tab_reports._load_list()

    def _on_scheduler_status(self, msg: str):
        """定时器状态更新到状态栏。"""
        self.statusBar().showMessage(msg)

    def _on_scheduler_task_done(self, key: str, ok: bool):
        """定时器任务完成后刷新数据。"""
        if ok:
            self.tab_portfolio._load()

    def closeEvent(self, e):
        """关闭时最小化到托盘（不退出）。"""
        if hasattr(self, "tray") and self.tray.isVisible():
            self.hide()
            self.tray.showMessage("三桶策略系统", "已最小化到系统托盘，双击图标恢复。",
                                  QSystemTrayIcon.Information, 2000)
            e.ignore()
        else:
            e.accept()
