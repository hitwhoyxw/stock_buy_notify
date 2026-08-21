"""策略管理页：定义 买入/持有/卖出 建议。

两种策略形态（同一张 strategies.csv）：
  · 简单策略：单一指标 + 操作符 + 阈值（indicator/operator/threshold 三列，
    存量策略零迁移继续可用）
  · 复合策略：condition 列存条件树 JSON —— AND/OR/NOT 嵌套、指标间比较
    （如 MA5>MA10）、形态类（均线多头排列）、交叉类（MACD/均线金叉死叉）

编辑对话框内置"条件构建器"（LeafEditor/GroupEditor 递归组件）：
分级下拉 + ＋按钮逐层添加，非技术用户无需手写 JSON。
保存前走 strategy_engine.validate_condition 结构自检。

策略可被监控自选页的股票引用，由 MonitorEngine 后台周期判断，
触发后弹窗 + 邮件提醒。
"""
from __future__ import annotations

import json

from PyQt5.QtCore import Qt, pyqtSignal
from PyQt5.QtGui import QColor
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QTableWidget,
    QTableWidgetItem, QHeaderView, QMessageBox, QDialog, QFormLayout,
    QComboBox, QDoubleSpinBox, QLineEdit, QSpinBox, QStackedWidget,
    QScrollArea, QFrame,
)

from monitor import INDICATORS
from strategy_engine import (
    INDICATOR_DEFS, MACD_FIELDS, StrategyConfigError,
    describe_condition, validate_condition,
)
from watchlist_store import WatchlistStore, STRATEGY_TYPES

TYPE_NAMES = {v: k for k, v in STRATEGY_TYPES.items()}  # "买入建议" -> "buy"
TYPE_COLORS = {"buy": "#27ae60", "hold": "#2980b9", "sell": "#e74c3c"}

# 简单模式（单指标 + 阈值）用的旧指标清单与操作符
OPERATORS = {
    "<": "小于 (<)",
    "<=": "小于等于 (<=)",
    ">": "大于 (>)",
    ">=": "大于等于 (>=)",
}
OP_BY_LABEL = {v: k for k, v in OPERATORS.items()}
INDICATOR_ITEMS = [(f"{label}  [{key}]", key)
                   for key, label in INDICATORS.items()]

# 条件树编辑器：指标清单 / 全量操作符（含交叉）
TREE_INDICATOR_ITEMS = [(f"{label}  [{key}]", key)
                        for key, (label, _, _) in INDICATOR_DEFS.items()]
TREE_OPERATORS = {
    "<": "<", "<=": "<=", ">": ">", ">=": ">=", "==": "==",
    "cross_up": "上穿(金叉)", "cross_down": "下穿(死叉)",
}
LOGIC_ITEMS = [("全部满足 (AND)", "and"), ("任一满足 (OR)", "or"),
               ("取反 (NOT)", "not")]
_MAX_GROUP_DEPTH = 6   # UI 嵌套上限（引擎允许 12，编辑器保留余量）


# ============================================================
# 条件构建器组件
# ============================================================

class ParamInputs(QWidget):
    """指标参数输入区：按 INDICATOR_DEFS 的 spec 动态生成（macd.field 下拉）。"""

    changed = pyqtSignal()

    def __init__(self, key: str, parent=None):
        super().__init__(parent)
        self._key = key
        self._boxes = {}  # 参数名 -> QComboBox|QSpinBox
        lay = QHBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(4)
        for name, (plabel, default, lo, hi) in INDICATOR_DEFS[key][1].items():
            if key == "macd" and name == "field":
                box = QComboBox()
                box.addItems(list(MACD_FIELDS))
                box.setCurrentText("dif")
                box.currentIndexChanged.connect(self.changed)
                w = box
            else:
                w = QSpinBox()
                w.setRange(lo, hi)
                w.setValue(int(default))
                w.valueChanged.connect(self.changed)
            lay.addWidget(QLabel(f"{plabel}:"))
            lay.addWidget(w)
            self._boxes[name] = w

    def get_params(self) -> dict:
        out = {}
        for name, w in self._boxes.items():
            out[name] = w.currentText() if isinstance(w, QComboBox) \
                else int(w.value())
        return out

    def set_params(self, params: dict):
        for name, w in self._boxes.items():
            if name not in params:
                continue
            if isinstance(w, QComboBox):
                idx = w.findText(str(params[name]).lower())
                if idx >= 0:
                    w.setCurrentIndex(idx)
            else:
                try:
                    w.setValue(int(params[name]))
                except (ValueError, TypeError):
                    pass


