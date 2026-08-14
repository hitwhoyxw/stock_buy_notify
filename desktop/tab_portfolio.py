"""持仓总览页：四桶持仓 + 盈亏 + 集中度 + 净值曲线 + K线图。

布局：
  顶部：摘要卡片（总成本/总市值/盈亏/净值/回撤）
  左侧：持仓明细表（按桶分组，实时盈亏）
  右侧上：四桶配比饼图 + 净值曲线
  右侧下：选中个股 K线 + MA60 + 买卖点
"""
from __future__ import annotations

import json
import os
from typing import Dict, List, Optional

import pandas as pd
from PyQt5.QtCore import Qt, QThread, QDate, pyqtSignal, QTimer
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QSplitter, QGridLayout,
    QLabel, QPushButton, QTableWidget, QTableWidgetItem, QHeaderView,
    QFrame, QComboBox, QMessageBox, QDialog, QFormLayout, QDateEdit,
    QDoubleSpinBox, QSpinBox, QLineEdit, QStackedWidget,
)
from PyQt5.QtGui import QColor, QFont

from engine import DataManager

# pyqtgraph 可选
try:
    import pyqtgraph as pg
    pg.setConfigOption("background", "#fff")
    pg.setConfigOption("foreground", "#333")
    HAS_PG = True
except ImportError:
    HAS_PG = False

# matplotlib 饼图
try:
    import matplotlib
    matplotlib.use("Qt5Agg")
    from matplotlib.figure import Figure
    from matplotlib.backends.backend_qt5agg import FigureCanvasQTAgg as FigureCanvas
    HAS_MPL = True
except ImportError:
    HAS_MPL = False

# 桶颜色
BUCKET_COLORS = {"A": "#27ae60", "B": "#2980b9", "C": "#e74c3c", "D": "#95a5a6"}
BUCKET_NAMES = {"A": "红利逆向", "B": "成长", "C": "热点周期", "D": "弹药库"}


# ============================================================
# 后线程：拉取实时行情
# ============================================================

def _to_tencent_symbol(code: str) -> str:
    pure = code.split(".")[0].zfill(6)
    if pure.startswith(("6", "5", "9")):
        return f"sh{pure}"
    if pure.startswith(("4", "8")):
        return f"bj{pure}"
    return f"sz{pure}"


class PriceFetcher(QThread):
    """后台拉取实时行情（腾讯源）。"""
    done = pyqtSignal(dict)

    def __init__(self, codes: list):
        super().__init__()
        self.codes = [c.split(".")[0].zfill(6) for c in codes]

    def run(self):
        import requests
        result: Dict[str, float] = {}
        for i in range(0, len(self.codes), 60):
            chunk = self.codes[i:i + 60]
            q = ",".join(_to_tencent_symbol(c) for c in chunk)
            try:
                resp = requests.get(
                    "http://qt.gtimg.cn/q=" + q,
                    headers={"User-Agent": "Mozilla/5.0"},
                    timeout=10,
                )
                resp.encoding = "gbk"
            except Exception:
                continue
            for line in resp.text.strip().split(";"):
                line = line.strip()
                if "=" not in line:
                    continue
                _, val = line.split("=", 1)
                f = val.strip('"').split("~")
                if len(f) < 47:
                    continue
                code = f[2]
                try:
                    price = float(f[3])
                    if price > 0:
                        result[code] = price
                except (ValueError, TypeError):
                    pass
        self.done.emit(result)


