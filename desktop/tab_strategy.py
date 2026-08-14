"""策略管理页：定义 买入/持有/卖出 建议（指标 + 条件 + 阈值 + 动作）。

策略可被监控自选页的股票引用（0 个或多个），
由 MonitorEngine 后台周期判断，触发后弹窗 + 邮件提醒。
"""
from __future__ import annotations

import pandas as pd
from PyQt5.QtCore import Qt
from PyQt5.QtGui import QColor
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QTableWidget,
    QTableWidgetItem, QHeaderView, QMessageBox, QDialog, QFormLayout,
    QComboBox, QDoubleSpinBox, QLineEdit,
)

from monitor import INDICATORS
from watchlist_store import WatchlistStore, STRATEGY_TYPES

TYPE_NAMES = {v: k for k, v in STRATEGY_TYPES.items()}  # "买入建议" -> "buy"
TYPE_COLORS = {"buy": "#27ae60", "hold": "#2980b9", "sell": "#e74c3c"}

OPERATORS = {
    "<": "小于 (<)",
    "<=": "小于等于 (<=)",
    ">": "大于 (>)",
    ">=": "大于等于 (>=)",
}
OP_BY_LABEL = {v: k for k, v in OPERATORS.items()}

# 指标示例值（对话框里做即时预览/自检提示用不到，留空即可）
INDICATOR_ITEMS = [(f"{label}  [{key}]", key)
                   for key, label in INDICATORS.items()]


class StrategyDialog(QDialog):
    """新建 / 编辑策略。strategy 为 None 时是新建模式。"""

    def __init__(self, parent=None, store: WatchlistStore = None,
                 strategy: dict = None):
        super().__init__(parent)
        self.store = store
        self.strategy = strategy
        self.setWindowTitle("编辑策略" if strategy else "➕ 新建策略")
        self.setMinimumWidth(480)
        self._build()
        if strategy:
            self._fill(strategy)

    def _build(self):
        form = QFormLayout()

        self.name_edit = QLineEdit()
        self.name_edit.setPlaceholderText("如：跌破MA60减仓")
        form.addRow("策略名称:", self.name_edit)

        self.type_combo = QComboBox()
        for key, label in STRATEGY_TYPES.items():
            self.type_combo.addItem(label, key)
        form.addRow("建议类型:", self.type_combo)

        self.ind_combo = QComboBox()
        for label, key in INDICATOR_ITEMS:
            self.ind_combo.addItem(label, key)
        form.addRow("监控指标:", self.ind_combo)

        self.op_combo = QComboBox()
        for op, label in OPERATORS.items():
            self.op_combo.addItem(label, op)
        form.addRow("触发条件:", self.op_combo)

        self.threshold_spin = QDoubleSpinBox()
        self.threshold_spin.setRange(-9999, 999999)
        self.threshold_spin.setDecimals(2)
        self.threshold_spin.setValue(0)
        form.addRow("阈值:", self.threshold_spin)

        self.action_edit = QLineEdit()
        self.action_edit.setPlaceholderText("如：现价跌破MA60，建议减仓1/3观察")
        form.addRow("触发后建议:", self.action_edit)

        self.prio_combo = QComboBox()
        self.prio_combo.addItems(["P0", "P1", "P2", "P3"])
        self.prio_combo.setCurrentText("P1")
        form.addRow("优先级:", self.prio_combo)

        btns = QHBoxLayout()
        ok_btn = QPushButton("保存")
        ok_btn.setStyleSheet("padding:6px 22px")
        ok_btn.clicked.connect(self.accept)
        cancel_btn = QPushButton("取消")
        cancel_btn.setStyleSheet("padding:6px 22px")
        cancel_btn.clicked.connect(self.reject)
        btns.addStretch()
        btns.addWidget(ok_btn)
        btns.addWidget(cancel_btn)

        lay = QVBoxLayout(self)
        lay.addLayout(form)
        lay.addLayout(btns)

    def _fill(self, s: dict):
        self.name_edit.setText(str(s.get("name", "")))
        self.type_combo.setCurrentIndex(
            max(0, self.type_combo.findData(str(s.get("type", "sell")))))
        self.ind_combo.setCurrentIndex(
            max(0, self.ind_combo.findData(str(s.get("indicator", "")))))
        self.op_combo.setCurrentIndex(
            max(0, self.op_combo.findData(str(s.get("operator", "<")))))
        try:
            self.threshold_spin.setValue(float(s.get("threshold", 0)))
        except (ValueError, TypeError):
            pass
        self.action_edit.setText(str(s.get("action", "")))
        self.prio_combo.setCurrentText(str(s.get("priority", "P1")))

    def accept(self):
        if not self.name_edit.text().strip():
            QMessageBox.warning(self, "缺少名称", "请填写策略名称。")
            return
        if not self.action_edit.text().strip():
            QMessageBox.warning(self, "缺少建议", "请填写触发后的建议动作。")
            return
        super().accept()

    def get_record(self) -> dict:
        return {
            "name": self.name_edit.text().strip(),
            "type": self.type_combo.currentData(),
            "indicator": self.ind_combo.currentData(),
            "operator": self.op_combo.currentData(),
            "threshold": f"{self.threshold_spin.value():g}",
            "action": self.action_edit.text().strip(),
            "priority": self.prio_combo.currentText(),
        }


