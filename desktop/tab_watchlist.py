"""监控自选页：量化策略监控的股票池 + 触发提醒历史。

入口：
  - 候选池页右键 "加入监控自选"
  - 本页 "手动添加"（输入 6 位代码）
每只股票可应用 0..N 个策略（右键 → 应用策略），
MonitorEngine 周期判断，触发 → 托盘弹窗 + 邮件 + 本页历史记录。
"""
from __future__ import annotations

from typing import Optional

from PyQt5.QtCore import Qt, QPoint, QTimer, QThread, pyqtSignal
from PyQt5.QtGui import QColor
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QLabel, QPushButton, QTableWidget,
    QTableWidgetItem, QHeaderView, QMessageBox, QDialog, QFormLayout,
    QLineEdit, QMenu, QAction, QGroupBox, QListWidget, QListWidgetItem,
    QInputDialog,
)

from monitor import MonitorEngine, TYPE_EMOJI, strategy_condition_text, fetch_quotes
from watchlist_store import WatchlistStore, STRATEGY_TYPES


class QuoteFetcher(QThread):
    """后台批量拉行情（复用 monitor.fetch_quotes，含名称/现价/涨跌幅）。

    用于本页 60s 自动刷新与添加对话框的"只填代码自动取名"。
    """
    done = pyqtSignal(dict)

    def __init__(self, codes: list):
        super().__init__()
        self.codes = [str(c).split(".")[0].zfill(6) for c in codes]

    def run(self):
        self.done.emit(fetch_quotes(self.codes))


# ============================================================
# 对话框
# ============================================================