class KlineFetcher(QThread):
    """后台拉取日 K 线（腾讯源）。"""
    done = pyqtSignal(object)  # DataFrame or None

    def __init__(self, code: str, days: int = 180):
        super().__init__()
        self.code = code.split(".")[0].zfill(6)
        self.days = days

    def run(self):
        import requests
        from datetime import datetime, timedelta
        end = datetime.now().strftime("%Y-%m-%d")
        start = (datetime.now() - timedelta(days=self.days * 2)).strftime("%Y-%m-%d")
        sym = _to_tencent_symbol(self.code)
        url = (f"http://web.ifzq.gtimg.cn/appstock/app/fqkline/get?"
               f"param={sym},day,{start},{end},640,qfq")
        try:
            resp = requests.get(url, timeout=15)
            data = resp.json()
            kdata = data.get("data", {}).get(sym, {})
            day_list = kdata.get("day", kdata.get("qfqday", []))
            if not day_list:
                self.done.emit(None)
                return
            rows = []
            for d in day_list:
                if len(d) >= 6:
                    rows.append({
                        "date": d[0], "open": float(d[1]),
                        "close": float(d[2]), "high": float(d[3]),
                        "low": float(d[4]), "volume": float(d[5]),
                    })
            df = pd.DataFrame(rows)
            if not df.empty:
                df["ma60"] = df["close"].rolling(60).mean()
                df["ma20"] = df["close"].rolling(20).mean()
            self.done.emit(df)
        except Exception:
            self.done.emit(None)


# ============================================================
# 摘要卡片
# ============================================================

class SummaryCard(QFrame):
    """单个摘要卡片。"""

    def __init__(self, title: str, value: str = "--", color: str = "#2c3e50"):
        super().__init__()
        self.setFrameStyle(QFrame.StyledPanel | QFrame.Raised)
        self.setStyleSheet(
            f"QFrame{{background:#fff;border:1px solid #e0e0e0;border-radius:6px;"
            f"padding:8px}}")
        lay = QVBoxLayout(self)
        lay.setContentsMargins(12, 8, 12, 8)
        self.title = QLabel(title)
        self.title.setStyleSheet(f"color:{color};font-size:12px;font-weight:bold")
        lay.addWidget(self.title)
        self.value = QLabel(value)
        self.value.setStyleSheet("font-size:18px;font-weight:bold;color:#2c3e50")
        lay.addWidget(self.value)

    def set_value(self, v: str, color: str = ""):
        self.value.setText(v)
        if color:
            self.value.setStyleSheet(f"font-size:18px;font-weight:bold;color:{color}")


# ============================================================
# 交易录入/编辑对话框
# ============================================================