class LeafEditor(QFrame):
    """叶子条件行：指标[+参数] 操作符 值(常量|指标) ✕"""

    removeRequested = pyqtSignal(object)
    changed = pyqtSignal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setFrameShape(QFrame.Box)
        self.setStyleSheet(
            "LeafEditor{border:1px solid #cfd8dc;border-radius:4px;"
            "background:#fafafa}")
        lay = QHBoxLayout(self)
        lay.setContentsMargins(6, 4, 6, 4)
        lay.setSpacing(4)

        self.ind_combo = QComboBox()
        for label, key in TREE_INDICATOR_ITEMS:
            self.ind_combo.addItem(label, key)
        self.ind_combo.currentIndexChanged.connect(self._on_ind_changed)
        self.ind_combo.currentIndexChanged.connect(self.changed)
        lay.addWidget(self.ind_combo)

        self.param_host = QHBoxLayout()
        lay.addLayout(self.param_host)
        self._param_inputs: ParamInputs = None
        self._on_ind_changed()

        self.op_combo = QComboBox()
        for op, label in TREE_OPERATORS.items():
            self.op_combo.addItem(label, op)
        self.op_combo.currentIndexChanged.connect(self.changed)
        lay.addWidget(self.op_combo)

        self.val_kind = QComboBox()
        self.val_kind.addItems(["常量", "指标"])
        self.val_kind.currentIndexChanged.connect(self._on_val_kind)
        self.val_kind.currentIndexChanged.connect(self.changed)
        lay.addWidget(self.val_kind)

        self.val_const = QDoubleSpinBox()
        self.val_const.setRange(-999999, 999999)
        self.val_const.setDecimals(3)
        self.val_const.valueChanged.connect(self.changed)
        lay.addWidget(self.val_const)

        self.val_ind = QComboBox()
        for label, key in TREE_INDICATOR_ITEMS:
            self.val_ind.addItem(label, key)
        self.val_ind.currentIndexChanged.connect(self._on_val_ind_changed)
        self.val_ind.currentIndexChanged.connect(self.changed)
        lay.addWidget(self.val_ind)

        self.val_param_host = QHBoxLayout()
        lay.addLayout(self.val_param_host)
        self._val_param_inputs: ParamInputs = None
        self._on_val_ind_changed()
        self._on_val_kind()

        del_btn = QPushButton("✕")
        del_btn.setFixedWidth(28)
        del_btn.setToolTip("删除此条件")
        del_btn.clicked.connect(lambda: self.removeRequested.emit(self))
        lay.addWidget(del_btn)

    # ── 动态参数区 ──

    def _clear_layout(self, host: QHBoxLayout):
        while host.count():
            item = host.takeAt(0)
            w = item.widget()
            if w is not None:
                w.deleteLater()

    def _on_ind_changed(self, *_):
        self._clear_layout(self.param_host)
        self._param_inputs = ParamInputs(self.ind_combo.currentData())
        self._param_inputs.changed.connect(self.changed)
        self.param_host.addWidget(self._param_inputs)

    def _on_val_ind_changed(self, *_):
        self._clear_layout(self.val_param_host)
        self._val_param_inputs = ParamInputs(self.val_ind.currentData())
        self._val_param_inputs.changed.connect(self.changed)
        self._val_param_inputs.setVisible(self.val_kind.currentIndex() != 0)
        self.val_param_host.addWidget(self._val_param_inputs)

    def _on_val_kind(self, *_):
        is_const = self.val_kind.currentIndex() == 0
        self.val_const.setVisible(is_const)
        self.val_ind.setVisible(not is_const)
        self._val_param_inputs.setVisible(not is_const)

    # ── 节点收集 / 回填 ──

    def node(self) -> dict:
        if self.val_kind.currentIndex() == 0:
            value = self.val_const.value()
        else:
            value = {"indicator": self.val_ind.currentData(),
                     "params": self._val_param_inputs.get_params()}
        return {
            "indicator": self.ind_combo.currentData(),
            "params": self._param_inputs.get_params(),
            "operator": self.op_combo.currentData(),
            "value": value,
        }

    def set_node(self, n: dict):
        key = str(n.get("indicator", ""))
        idx = self.ind_combo.findData(key)
        if idx >= 0:
            self.ind_combo.setCurrentIndex(idx)   # 触发参数区重建
        self._param_inputs.set_params(n.get("params") or {})
        op = str(n.get("operator", ""))
        oi = self.op_combo.findData(op)
        if oi >= 0:
            self.op_combo.setCurrentIndex(oi)
        v = n.get("value")
        if isinstance(v, dict):
            self.val_kind.setCurrentIndex(1)
            vi = self.val_ind.findData(str(v.get("indicator", "")))
            if vi >= 0:
                self.val_ind.setCurrentIndex(vi)  # 触发值参数区重建
            self._val_param_inputs.set_params(v.get("params") or {})
        elif isinstance(v, (int, float, str)):
            self.val_kind.setCurrentIndex(0)
            try:
                self.val_const.setValue(float(v))
            except (ValueError, TypeError):
                pass


