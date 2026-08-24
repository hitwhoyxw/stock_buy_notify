"""LLM 桥接页：skill_input → LLM 分析 → skill_output。

三种使用模式：
1. 手动模式：复制 skill_input → 粘贴到 Qoder 对话 → 复制 LLM 回复 → 粘贴回 skill_output
2. 文件模式：从文件导入 skill_output（外部 agent 写入）
3. API 模式（预留）：填入 API Key 后直接调用（未来扩展）
"""
from __future__ import annotations

from PyQt5.QtCore import Qt, pyqtSignal
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QSplitter, QGroupBox,
    QPushButton, QLabel, QTextEdit, QComboBox, QLineEdit,
    QMessageBox, QFileDialog, QApplication,
)

from engine import DataManager, TaskEngine, TASKS


class LLMBridgeTab(QWidget):
    """LLM 桥接页面。"""

    def __init__(self, dm: DataManager, engine: TaskEngine):
        super().__init__()
        self.dm = dm
        self.engine = engine
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)

        # ── 顶部操作栏 ──
        bar = QHBoxLayout()
        bar.addWidget(QLabel("<h2>🤖 LLM 桥接</h2>"))

        bar.addWidget(QLabel("Skill:"))
        self.skill_combo = QComboBox()
        for key, info in DataManager.SKILLS.items():
            self.skill_combo.addItem(f"{key} — {info['label']}", key)
        self.skill_combo.currentIndexChanged.connect(self._load)
        self.skill_combo.setStyleSheet("padding:4px")
        bar.addWidget(self.skill_combo)

        bar.addStretch()

        # 生成输入按钮
        self.gen_btn = QPushButton("⚡ 生成输入（运行脚本）")
        self.gen_btn.setStyleSheet(
            "QPushButton{background:#e67e22;color:white;border:none;"
            "padding:8px 16px;border-radius:4px}"
            "QPushButton:hover{background:#d35400}")
        self.gen_btn.clicked.connect(self._gen_input)
        bar.addWidget(self.gen_btn)

        lay.addLayout(bar)

        # ── 双栏 Splitter ──
        splitter = QSplitter(Qt.Horizontal)

        # 左栏：skill_input（只读）
        left = QWidget()
        left_lay = QVBoxLayout(left)
        left_lay.setContentsMargins(0, 0, 0, 0)

        left_bar = QHBoxLayout()
        left_bar.addWidget(QLabel("<b>📥 LLM 输入</b> (skill_input)"))
        left_bar.addStretch()
        self.input_info = QLabel("")
        self.input_info.setStyleSheet("color:#999;font-size:11px")
        left_bar.addWidget(self.input_info)

        copy_btn = QPushButton("📋 复制到剪贴板")
        copy_btn.setStyleSheet("padding:4px 12px")
        copy_btn.clicked.connect(self._copy_input)
        left_bar.addWidget(copy_btn)

        reload_btn = QPushButton("🔄 重新加载")
        reload_btn.setStyleSheet("padding:4px 12px")
        reload_btn.clicked.connect(self._load)
        left_bar.addWidget(reload_btn)
        left_lay.addLayout(left_bar)

        self.input_text = QTextEdit()
        self.input_text.setReadOnly(True)
        self.input_text.setStyleSheet(
            "QTextEdit{background:#fafafa;font-family:Consolas,monospace;font-size:12px}")
        left_lay.addWidget(self.input_text)
        splitter.addWidget(left)

        # 右栏：skill_output（可编辑）
        right = QWidget()
        right_lay = QVBoxLayout(right)
        right_lay.setContentsMargins(0, 0, 0, 0)

        right_bar = QHBoxLayout()
        right_bar.addWidget(QLabel("<b>📤 LLM 输出</b> (skill_output)"))
        right_bar.addStretch()
        self.output_info = QLabel("")
        self.output_info.setStyleSheet("color:#999;font-size:11px")
        right_bar.addWidget(self.output_info)

        import_btn = QPushButton("📂 从文件导入")
        import_btn.setStyleSheet("padding:4px 12px")
        import_btn.clicked.connect(self._import_file)
        right_bar.addWidget(import_btn)

        save_btn = QPushButton("💾 保存")
        save_btn.setStyleSheet(
            "QPushButton{background:#27ae60;color:white;border:none;"
            "padding:4px 12px;border-radius:4px}"
            "QPushButton:hover{background:#229954}")
        save_btn.clicked.connect(self._save_output)
        right_bar.addWidget(save_btn)
        right_lay.addLayout(right_bar)

        self.output_text = QTextEdit()
        self.output_text.setStyleSheet(
            "QTextEdit{font-family:Consolas,monospace;font-size:12px}")
        self.output_text.setPlaceholderText(
            "将 LLM（Qoder）的分析结果粘贴到这里，然后点击「保存」。\n"
            "或点击「从文件导入」加载外部 agent 写入的 skill_output 文件。")
        right_lay.addWidget(self.output_text)
        splitter.addWidget(right)

        splitter.setSizes([600, 600])
        lay.addWidget(splitter)

        # ── 使用提示 ──
        tip = QLabel(
            "💡 使用方式：\n"
            "  1. 点击「生成输入」运行脚本生成 skill_input\n"
            "  2. 点击「复制到剪贴板」→ 粘贴到 Qoder 对话框 → 获取 LLM 分析\n"
            "  3. 将 LLM 回复粘贴到右侧「LLM 输出」框 → 点击「保存」\n"
            "  4. 外部 agent（Qoder 定时任务）可直接写入 skill_output 文件，点击「重新加载」查看")
        tip.setStyleSheet(
            "background:#e8f4fd;padding:8px;border-radius:4px;font-size:12px;color:#555")
        tip.setWordWrap(True)
        lay.addWidget(tip)

    def showEvent(self, e):
        super().showEvent(e)
        self._load()

    def _current_skill(self) -> str:
        return self.skill_combo.currentData()

    def _load(self):
        skill = self._current_skill()
        if not skill:
            return

        # 加载 skill_input
        input_text = self.dm.skill_input_text(skill)
        self.input_text.setPlainText(input_text)
        info = DataManager.SKILLS[skill]
        mtime = self.dm.file_mtime(info["input"])
        size = self.dm.file_size(info["input"])
        self.input_info.setText(f"{size} | {mtime}" if mtime else "未生成")

        # 加载已有的 skill_output
        output_text = self.dm.skill_output_text(skill)
        self.output_text.setPlainText(output_text)
        mtime2 = self.dm.file_mtime(info["output"])
        size2 = self.dm.file_size(info["output"])
        self.output_info.setText(f"{size2} | {mtime2}" if mtime2 else "未保存")

    def _copy_input(self):
        text = self.input_text.toPlainText()
        if not text:
            QMessageBox.information(self, "提示", "skill_input 为空，请先生成。")
            return
        clipboard = QApplication.clipboard()
        clipboard.setText(text)
        QMessageBox.information(self, "已复制", "skill_input 已复制到剪贴板。\n\n请粘贴到 Qoder 对话框获取 LLM 分析。")

    def _save_output(self):
        skill = self._current_skill()
        text = self.output_text.toPlainText()
        if not text.strip():
            QMessageBox.warning(self, "提示", "输出内容为空。")
            return
        if self.dm.save_skill_output(skill, text):
            info = DataManager.SKILLS[skill]
            mtime = self.dm.file_mtime(info["output"])
            self.output_info.setText(f"已保存 | {mtime}")
            QMessageBox.information(self, "成功",
                f"已保存到 data/{info['output']}\n\n后续脚本可读取此文件继续处理。")
        else:
            QMessageBox.critical(self, "错误", "保存失败，请检查文件权限。")

    def _import_file(self):
        path, _ = QFileDialog.getOpenFileName(
            self, "导入 skill_output", "", "Markdown (*.md);;All Files (*)")
        if path:
            try:
                with open(path, "r", encoding="utf-8") as f:
                    self.output_text.setPlainText(f.read())
                QMessageBox.information(self, "已导入", f"已从 {path} 导入。")
            except Exception as e:
                QMessageBox.critical(self, "错误", str(e))

    def _gen_input(self):
        """运行对应的脚本生成 skill_input。"""
        skill = self._current_skill()
        if not skill:
            return

        # 找到对应的 task
        task = None
        for t in TASKS.values():
            if t.skill == skill:
                task = t
                break

        if not task:
            QMessageBox.warning(self, "提示", f"未找到 {skill} 对应的脚本。")
            return

        if self.engine.is_running():
            QMessageBox.warning(self, "提示", "有任务正在运行，请等待完成。")
            return

        self.gen_btn.setEnabled(False)
        self.gen_btn.setText("⏳ 运行中…")

        self.engine.run(
            task.key, list(task.default_args),
            on_output=lambda _: None,  # 不在 LLM 页显示日志
            on_finished=self._on_gen_done)

    def _on_gen_done(self, key: str, ok: bool, msg: str):
        self.gen_btn.setEnabled(True)
        self.gen_btn.setText("⚡ 生成输入（运行脚本）")

        if ok:
            self._load()  # 重新加载 skill_input
            QMessageBox.information(self, "生成完成",
                f"skill_input 已生成。\n\n请点击「复制到剪贴板」获取内容，\n粘贴到 Qoder 对话框进行 LLM 分析。")
        else:
            QMessageBox.warning(self, "生成失败", msg)