class StrategyTab(QWidget):
    """策略列表 + 增删改 + 启停。"""

    def __init__(self, store: WatchlistStore):
        super().__init__()
        self.store = store
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)

        bar = QHBoxLayout()
        bar.addWidget(QLabel("<h2>🧭 策略管理</h2>"))
        bar.addStretch()

        add_btn = QPushButton("➕ 新建策略")
        add_btn.setStyleSheet("padding:6px 16px")
        add_btn.clicked.connect(self._on_add)
        bar.addWidget(add_btn)

        edit_btn = QPushButton("✏️ 编辑")
        edit_btn.setStyleSheet("padding:6px 16px")
        edit_btn.clicked.connect(self._on_edit)
        bar.addWidget(edit_btn)

        toggle_btn = QPushButton("⏯ 启用/禁用")
        toggle_btn.setStyleSheet("padding:6px 16px")
        toggle_btn.clicked.connect(self._on_toggle)
        bar.addWidget(toggle_btn)

        del_btn = QPushButton("🗑 删除")
        del_btn.setStyleSheet("padding:6px 16px")
        del_btn.clicked.connect(self._on_delete)
        bar.addWidget(del_btn)

        lay.addLayout(bar)

        self.hint_lbl = QLabel(
            "策略 = 指标 + 条件 + 阈值 → 建议动作。在「监控自选」页给股票应用策略后，"
            "由监控引擎周期判断并在触发时弹窗 + 邮件提醒。")
        self.hint_lbl.setStyleSheet("color:#666;font-size:12px;padding:4px")
        self.hint_lbl.setWordWrap(True)
        lay.addWidget(self.hint_lbl)

        self.table = QTableWidget()
        self.table.setAlternatingRowColors(True)
        self.table.setSelectionBehavior(QTableWidget.SelectRows)
        self.table.setSelectionMode(QTableWidget.SingleSelection)
        self.table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.table.verticalHeader().setDefaultSectionSize(30)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.doubleClicked.connect(lambda _: self._on_edit())
        lay.addWidget(self.table)

        self._load()

    def showEvent(self, e):
        super().showEvent(e)
        self._load()

    # ── 渲染 ──

    def _load(self):
        df = self.store.list_strategies()
        self.table.setRowCount(0)
        self.table.setColumnCount(8)
        self.table.setHorizontalHeaderLabels(
            ["ID", "名称", "类型", "指标", "条件", "建议动作", "优先级", "状态"])

        if df.empty:
            return
        for _, row in df.iterrows():
            r = self.table.rowCount()
            self.table.insertRow(r)
            ind_key = str(row["indicator"])
            ind_label = INDICATORS.get(ind_key, ind_key)
            enabled = str(row["enabled"]) == "1"
            stype = str(row["type"])

            vals = [
                str(row["id"]),
                str(row["name"]),
                STRATEGY_TYPES.get(stype, stype),
                ind_label,
                f"{str(row['operator'])} {str(row['threshold'])}",
                str(row["action"]),
                str(row["priority"]),
                "✅ 启用" if enabled else "⏸ 禁用",
            ]
            for c, v in enumerate(vals):
                item = QTableWidgetItem(v)
                if c in (0, 2, 4, 6, 7):
                    item.setTextAlignment(Qt.AlignCenter)
                if c == 2:
                    item.setForeground(QColor(TYPE_COLORS.get(stype, "#333")))
                if c == 7 and not enabled:
                    item.setForeground(QColor("#999"))
                self.table.setItem(r, c, item)

        self.table.resizeColumnsToContents()
        if self.table.columnCount() > 5:
            self.table.horizontalHeader().setSectionResizeMode(
                5, QHeaderView.Stretch)

    # ── 槽函数 ──

    def _current_sid(self) -> str:
        row = self.table.currentRow()
        if row < 0:
            return ""
        item = self.table.item(row, 0)
        return item.text() if item else ""

    def _on_add(self):
        dlg = StrategyDialog(self, self.store)
        if dlg.exec_() != QDialog.Accepted:
            return
        sid = self.store.add_strategy(dlg.get_record())
        if sid:
            self._load()
        else:
            QMessageBox.critical(self, "失败", "写入 strategies.csv 失败。")

    def _on_edit(self):
        sid = self._current_sid()
        if not sid:
            QMessageBox.information(self, "提示", "请先选中一个策略。")
            return
        df = self.store.list_strategies()
        match = df[df["id"].astype(str) == sid]
        if match.empty:
            return
        dlg = StrategyDialog(self, self.store, match.iloc[0].to_dict())
        if dlg.exec_() != QDialog.Accepted:
            return
        if self.store.update_strategy(sid, dlg.get_record()):
            self._load()
        else:
            QMessageBox.critical(self, "失败", "更新 strategies.csv 失败。")

    def _on_toggle(self):
        sid = self._current_sid()
        if not sid:
            QMessageBox.information(self, "提示", "请先选中一个策略。")
            return
        self.store.toggle_strategy(sid)
        self._load()

    def _on_delete(self):
        sid = self._current_sid()
        if not sid:
            QMessageBox.information(self, "提示", "请先选中一个策略。")
            return
        if QMessageBox.question(
                self, "确认删除",
                f"确定删除策略 {sid} 吗？\n已应用该策略的股票会自动解除引用。"
        ) != QMessageBox.Yes:
            return
        if self.store.delete_strategy(sid):
            self._load()