class GroupEditor(QFrame):
    """组合节点编辑器：逻辑(AND/OR/NOT) + 子节点列表 + ＋添加。

    depth=0 为根（无 ✕ 删除按钮）；每层子 GroupEditor depth+1。
    """

    removeRequested = pyqtSignal(object)
    changed = pyqtSignal()

    def __init__(self, depth: int = 0, parent=None):
        super().__init__(parent)
        self._depth = depth
        self._children = []   # List[LeafEditor|GroupEditor]
        self.setFrameShape(QFrame.Box)
        self.setStyleSheet(
            "GroupEditor{border:1px solid #b0bec5;border-radius:5px;"
            "background:#fff}")
        lay = QVBoxLayout(self)
        lay.setContentsMargins(8, 6, 8, 6)
        lay.setSpacing(4)

        top = QHBoxLayout()
        self.logic_combo = QComboBox()
        for label, key in LOGIC_ITEMS:
            self.logic_combo.addItem(label, key)
        self.logic_combo.currentIndexChanged.connect(self._on_logic)
        self.logic_combo.currentIndexChanged.connect(self.changed)
        top.addWidget(QLabel("逻辑:"))
        top.addWidget(self.logic_combo)
        top.addStretch()

        self.add_leaf_btn = QPushButton("＋ 条件")
        self.add_leaf_btn.clicked.connect(lambda: self._add_leaf())
        top.addWidget(self.add_leaf_btn)
        self.add_group_btn = QPushButton("＋ 条件组")
        self.add_group_btn.setEnabled(depth < _MAX_GROUP_DEPTH)
        self.add_group_btn.clicked.connect(lambda: self._add_group())
        top.addWidget(self.add_group_btn)
        if depth > 0:
            del_btn = QPushButton("✕")
            del_btn.setFixedWidth(28)
            del_btn.setToolTip("删除此条件组")
            del_btn.clicked.connect(lambda: self.removeRequested.emit(self))
            top.addWidget(del_btn)
        lay.addLayout(top)

        self.children_host = QVBoxLayout()
        lay.addLayout(self.children_host)

    # ── 子节点管理 ──

    def _add_leaf(self, node: dict = None) -> LeafEditor:
        leaf = LeafEditor()
        leaf.removeRequested.connect(self._remove_child)
        leaf.changed.connect(self.changed)
        self._children.append(leaf)
        self.children_host.addWidget(leaf)
        if node:
            leaf.set_node(node)
        self._sync_not_mode()
        self.changed.emit()
        return leaf

    def _add_group(self, node: dict = None) -> GroupEditor:
        grp = GroupEditor(self._depth + 1)
        grp.removeRequested.connect(self._remove_child)
        grp.changed.connect(self.changed)
        self._children.append(grp)
        self.children_host.addWidget(grp)
        if node:
            grp.set_node(node)
        self._sync_not_mode()
        self.changed.emit()
        return grp

    def _remove_child(self, w):
        if w in self._children:
            self._children.remove(w)
        self.children_host.removeWidget(w)
        w.deleteLater()
        self._sync_not_mode()
        self.changed.emit()

    def _on_logic(self, *_):
        # 切到 NOT：只保留第一个子节点（引擎要求 not 恰好 1 个子条件）
        if self.logic_combo.currentData() == "not":
            for w in list(self._children[1:]):
                self._remove_child(w)
        self._sync_not_mode()

    def _sync_not_mode(self):
        is_not = self.logic_combo.currentData() == "not"
        self.add_leaf_btn.setEnabled(not is_not or not self._children)
        self.add_group_btn.setEnabled(
            not is_not and self._depth < _MAX_GROUP_DEPTH)

    # ── 节点收集 / 回填 ──

    def node(self) -> dict:
        return {
            "logic": self.logic_combo.currentData(),
            "children": [w.node() for w in self._children],
        }

    def set_node(self, n: dict):
        logic = str(n.get("logic", "and")).lower()
        li = self.logic_combo.findData(logic)
        if li >= 0:
            self.logic_combo.setCurrentIndex(li)
        for c in (n.get("children") or []):
            if isinstance(c, dict) and "logic" in c:
                self._add_group(c)
            else:
                self._add_leaf(c)
        if self.logic_combo.currentData() == "not":
            for w in list(self._children[1:]):
                self._remove_child(w)
        self._sync_not_mode()