class TradeDialog(QDialog):
    """记一笔交易 / 编辑已有交易记录。

    trade 为 None 时是新增模式，否则编辑模式（预填数据）。
    金额默认按 价格×股数 自动计算，也可手动覆盖（含手续费场景）。
    """

    def __init__(self, parent=None, trade: dict = None):
        super().__init__(parent)
        self.setWindowTitle("编辑交易" if trade else "➕ 记一笔交易")
        self.setMinimumWidth(440)
        self._build()
        if trade:
            self._fill(trade)
        else:
            self.shares_spin.setValue(100)

    def _build(self):
        form = QFormLayout()

        self.date_edit = QDateEdit(QDate.currentDate())
        self.date_edit.setDisplayFormat("yyyy-MM-dd")
        self.date_edit.setCalendarPopup(True)
        form.addRow("日期:", self.date_edit)

        self.direction_combo = QComboBox()
        self.direction_combo.addItems(["买入", "卖出"])
        form.addRow("方向:", self.direction_combo)

        self.bucket_combo = QComboBox()
        self.bucket_combo.addItems(["A", "B", "C", "D"])
        form.addRow("桶:", self.bucket_combo)

        self.code_edit = QLineEdit()
        self.code_edit.setPlaceholderText("6 位数字，如 600519")
        form.addRow("代码:", self.code_edit)

        self.name_edit = QLineEdit()
        form.addRow("名称:", self.name_edit)

        self.industry_edit = QLineEdit()
        self.industry_edit.setPlaceholderText("申万一级行业（可选）")
        form.addRow("行业:", self.industry_edit)

        self.price_spin = QDoubleSpinBox()
        self.price_spin.setDecimals(3)
        self.price_spin.setRange(0.001, 999999)
        self.price_spin.valueChanged.connect(self._auto_amount)
        form.addRow("价格:", self.price_spin)

        self.shares_spin = QSpinBox()
        self.shares_spin.setRange(0, 100_000_000)
        self.shares_spin.setSingleStep(100)
        self.shares_spin.valueChanged.connect(self._auto_amount)
        form.addRow("股数:", self.shares_spin)

        self.amount_spin = QDoubleSpinBox()
        self.amount_spin.setDecimals(2)
        self.amount_spin.setRange(0, 999_999_999)
        form.addRow("金额(元):", self.amount_spin)

        self.rule_edit = QLineEdit()
        self.rule_edit.setPlaceholderText("触发规则 ID（可选）")
        form.addRow("规则ID:", self.rule_edit)

        self.reason_edit = QLineEdit()
        self.reason_edit.setPlaceholderText("一句话决策理由（可选）")
        form.addRow("决策理由:", self.reason_edit)

        btns = QHBoxLayout()
        ok_btn = QPushButton("💾 保存")
        ok_btn.setStyleSheet(
            "QPushButton{background:#27ae60;color:white;border:none;"
            "padding:8px 24px;border-radius:4px;font-size:13px}"
            "QPushButton:hover{background:#229954}")
        ok_btn.clicked.connect(self.accept)
        cancel_btn = QPushButton("取消")
        cancel_btn.clicked.connect(self.reject)
        btns.addStretch()
        btns.addWidget(ok_btn)
        btns.addWidget(cancel_btn)

        lay = QVBoxLayout(self)
        lay.addLayout(form)
        lay.addLayout(btns)

    def _auto_amount(self):
        """价格或股数变化时自动算金额。"""
        self.amount_spin.setValue(
            round(self.price_spin.value() * self.shares_spin.value(), 2))

    def _fill(self, trade: dict):
        """编辑模式预填。"""
        try:
            y, m, d = str(trade.get("日期", "")).split("-")
            self.date_edit.setDate(QDate(int(y), int(m), int(d)))
        except Exception:
            pass
        direction = str(trade.get("方向", "买入")).strip() or "买入"
        self.direction_combo.setCurrentText(direction)
        bucket = str(trade.get("桶", "A")).strip().upper() or "A"
        self.bucket_combo.setCurrentText(bucket)
        self.code_edit.setText(str(trade.get("代码", "")))
        self.name_edit.setText(str(trade.get("名称", "")))
        self.industry_edit.setText(str(trade.get("申万一级行业", "")))
        try:
            self.price_spin.setValue(float(trade.get("价格", 0) or 0))
        except (ValueError, TypeError):
            pass
        try:
            self.shares_spin.setValue(int(float(trade.get("股数", 0) or 0)))
        except (ValueError, TypeError):
            pass
        # 金额在价格/股数之后设置，覆盖自动计算值
        try:
            self.amount_spin.setValue(float(trade.get("金额", 0) or 0))
        except (ValueError, TypeError):
            pass
        self.rule_edit.setText(str(trade.get("触发规则ID", "")))
        self.reason_edit.setText(str(trade.get("决策理由(一句话)", "")))

    def accept(self):
        """保存前校验。"""
        code = self.code_edit.text().strip()
        if not (code.isdigit() and len(code) == 6):
            QMessageBox.warning(self, "代码格式错误",
                                "股票代码必须是 6 位数字，如 600519。")
            return
        if self.shares_spin.value() <= 0:
            QMessageBox.warning(self, "股数错误", "股数必须大于 0。")
            return
        if self.price_spin.value() <= 0:
            QMessageBox.warning(self, "价格错误", "价格必须大于 0。")
            return
        super().accept()

    def get_record(self) -> dict:
        return {
            "日期": self.date_edit.date().toString("yyyy-MM-dd"),
            "方向": self.direction_combo.currentText(),
            "桶": self.bucket_combo.currentText(),
            "代码": self.code_edit.text().strip(),
            "名称": self.name_edit.text().strip(),
            "申万一级行业": self.industry_edit.text().strip(),
            "价格": f"{self.price_spin.value():.3f}",
            "股数": str(self.shares_spin.value()),
            "金额": f"{self.amount_spin.value():.2f}",
            "触发规则ID": self.rule_edit.text().strip(),
            "决策理由(一句话)": self.reason_edit.text().strip(),
        }