class AddWatchDialog(QDialog):
    """手动添加监控股票（只填代码时自动获取名称）。"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("➕ 添加监控股票")
        self.setMinimumWidth(380)
        self._name_thread: Optional[QuoteFetcher] = None
        form = QFormLayout()

        self.code_edit = QLineEdit()
        self.code_edit.setPlaceholderText("6 位数字，如 600519")
        # 代码录入完成（失焦/回车）且名称为空 → 后台拉名称自动回填
        self.code_edit.editingFinished.connect(self._try_fetch_name)
        form.addRow("代码:", self.code_edit)

        self.name_edit = QLineEdit()
        self.name_edit.setPlaceholderText("名称（可选，只填代码可自动获取）")
        form.addRow("名称:", self.name_edit)

        self.note_edit = QLineEdit()
        self.note_edit.setPlaceholderText("备注（可选）")
        form.addRow("备注:", self.note_edit)

        btns = QHBoxLayout()
        ok_btn = QPushButton("添加")
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

    def _try_fetch_name(self):
        """只填代码时自动获取名称（名称已填则不打扰）。"""
        code = self.code_edit.text().strip()
        if not (code.isdigit() and len(code) in (5, 6)):
            return
        if self.name_edit.text().strip():
            return
        if self._name_thread and self._name_thread.isRunning():
            return
        self._name_thread = QuoteFetcher([code])
        self._name_thread.done.connect(self._on_name_fetched)
        self._name_thread.start()

    def _on_name_fetched(self, quotes: dict):
        """名称拉取回调：对话框仍打开且名称仍空 → 回填。"""
        if not self.isVisible():
            return
        code = self.code_edit.text().strip().zfill(6)
        q = quotes.get(code)
        if q and q.get("name") and not self.name_edit.text().strip():
            self.name_edit.setText(q["name"])

    def accept(self):
        code = self.code_edit.text().strip()
        if not (code.isdigit() and len(code) == 6):
            QMessageBox.warning(self, "代码格式错误",
                                "股票代码必须是 6 位数字，如 600519。")
            return
        super().accept()

    def get_record(self) -> dict:
        return {
            "code": self.code_edit.text().strip(),
            "name": self.name_edit.text().strip(),
            "note": self.note_edit.text().strip(),
        }


class ApplyStrategyDialog(QDialog):
    """给股票应用/解除策略（多选）。"""

    def __init__(self, parent=None, store: WatchlistStore = None,
                 code: str = "", name: str = ""):
        super().__init__(parent)
        self.setWindowTitle(f"🎯 应用策略 — {name} ({code})")
        self.setMinimumWidth(460)
        self.store = store
        self.code = code

        lay = QVBoxLayout(self)
        tip = QLabel("勾选要应用到该股票的策略（0 个或多个）：")
        tip.setStyleSheet("color:#666")
        lay.addWidget(tip)

        self.listw = QListWidget()
        current = self._current_ids(code)
        df = store.list_strategies()
        for _, row in df.iterrows():
            sid = str(row["id"])
            stype = str(row["type"])
            label = (f"[{sid}] {STRATEGY_TYPES.get(stype, stype)} · "
                     f"{row['name']} — {strategy_condition_text(row.to_dict())}")
            item = QListWidgetItem(label)
            item.setFlags(item.flags() | Qt.ItemIsUserCheckable)
            item.setCheckState(
                Qt.Checked if sid in current else Qt.Unchecked)
            if str(row["enabled"]) != "1":
                item.setForeground(QColor("#999"))
                item.setText("⏸ " + label)
            self.listw.addItem(item)
        lay.addWidget(self.listw)

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

    def _current_ids(self, code: str) -> set:
        df = self.store.list_watchlist()
        if df.empty:
            return set()
        hit = df[df["code"].str.zfill(6) == code.split(".")[0].zfill(6)]
        if hit.empty:
            return set()
        return {s for s in str(hit.iloc[0]["strategies"]).split(";") if s}

    def selected_ids(self) -> list:
        ids = []
        for i in range(self.listw.count()):
            item = self.listw.item(i)
            if item.checkState() == Qt.Checked:
                # label 形如 "[S1] ..."，取方括号内的 ID
                ids.append(item.text().split("]")[0].lstrip("⏸ ["))
        return ids


# ============================================================
# 主页面
# ============================================================

class WatchlistTab(QWidget):
    """监控自选池 + 触发历史。"""

    def __init__(self, store: WatchlistStore, monitor: Optional[MonitorEngine],
                 on_manage_strategies=None):
        super().__init__()
        self.store = store
        self.monitor = monitor
        self._on_manage_strategies = on_manage_strategies
        self._quote_thread: Optional[QuoteFetcher] = None
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)

        # ── 顶部操作栏 ──
        bar = QHBoxLayout()
        bar.addWidget(QLabel("<h2>🎯 监控自选</h2>"))

        add_btn = QPushButton("➕ 手动添加")
        add_btn.setStyleSheet("padding:6px 16px")
        add_btn.clicked.connect(self._on_add)
        bar.addWidget(add_btn)

        self.monitor_btn = QPushButton("▶ 开始监控")
        self.monitor_btn.setStyleSheet(
            "QPushButton{background:#27ae60;color:white;padding:6px 16px;"
            "border:none;border-radius:4px}")
        self.monitor_btn.clicked.connect(self._on_toggle_monitor)
        bar.addWidget(self.monitor_btn)

        strategy_btn = QPushButton("🧭 管理策略")
        strategy_btn.setStyleSheet("padding:6px 16px")
        strategy_btn.clicked.connect(self._goto_strategy_tab)
        bar.addWidget(strategy_btn)

        bar.addStretch()

        self.status_lbl = QLabel("监控未启动")
        self.status_lbl.setStyleSheet("color:#666;font-size:12px")
        bar.addWidget(self.status_lbl)

        refresh_btn = QPushButton("🔄 刷新")
        refresh_btn.setStyleSheet("padding:6px 14px")
        refresh_btn.clicked.connect(self._load)
        bar.addWidget(refresh_btn)

        lay.addLayout(bar)

        self.hint_lbl = QLabel(
            "从「候选池」右键添加股票到此页，或在策略页定义买入/持有/卖出建议后"
            "右键应用到股票。触发时托盘弹窗 + 邮件提醒（邮箱在设置页配置）。")
        self.hint_lbl.setStyleSheet("color:#666;font-size:12px;padding:2px")
        self.hint_lbl.setWordWrap(True)
        lay.addWidget(self.hint_lbl)

        # ── 监控股票表 ──
        self.table = QTableWidget()
        self.table.setAlternatingRowColors(True)
        self.table.setSelectionBehavior(QTableWidget.SelectRows)
        self.table.setSelectionMode(QTableWidget.SingleSelection)
        self.table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.table.verticalHeader().setDefaultSectionSize(30)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.setContextMenuPolicy(Qt.CustomContextMenu)
        self.table.customContextMenuRequested.connect(self._on_context_menu)
        lay.addWidget(self.table, stretch=3)

        # ── 触发历史 ──
        hist_group = QGroupBox("🔔 触发提醒记录（当日同一策略同一股票只提醒一次）")
        hist_lay = QVBoxLayout(hist_group)

        hist_bar = QHBoxLayout()
        self.hist_count_lbl = QLabel("")
        self.hist_count_lbl.setStyleSheet("color:#666;font-size:12px")
        hist_bar.addWidget(self.hist_count_lbl)
        hist_bar.addStretch()
        clear_btn = QPushButton("清空记录")
        clear_btn.setStyleSheet("padding:4px 12px")
        clear_btn.clicked.connect(self._on_clear_history)
        hist_bar.addWidget(clear_btn)
        hist_lay.addLayout(hist_bar)

        self.hist_table = QTableWidget()
        self.hist_table.setAlternatingRowColors(True)
        self.hist_table.setSelectionBehavior(QTableWidget.SelectRows)
        self.hist_table.setEditTriggers(QTableWidget.NoEditTriggers)
        self.hist_table.verticalHeader().setDefaultSectionSize(28)
        self.hist_table.horizontalHeader().setStretchLastSection(True)
        hist_lay.addWidget(self.hist_table)
        lay.addWidget(hist_group, stretch=2)

        self._load()
        self._load_history()

        # 60 秒自动拉行情：未启动监控也能看到现价/涨跌，并补全空白名称
        self._auto_timer = QTimer(self)
        self._auto_timer.setInterval(60_000)
        self._auto_timer.timeout.connect(self._auto_refresh_quotes)
        self._auto_timer.start()

    def showEvent(self, e):
        super().showEvent(e)
        self._load()
        self._load_history()
        self._sync_monitor_btn()
        self._auto_refresh_quotes()  # 打开页面即拉一次

    # ============================================================
    # 自选池表
    # ============================================================

    def _load(self):
        df = self.store.list_watchlist()
        self.table.setRowCount(0)
        self.table.setColumnCount(7)
        self.table.setHorizontalHeaderLabels(
            ["代码", "名称", "来源", "现价", "当日涨跌%", "应用策略", "备注"])

        snames = self._strategy_names()
        for _, row in df.iterrows():
            r = self.table.rowCount()
            self.table.insertRow(r)
            sids = [s for s in str(row["strategies"]).split(";") if s]
            sname_txt = ", ".join(snames.get(s, s) for s in sids) if sids else "—"
            vals = [
                str(row["code"]), str(row["name"]), str(row["added_from"]),
                "--", "--", sname_txt, str(row["note"]),
            ]
            for c, v in enumerate(vals):
                item = QTableWidgetItem(v)
                if c in (0, 2, 3, 4):
                    item.setTextAlignment(Qt.AlignCenter)
                if c == 5 and sname_txt != "—":
                    item.setForeground(QColor("#8e44ad"))
                self.table.setItem(r, c, item)

        self.table.resizeColumnsToContents()
        if self.table.columnCount() > 6:
            self.table.horizontalHeader().setSectionResizeMode(
                6, QHeaderView.Stretch)

    def _strategy_names(self) -> dict:
        df = self.store.list_strategies()
        if df.empty:
            return {}
        return {str(r["id"]): str(r["name"]) for _, r in df.iterrows()}

    def _on_quotes(self, quotes: dict):
        """行情到达 → 更新现价/涨跌列（MonitorEngine 推送与本页自动拉取共用）。"""
        for r in range(self.table.rowCount()):
            item = self.table.item(r, 0)
            if not item:
                continue
            code = item.text().strip()
            q = quotes.get(code)
            if not q:
                continue
            price_item = self.table.item(r, 3)
            if price_item:
                price_item.setText(f"{q.get('price', 0):.2f}")
            chg_item = self.table.item(r, 4)
            if chg_item is not None and q.get("change_pct") is not None:
                pct = float(q["change_pct"])
                chg_item.setText(f"{pct:+.2f}%")
                # A股习惯：涨红跌绿
                chg_item.setForeground(
                    QColor("#e74c3c") if pct > 0 else
                    QColor("#27ae60") if pct < 0 else QColor("#333"))

    # ── 60s 自动行情：现价/涨跌 + 名称自动补全 ──

    def _auto_refresh_quotes(self):
        """后台拉取自选池行情（静默；未启动监控也刷新）。"""
        df = self.store.list_watchlist()
        if df.empty:
            return
        if self._quote_thread and self._quote_thread.isRunning():
            return  # 上一轮还在拉取
        codes = [str(c).strip() for c in df["code"] if str(c).strip()]
        if not codes:
            return
        self._quote_thread = QuoteFetcher(codes)
        self._quote_thread.done.connect(self._on_quotes_auto)
        self._quote_thread.start()

    def _on_quotes_auto(self, quotes: dict):
        """自动行情回调：更新现价/涨跌，并补全名称空白的股票（回写 CSV）。"""
        self._on_quotes(quotes)
        for r in range(self.table.rowCount()):
            code_item = self.table.item(r, 0)
            name_item = self.table.item(r, 1)
            if not code_item or not name_item:
                continue
            q = quotes.get(code_item.text().strip())
            if q and q.get("name") and not name_item.text().strip():
                name_item.setText(q["name"])
                self.store.set_name(code_item.text().strip(), q["name"])

    def _current_code(self) -> (str, str):
        row = self.table.currentRow()
        if row < 0:
            return "", ""
        code_item = self.table.item(row, 0)
        name_item = self.table.item(row, 1)
        if not code_item:
            return "", ""
        return (code_item.text().strip(),
                name_item.text().strip() if name_item else "")

    def _on_context_menu(self, pos: QPoint):
        code, name = self._current_code()
        if not code:
            return
        menu = QMenu(self)
        apply_act = QAction(f"🎯 应用策略（{name or code}）", self)
        apply_act.triggered.connect(self._on_apply_strategy)
        menu.addAction(apply_act)

        note_act = QAction("📝 修改备注", self)
        note_act.triggered.connect(self._on_edit_note)
        menu.addAction(note_act)

        menu.addSeparator()
        del_act = QAction("🗑 移出监控池", self)
        del_act.triggered.connect(self._on_remove)
        menu.addAction(del_act)

        menu.exec_(self.table.viewport().mapToGlobal(pos))

    def _on_add(self):
        dlg = AddWatchDialog(self)
        if dlg.exec_() != QDialog.Accepted:
            return
        rec = dlg.get_record()
        ok, msg = self.store.add(rec["code"], rec["name"], "manual", rec["note"])
        if ok:
            self._load()
        else:
            QMessageBox.warning(self, "添加失败", msg)

    def _on_apply_strategy(self):
        code, name = self._current_code()
        if not code:
            return
        dlg = ApplyStrategyDialog(self, self.store, code, name)
        if dlg.exec_() != QDialog.Accepted:
            return
        if self.store.set_strategies(code, dlg.selected_ids()):
            self._load()

    def _on_edit_note(self):
        code, name = self._current_code()
        if not code:
            return
        df = self.store.list_watchlist()
        hit = df[df["code"].str.zfill(6) == code]
        old_note = str(hit.iloc[0]["note"]) if not hit.empty else ""
        text, ok = QInputDialog.getText(self, "修改备注",
                                        f"{name or code} 的备注:", text=old_note)
        if ok:
            self.store.set_note(code, text.strip())
            self._load()

    def _on_remove(self):
        code, name = self._current_code()
        if not code:
            return
        if QMessageBox.question(
                self, "确认移除",
                f"确定把 {name or code} 移出监控池吗？"
        ) != QMessageBox.Yes:
            return
        if self.store.remove(code):
            self._load()

    # ============================================================
    # 触发历史
    # ============================================================

    def _load_history(self):
        history = self.store.load_history()
        self.hist_table.setRowCount(0)
        self.hist_table.setColumnCount(8)
        self.hist_table.setHorizontalHeaderLabels(
            ["时间", "代码", "名称", "类型", "策略", "触发条件", "当前值", "建议动作"])

        for e in history:
            r = self.hist_table.rowCount()
            self.hist_table.insertRow(r)
            vals = [
                str(e.get("time", "")), str(e.get("code", "")),
                str(e.get("name", "")),
                TYPE_EMOJI.get(str(e.get("type", "")), ""),
                str(e.get("strategy_name", "")),
                str(e.get("indicator_label", "")),
                str(e.get("value", "")), str(e.get("action", "")),
            ]
            for c, v in enumerate(vals):
                item = QTableWidgetItem(v)
                if c in (0, 1, 3, 6):
                    item.setTextAlignment(Qt.AlignCenter)
                if c == 3:
                    t = str(e.get("type", ""))
                    item.setForeground(QColor(
                        {"buy": "#27ae60", "sell": "#e74c3c",
                         "hold": "#2980b9"}.get(t, "#333")))
                self.hist_table.setItem(r, c, item)

        self.hist_table.resizeColumnsToContents()
        if self.hist_table.columnCount() > 7:
            self.hist_table.horizontalHeader().setSectionResizeMode(
                7, QHeaderView.Stretch)
        self.hist_count_lbl.setText(f"共 {len(history)} 条")

    def on_alerts(self, alerts: list):
        """MonitorEngine 触发提醒后刷新历史（由 main_window 转发）。"""
        self._load_history()

    def _on_clear_history(self):
        if QMessageBox.question(
                self, "确认清空", "清空全部触发记录（含当日去重标记）？"
        ) != QMessageBox.Yes:
            return
        self.store.clear_history()
        self._load_history()

    # ============================================================
    # 监控启停
    # ============================================================

    def _goto_strategy_tab(self):
        if self._on_manage_strategies:
            self._on_manage_strategies()

    def _sync_monitor_btn(self):
        if self.monitor is None:
            return
        if self.monitor.is_active():
            self.monitor_btn.setText("⏸ 停止监控")
            self.monitor_btn.setStyleSheet(
                "QPushButton{background:#e74c3c;color:white;padding:6px 16px;"
                "border:none;border-radius:4px}")
            self.status_lbl.setText("监控运行中…")
        else:
            self.monitor_btn.setText("▶ 开始监控")
            self.monitor_btn.setStyleSheet(
                "QPushButton{background:#27ae60;color:white;padding:6px 16px;"
                "border:none;border-radius:4px}")
            self.status_lbl.setText("监控未启动")

    def _on_toggle_monitor(self):
        if self.monitor is None:
            return
        if self.monitor.is_active():
            self.monitor.stop_monitoring()
        else:
            if self.store.list_watchlist().empty:
                QMessageBox.information(
                    self, "监控池为空",
                    "请先添加股票（候选池右键 或 手动添加）再启动监控。")
                return
            self.monitor.start_monitoring()
        self._sync_monitor_btn()

    def on_monitor_status(self, msg: str):
        self.status_lbl.setText(msg)