# ============================================================
# 策略编辑对话框（简单 / 高级 双模式）
# ============================================================

class StrategyDialog(QDialog):
    """新建 / 编辑策略。strategy 为 None 时是新建模式。"""

    def __init__(self, parent=None, store: WatchlistStore = None,
                 strategy: dict = None):
        super().__init__(parent)
        self.store = store
        self.strategy = strategy
        self.setWindowTitle("编辑策略" if strategy else "➕ 新建策略")
        self.setMinimumWidth(560)
        self._build()
        self._update_preview()
        if strategy:
            self._fill(strategy)

    def _build(self):
        lay = QVBoxLayout(self)

        form = QFormLayout()
        self.name_edit = QLineEdit()
        self.name_edit.setPlaceholderText("如：跌破MA60减仓 / MACD金叉放量")
        form.addRow("策略名称:", self.name_edit)

        self.type_combo = QComboBox()
        for key, label in STRATEGY_TYPES.items():
            self.type_combo.addItem(label, key)
        form.addRow("建议类型:", self.type_combo)

        self.mode_combo = QComboBox()
        self.mode_combo.addItems([
            "简单：单指标 + 阈值",
            "高级：条件组合（AND/OR/NOT · 金叉死叉 · 指标间比较）",
        ])
        self.mode_combo.currentIndexChanged.connect(self._on_mode_changed)
        form.addRow("条件模式:", self.mode_combo)
        lay.addLayout(form)

        self.stack = QStackedWidget()
        lay.addWidget(self.stack, 1)

        # ── 页1：简单模式（存量交互不变）──
        simple = QFormLayout()
        self.ind_combo = QComboBox()
        for label, key in INDICATOR_ITEMS:
            self.ind_combo.addItem(label, key)
        simple.addRow("监控指标:", self.ind_combo)

        self.op_combo = QComboBox()
        for op, label in OPERATORS.items():
            self.op_combo.addItem(label, op)
        simple.addRow("触发条件:", self.op_combo)

        self.threshold_spin = QDoubleSpinBox()
        self.threshold_spin.setRange(-9999, 999999)
        self.threshold_spin.setDecimals(2)
        self.threshold_spin.setValue(0)
        simple.addRow("阈值:", self.threshold_spin)
        page1 = QWidget()
        page1.setLayout(simple)
        self.stack.addWidget(page1)

        # ── 页2：高级模式（条件树构建器）──
        self.tree_root = GroupEditor(depth=0)
        self.tree_root._add_leaf()   # 默认给出一行，避免空白起点
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setWidget(self.tree_root)
        scroll.setMinimumHeight(180)
        self.stack.addWidget(scroll)

        form2 = QFormLayout()
        self.action_edit = QLineEdit()
        self.action_edit.setPlaceholderText("如：现价跌破MA60，建议减仓1/3观察")
        form2.addRow("触发后建议:", self.action_edit)

        self.prio_combo = QComboBox()
        self.prio_combo.addItems(["P0", "P1", "P2", "P3"])
        self.prio_combo.setCurrentText("P1")
        form2.addRow("优先级:", self.prio_combo)
        lay.addLayout(form2)

        self.preview_lbl = QLabel("")
        self.preview_lbl.setStyleSheet(
            "color:#555;font-size:12px;padding:2px;"
            "background:#f7f9fa;border-radius:3px")
        self.preview_lbl.setWordWrap(True)
        lay.addWidget(self.preview_lbl)
        self.tree_root.changed.connect(self._update_preview)

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
        lay.addLayout(btns)

    def _on_mode_changed(self, *_):
        self.stack.setCurrentIndex(self.mode_combo.currentIndex())
        self._update_preview()

    def _update_preview(self, *_):
        """高级模式下实时生成人类可读的条件描述（降级展示）。"""
        if self.mode_combo.currentIndex() != 1:
            self.preview_lbl.setText(
                f"条件描述: {self.ind_combo.currentData() or ''} "
                f"{self.op_combo.currentData() or ''} "
                f"{self.threshold_spin.value():g}")
            return
        try:
            node = self.tree_root.node()
            text = describe_condition(node)
            validate_condition(node)
        except Exception as e:   # 描述生成失败/结构不完整：直接展示原因
            text = f"（条件不完整：{e}）"
        self.preview_lbl.setText(f"条件描述: {text}")

    def _fill(self, s: dict):
        self.name_edit.setText(str(s.get("name", "")))
        self.type_combo.setCurrentIndex(
            max(0, self.type_combo.findData(str(s.get("type", "sell")))))
        raw = str(s.get("condition") or "").strip()
        if raw:
            # 复合策略 → 高级模式加载条件树
            self.mode_combo.setCurrentIndex(1)
            try:
                self.tree_root.set_node(json.loads(raw))
            except json.JSONDecodeError:
                pass
        else:
            self.mode_combo.setCurrentIndex(0)
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
        self._update_preview()

    def accept(self):
        if not self.name_edit.text().strip():
            QMessageBox.warning(self, "缺少名称", "请填写策略名称。")
            return
        if not self.action_edit.text().strip():
            QMessageBox.warning(self, "缺少建议", "请填写触发后的建议动作。")
            return
        if self.mode_combo.currentIndex() == 1:
            node = self.tree_root.node()
            try:
                validate_condition(node)
            except StrategyConfigError as e:
                QMessageBox.warning(self, "条件配置错误", f"请检查条件设置：\n{e}")
                return
        super().accept()

    def get_record(self) -> dict:
        record = {
            "name": self.name_edit.text().strip(),
            "type": self.type_combo.currentData(),
            "action": self.action_edit.text().strip(),
            "priority": self.prio_combo.currentText(),
        }
        if self.mode_combo.currentIndex() == 1:
            record.update({
                "indicator": "", "operator": "", "threshold": "",
                "condition": json.dumps(
                    self.tree_root.node(), ensure_ascii=False,
                    separators=(",", ":")),
            })
        else:
            record.update({
                "indicator": self.ind_combo.currentData(),
                "operator": self.op_combo.currentData(),
                "threshold": f"{self.threshold_spin.value():g}",
                "condition": "",
            })
        return record