# ============================================================
# 持仓总览 Tab
# ============================================================

class PortfolioTab(QWidget):
    """持仓总览页面。"""

    def __init__(self, dm: DataManager):
        super().__init__()
        self.dm = dm
        self._prices: Dict[str, float] = {}
        self._kline_thread: Optional[KlineFetcher] = None
        self._price_thread: Optional[PriceFetcher] = None
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)

        # 顶部栏
        bar = QHBoxLayout()
        bar.addWidget(QLabel("<h2>📊 持仓总览</h2>"))

        # 视图切换：持仓汇总 / 交易流水
        self.view_combo = QComboBox()
        self.view_combo.addItems(["持仓汇总", "交易流水"])
        self.view_combo.currentIndexChanged.connect(self._switch_view)
        self.view_combo.setStyleSheet("padding:4px")
        bar.addWidget(self.view_combo)
        bar.addStretch()

        # 记一笔交易
        self.add_trade_btn = QPushButton("➕ 记一笔")
        self.add_trade_btn.setStyleSheet(
            "QPushButton{background:#27ae60;color:white;border:none;"
            "padding:6px 16px;border-radius:4px;font-size:13px}"
            "QPushButton:hover{background:#229954}")
        self.add_trade_btn.clicked.connect(self._on_add_trade)
        bar.addWidget(self.add_trade_btn)

        self.refresh_btn = QPushButton("🔄 刷新行情")
        self.refresh_btn.setStyleSheet(
            "QPushButton{background:#3498db;color:white;border:none;"
            "padding:6px 16px;border-radius:4px;font-size:13px}"
            "QPushButton:hover{background:#2980b9}")
        self.refresh_btn.clicked.connect(self._refresh_prices)
        bar.addWidget(self.refresh_btn)
        lay.addLayout(bar)

        # 视图堆栈：页1 持仓汇总 / 页2 交易流水
        self.stack = QStackedWidget()

        # ── 页1：持仓汇总（卡片 + 表 + 图 + K线）──
        page_pos = QWidget()
        pos_lay = QVBoxLayout(page_pos)
        pos_lay.setContentsMargins(0, 0, 0, 0)

        # 摘要卡片行
        cards = QHBoxLayout()
        self.card_cost = SummaryCard("总成本", "0")
        self.card_mv = SummaryCard("总市值", "0")
        self.card_pnl = SummaryCard("浮盈亏", "0")
        self.card_nav = SummaryCard("净值", "1.0000")
        self.card_dd = SummaryCard("回撤", "0%")
        for c in [self.card_cost, self.card_mv, self.card_pnl, self.card_nav, self.card_dd]:
            cards.addWidget(c)
        cards.addStretch()
        pos_lay.addLayout(cards)

        # 主体：左表 + 右图
        splitter = QSplitter(Qt.Horizontal)

        # 左：持仓表
        left = QWidget()
        left_lay = QVBoxLayout(left)
        left_lay.setContentsMargins(0, 0, 0, 0)
        left_lay.addWidget(QLabel("<b>持仓明细</b>"))
        self.table = QTableWidget()
        self.table.setColumnCount(9)
        self.table.setHorizontalHeaderLabels(
            ["代码", "名称", "桶", "股数", "成本", "现价", "市值", "盈亏", "盈亏%"])
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.Stretch)
        self.table.setSelectionBehavior(QTableWidget.SelectRows)
        self.table.setSelectionMode(QTableWidget.SingleSelection)
        self.table.clicked.connect(self._on_row_clicked)
        left_lay.addWidget(self.table)
        splitter.addWidget(left)

        # 右：图表区
        right = QWidget()
        right_lay = QVBoxLayout(right)
        right_lay.setContentsMargins(0, 0, 0, 0)

        # 饼图 + 净值曲线
        charts_split = QSplitter(Qt.Vertical)

        # 饼图
        if HAS_MPL:
            self.fig = Figure(figsize=(4, 3))
            self.canvas = FigureCanvas(self.fig)
            charts_split.addWidget(self.canvas)
        else:
            charts_split.addWidget(QLabel("matplotlib 不可用，无法显示饼图"))

        # 净值曲线
        if HAS_PG:
            self.nav_plot = pg.PlotWidget(title="组合净值")
            self.nav_plot.setLabel("left", "净值")
            self.nav_plot.setLabel("bottom", "日期")
            self.nav_plot.showGrid(x=False, y=True)
            charts_split.addWidget(self.nav_plot)
        else:
            charts_split.addWidget(QLabel("pyqtgraph 不可用"))

        charts_split.setSizes([200, 300])
        right_lay.addWidget(charts_split)
        splitter.addWidget(right)

        splitter.setSizes([500, 400])
        pos_lay.addWidget(splitter)

        # 底部：K线图
        if HAS_PG:
            self.kline_label = QLabel("")
            self.kline_label.setStyleSheet("color:#999;font-size:11px")
            pos_lay.addWidget(self.kline_label)
            self.kline_plot = pg.PlotWidget(title="个股 K线")
            self.kline_plot.setLabel("left", "价格")
            self.kline_plot.setLabel("bottom", "交易日")
            self.kline_plot.showGrid(x=False, y=True)
            pos_lay.addWidget(self.kline_plot)
        else:
            pos_lay.addWidget(QLabel("安装 pyqtgraph 后可显示 K 线图"))

        self.stack.addWidget(page_pos)

        # ── 页2：交易流水（增删改查）──
        page_trades = QWidget()
        trades_lay = QVBoxLayout(page_trades)
        trades_lay.setContentsMargins(0, 0, 0, 0)

        trades_bar = QHBoxLayout()
        hint = QLabel("双击行或点按钮编辑；增删改后持仓与图表自动刷新")
        hint.setStyleSheet("color:#999;font-size:11px")
        trades_bar.addWidget(hint)
        trades_bar.addStretch()

        self.edit_trade_btn = QPushButton("✏️ 编辑选中")
        self.edit_trade_btn.setStyleSheet(
            "QPushButton{background:#f39c12;color:white;border:none;"
            "padding:6px 16px;border-radius:4px;font-size:13px}"
            "QPushButton:hover{background:#e67e22}")
        self.edit_trade_btn.clicked.connect(lambda: self._on_edit_trade())
        trades_bar.addWidget(self.edit_trade_btn)

        self.del_trade_btn = QPushButton("🗑 删除选中")
        self.del_trade_btn.setStyleSheet(
            "QPushButton{background:#e74c3c;color:white;border:none;"
            "padding:6px 16px;border-radius:4px;font-size:13px}"
            "QPushButton:hover{background:#c0392b}")
        self.del_trade_btn.clicked.connect(self._on_delete_trade)
        trades_bar.addWidget(self.del_trade_btn)

        trades_lay.addLayout(trades_bar)

        self.trades_table = QTableWidget()
        self.trades_table.setColumnCount(11)
        self.trades_table.setHorizontalHeaderLabels(
            ["日期", "方向", "桶", "代码", "名称", "行业",
             "价格", "股数", "金额", "规则ID", "决策理由"])
        self.trades_table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeToContents)
        self.trades_table.horizontalHeader().setStretchLastSection(True)
        self.trades_table.setSelectionBehavior(QTableWidget.SelectRows)
        self.trades_table.setSelectionMode(QTableWidget.SingleSelection)
        self.trades_table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.trades_table.doubleClicked.connect(
            lambda idx: self._on_edit_trade(idx))
        trades_lay.addWidget(self.trades_table)

        self.stack.addWidget(page_trades)
        lay.addWidget(self.stack)

    def showEvent(self, e):
        super().showEvent(e)
        self._load()

    def _load(self):
        """加载持仓数据和图表。"""
        self._load_positions()
        self._load_nav()
        self._load_pie()
        self._load_trades()

    # ── 视图切换 ──

    def _switch_view(self, index: int):
        self.stack.setCurrentIndex(index)
        if index == 1:
            self._load_trades()

    # ── 交易流水 CRUD ──

    def _load_trades(self):
        """加载交易流水表。"""
        df = self.dm.read_trades()
        self.trades_table.setRowCount(0)
        if df.empty:
            return
        cols = ["日期", "方向", "桶", "代码", "名称", "申万一级行业",
                "价格", "股数", "金额", "触发规则ID", "决策理由(一句话)"]
        self.trades_table.setRowCount(len(df))
        for i, (_, row) in enumerate(df.iterrows()):
            for j, c in enumerate(cols):
                text = str(row.get(c, ""))
                item = QTableWidgetItem(text)
                if c == "方向":
                    item.setForeground(
                        QColor("#e74c3c" if text == "买入" else "#27ae60"))
                elif c == "桶":
                    item.setForeground(
                        QColor(BUCKET_COLORS.get(text.strip().upper(), "#333")))
                self.trades_table.setItem(i, j, item)

    def _on_add_trade(self):
        """记一笔交易（新增）。"""
        dlg = TradeDialog(self)
        if dlg.exec_() != QDialog.Accepted:
            return
        record = dlg.get_record()
        # 卖出超持仓提醒（不强制阻止，允许跨桶修正等场景）
        if record["方向"] == "卖出":
            held = self.dm.shares_of(record["代码"])
            if float(record["股数"]) > held:
                ret = QMessageBox.warning(
                    self, "卖出超过持仓",
                    f"{record['代码']} 当前净持仓 {held:.0f} 股，"
                    f"本次卖出 {record['股数']} 股，将出现负持仓。\n仍要保存吗？",
                    QMessageBox.Yes | QMessageBox.No)
                if ret != QMessageBox.Yes:
                    return
        if self.dm.append_trade(record):
            self._after_trade_changed()
        else:
            QMessageBox.critical(self, "保存失败", "写入 live_trade_log.csv 失败，请检查文件是否被占用。")

    def _on_edit_trade(self, index=None):
        """编辑选中的交易记录（按钮触发 index=None；双击触发传 QModelIndex）。"""
        row = index.row() if index is not None and hasattr(index, "row") \
            else self.trades_table.currentRow()
        if row < 0:
            QMessageBox.information(self, "提示", "请先选中一条交易记录。")
            return
        df = self.dm.read_trades()
        if row >= len(df):
            return
        trade = df.iloc[row].to_dict()
        dlg = TradeDialog(self, trade=trade)
        if dlg.exec_() != QDialog.Accepted:
            return
        record = dlg.get_record()
        if self.dm.update_trade(row, record):
            self._after_trade_changed()
        else:
            QMessageBox.critical(self, "保存失败", "更新 live_trade_log.csv 失败。")

    def _on_delete_trade(self):
        """删除选中的交易记录。"""
        row = self.trades_table.currentRow()
        if row < 0:
            QMessageBox.information(self, "提示", "请先选中一条交易记录。")
            return
        df = self.dm.read_trades()
        if row >= len(df):
            return
        t = df.iloc[row]
        ret = QMessageBox.question(
            self, "确认删除",
            f"确定删除这条交易记录？\n\n"
            f"{t['日期']}  {t['方向']}  {t['代码']} {t['名称']}\n"
            f"{t['股数']} 股 @ {t['价格']} 元 = {t['金额']} 元",
            QMessageBox.Yes | QMessageBox.No)
        if ret != QMessageBox.Yes:
            return
        if self.dm.delete_trade(row):
            self._after_trade_changed()
        else:
            QMessageBox.critical(self, "删除失败", "更新 live_trade_log.csv 失败。")

    def _after_trade_changed(self):
        """增删改交易后刷新持仓、图表、流水表。"""
        self._load()

    # ── 持仓表 ──

    def _load_positions(self):
        pos = self.dm.load_positions()
        self.table.setRowCount(0)
        if pos.empty:
            return

        total_cost = 0.0
        total_mv = 0.0
        self.table.setRowCount(len(pos))
        for i, (_, row) in enumerate(pos.iterrows()):
            code = str(row["代码"])
            name = str(row.get("名称", ""))
            bucket = str(row.get("桶", "")).strip().upper()
            shares = float(row.get("净股数", 0) or 0)
            avg_cost = float(row.get("平均成本", 0) or 0)
            cost_val = shares * avg_cost
            price = self._prices.get(code.zfill(6), 0)
            mv_val = shares * price if price > 0 else cost_val
            pnl = mv_val - cost_val
            pnl_pct = (pnl / cost_val * 100) if cost_val > 0 else 0

            total_cost += cost_val
            total_mv += mv_val

            items = [
                code, name, bucket, f"{shares:.0f}", f"{avg_cost:.2f}",
                f"{price:.2f}" if price > 0 else "--",
                f"{mv_val:.0f}", f"{pnl:+.0f}", f"{pnl_pct:+.1f}%",
            ]
            for j, text in enumerate(items):
                item = QTableWidgetItem(text)
                if j == 2:  # 桶列着色
                    color = BUCKET_COLORS.get(bucket, "#333")
                    item.setForeground(QColor(color))
                if j == 7 or j == 8:  # 盈亏列着色
                    if pnl > 0:
                        item.setForeground(QColor("#e74c3c"))
                    elif pnl < 0:
                        item.setForeground(QColor("#27ae60"))
                self.table.setItem(i, j, item)

        # 更新摘要卡片
        self.card_cost.set_value(f"{total_cost:,.0f}")
        self.card_mv.set_value(f"{total_mv:,.0f}")
        pnl_total = total_mv - total_cost
        pnl_color = "#e74c3c" if pnl_total > 0 else ("#27ae60" if pnl_total < 0 else "#2c3e50")
        self.card_pnl.set_value(
            f"{pnl_total:+,.0f} ({pnl_total/total_cost*100:+.1f}%)" if total_cost > 0 else "0",
            pnl_color)

    # ── 饼图 ──

    def _load_pie(self):
        if not HAS_MPL:
            return
        weights = self.dm.bucket_weights()
        self.fig.clear()
        ax = self.fig.add_subplot(111)
        labels = []
        sizes = []
        colors = []
        for b in ["A", "B", "C", "D"]:
            w = weights.get(b, 0)
            if w > 0.001:
                labels.append(f"{b} {BUCKET_NAMES[b]} ({w*100:.1f}%)")
                sizes.append(w)
                colors.append(BUCKET_COLORS[b])
        if sizes:
            ax.pie(sizes, labels=labels, colors=colors, autopct="",
                   startangle=90, wedgeprops={"width": 0.4})
            ax.set_title("四桶配比", fontsize=10)
        else:
            ax.text(0.5, 0.5, "暂无持仓", ha="center", va="center", fontsize=12)
            ax.set_axis_off()
        self.canvas.draw()

    # ── 净值曲线 ──

    def _load_nav(self):
        if not HAS_PG:
            return
        nav_df = self.dm.load_nav()
        if nav_df.empty or "nav" not in nav_df.columns:
            return
        nav_vals = pd.to_numeric(nav_df["nav"], errors="coerce").dropna()
        if nav_vals.empty:
            return
        self.nav_plot.clear()
        x = list(range(len(nav_vals)))
        self.nav_plot.plot(x, nav_vals.tolist(), pen=pg.mkPen("#2980b9", width=2))

        # 峰值线
        peak = nav_vals.max()
        self.nav_plot.addLine(y=peak, pen=pg.mkPen("#e74c3c", style=Qt.DashDotLine, width=1))

        # 日期轴标签
        if "date" in nav_df.columns:
            dates = nav_df["date"].tolist()
            ticks = [[(i, str(d)[5:10]) for i, d in enumerate(dates) if i % max(1, len(dates)//10) == 0]]
            ax = self.nav_plot.getAxis("bottom")
            ax.setTicks(ticks)

        # 更新摘要卡片
        dd_str = nav_df.iloc[-1].get("drawdown_pct", "0")
        try:
            dd_val = float(dd_str)
            self.card_dd.set_value(f"{dd_val:.1f}%",
                                   "#e74c3c" if dd_val < -15 else "#2c3e50")
        except (ValueError, TypeError):
            pass
        try:
            self.card_nav.set_value(f"{float(nav_vals.iloc[-1]):.4f}")
        except (ValueError, IndexError):
            pass

    # ── 刷新行情 ──

    def _refresh_prices(self):
        pos = self.dm.load_positions()
        if pos.empty:
            QMessageBox.information(self, "提示", "当前无持仓，无需刷新。")
            return
        codes = [str(r["代码"]).strip() for _, r in pos.iterrows() if str(r["代码"]).strip()]
        self.refresh_btn.setEnabled(False)
        self.refresh_btn.setText("⏳ 拉取中...")
        self._price_thread = PriceFetcher(codes)
        self._price_thread.done.connect(self._on_prices_done)
        self._price_thread.start()

    def _on_prices_done(self, prices: dict):
        self._prices.update(prices)
        self.refresh_btn.setEnabled(True)
        self.refresh_btn.setText("🔄 刷新行情")
        self._load_positions()
        self._load_nav()

    # ── K线图 ──

    def _on_row_clicked(self, index):
        if not HAS_PG:
            return
        row = index.row()
        if row < 0:
            return
        code_item = self.table.item(row, 0)
        name_item = self.table.item(row, 1)
        if not code_item:
            return
        code = code_item.text()
        name = name_item.text() if name_item else ""
        self.kline_label.setText(f"加载 {code} {name} K线...")
        self._kline_thread = KlineFetcher(code)
        self._kline_thread.done.connect(lambda df: self._on_kline_done(code, name, df))
        self._kline_thread.start()

    def _on_kline_done(self, code: str, name: str, df):
        self.kline_plot.clear()
        if df is None or df.empty:
            self.kline_label.setText(f"{code} {name} K线数据获取失败")
            return
        self.kline_label.setText(f"{code} {name} · {len(df)} 日 K线 (MA20/MA60)")

        # 蜡烛图
        n = len(df)
        for i in range(n):
            o, c, h, l = df.iloc[i]["open"], df.iloc[i]["close"], df.iloc[i]["high"], df.iloc[i]["low"]
            color = "#e74c3c" if c >= o else "#27ae60"
            # 影线
            self.kline_plot.plot([i, i], [l, h], pen=pg.mkPen(color, width=1))
            # 实体
            body_low, body_high = min(o, c), max(o, c)
            self.kline_plot.plot([i, i], [body_low, body_high], pen=pg.mkPen(color, width=3))

        # MA20 / MA60
        if "ma20" in df.columns:
            ma20 = df["ma20"].dropna()
            if not ma20.empty:
                self.kline_plot.plot(
                    list(range(len(df) - len(ma20), len(df))),
                    ma20.tolist(), pen=pg.mkPen("#f39c12", width=1.5))
        if "ma60" in df.columns:
            ma60 = df["ma60"].dropna()
            if not ma60.empty:
                self.kline_plot.plot(
                    list(range(len(df) - len(ma60), len(df))),
                    ma60.tolist(), pen=pg.mkPen("#9b59b6", width=1.5))

        # 日期轴
        if "date" in df.columns:
            dates = df["date"].tolist()
            ticks = [[(i, str(d)[5:10]) for i, d in enumerate(dates) if i % max(1, n//8) == 0]]
            ax = self.kline_plot.getAxis("bottom")
            ax.setTicks(ticks)