# ============================================================
# 策略列表页
# ============================================================

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
            "策略 = 触发条件 → 建议动作。支持两类：简单（单指标+阈值）与"
            "复合（条件组合：AND/OR/NOT、均线形态、MACD/均线金叉死叉，"
            "新建策略对话框里切换「高级」模式配置）。在「监控自选」页给股票"
            "应用策略后，由监控引擎周期判断并在触发时弹窗 + 邮件提醒。")
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

    @staticmethod
    def _condition_cells(row) -> tuple:
        """返回 (指标列文本, 条件列文本, 条件全文tooltip)。"""
        raw = str(row.get("condition", "") or "").strip()
        if raw:
            try:
                full = describe_condition(json.loads(raw))
            except Exception:
                full = raw[:80]
            short = full if len(full) <= 42 else full[:41] + "…"
            return "🔀 复合条件", short, full
        ind_key = str(row.get("indicator", ""))
        ind_label = INDICATORS.get(ind_key, ind_key) or "—"
        cond = f"{str(row.get('operator', ''))} {str(row.get('threshold', ''))}".strip()
        return ind_label, cond or "—", f"{ind_label} {cond}"

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
            ind_label, cond_label, cond_full = self._condition_cells(row)
            enabled = str(row["enabled"]) == "1"
            stype = str(row["type"])

            vals = [
                str(row["id"]),
                str(row["name"]),
                STRATEGY_TYPES.get(stype, stype),
                ind_label,
                cond_label,
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
                if c == 3 and str(row.get("condition", "") or "").strip():
                    item.setForeground(QColor("#8e44ad"))
                if c == 4:
                    item.setToolTip(cond_full)
                if c == 7 and not enabled:
                    item.setForeground(QColor("#999"))
                self.table.setItem(r, c, item)

        self.table.resizeColumnsToContents()
        for c in range(self.table.columnCount()):
            if self.table.columnWidth(c) > 260:
                self.table.setColumnWidth(c, 260)
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
