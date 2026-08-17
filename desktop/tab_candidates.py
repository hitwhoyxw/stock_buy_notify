"""候选池页：三桶 CSV 表格查看。

功能：
- A/B/C 三桶切换
- 表格排序、搜索过滤
- 机构持仓列高亮（险资/社保/养老/QFII）
- 导出 Excel
- 自动刷新
- 右键 "加入监控自选"（需传入 watchlist_store）
"""
from __future__ import annotations

import time

import pandas as pd
from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtWidgets import (
    QWidget, QVBoxLayout, QHBoxLayout, QPushButton, QLabel,
    QTableWidget, QTableWidgetItem, QHeaderView, QComboBox,
    QLineEdit, QFileDialog, QMessageBox, QCheckBox, QMenu, QAction,
)

from engine import DataManager


# 机构持仓列：绿色高亮
INST_COLS = {"has_insurance", "has_social_security", "has_pension", "has_qfii"}

# 不在表格中显示的列（太长）
HIDDEN_COLS = {"inst_detail", "pick_reason"}

# 数值列（用于排序）
NUMERIC_COLS = {
    "price", "dividend_yield_ttm", "roe_5y_avg", "fcf_coverage", "pb",
    "pb_percentile", "dividend_years", "loss_q_3y", "ocf_ps_annual",
    "quality_score", "sort_value",
    "total_mv_yi", "profit_cagr_3y", "revenue_cagr_3y", "np_yoy_latest",
    "roe_ann", "ocf_to_np", "pe_ttm", "peg",
    "text_score", "categories_hit_count", "np_yoy", "revenue_yoy",
    "gross_margin",
}


class CandidatesTab(QWidget):
    """三桶候选池表格查看页。"""

    def __init__(self, dm: DataManager, watchlist_store=None):
        super().__init__()
        self.dm = dm
        self.watchlist_store = watchlist_store
        self._full_df = pd.DataFrame()
        self._build()

    def _build(self):
        lay = QVBoxLayout(self)

        # ── 顶部操作栏 ──
        bar = QHBoxLayout()
        bar.addWidget(QLabel("<h2>📋 候选池</h2>"))

        self.bucket_combo = QComboBox()
        self.bucket_combo.addItems(["A — 红利逆向", "B — 成长", "C — 热点周期"])
        self.bucket_combo.currentIndexChanged.connect(self._load)
        self.bucket_combo.setStyleSheet("padding:4px")
        bar.addWidget(self.bucket_combo)

        bar.addStretch()

        # 机构持仓过滤
        self.inst_filter = QCheckBox("只看有机构持仓")
        self.inst_filter.stateChanged.connect(self._apply_filters)
        bar.addWidget(self.inst_filter)

        # 搜索
        self.search_edit = QLineEdit()
        self.search_edit.setPlaceholderText("搜索代码/名称…")
        self.search_edit.setFixedWidth(200)
        self.search_edit.textChanged.connect(self._apply_filters)
        bar.addWidget(self.search_edit)

        # 刷新
        refresh_btn = QPushButton("🔄 刷新")
        refresh_btn.setStyleSheet("padding:6px 14px")
        refresh_btn.clicked.connect(self._load)
        bar.addWidget(refresh_btn)

        # 导出
        export_btn = QPushButton("📤 导出 Excel")
        export_btn.setStyleSheet("padding:6px 14px")
        export_btn.clicked.connect(self._export)
        bar.addWidget(export_btn)

        lay.addLayout(bar)

        # ── 统计信息 ──
        self.stats_lbl = QLabel("")
        self.stats_lbl.setStyleSheet("color:#666;font-size:12px;padding:4px")
        lay.addWidget(self.stats_lbl)

        # ── 表格 ──
        self.table = QTableWidget()
        self.table.setAlternatingRowColors(True)
        self.table.setSelectionBehavior(QTableWidget.SelectRows)
        self.table.setSortingEnabled(True)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.verticalHeader().setDefaultSectionSize(28)
        self.table.setContextMenuPolicy(Qt.CustomContextMenu)
        self.table.customContextMenuRequested.connect(self._on_context_menu)
        lay.addWidget(self.table)

        # ── 明细面板（底部展开） ──
        self.detail_lbl = QLabel("")
        self.detail_lbl.setStyleSheet(
            "background:#f8f9fa;padding:8px;border-top:1px solid #dee2e6;"
            "font-size:12px;color:#495057")
        self.detail_lbl.setWordWrap(True)
        self.detail_lbl.setMaximumHeight(80)
        lay.addWidget(self.detail_lbl)

        self.table.itemSelectionChanged.connect(self._on_select)

    def showEvent(self, e):
        super().showEvent(e)
        self._load()

    # ── 加载数据 ──

    def _load(self):
        bucket = "ABC"[self.bucket_combo.currentIndex()]
        df = self.dm.load_csv(f"candidates_{bucket}.csv")
        self._full_df = df
        self._apply_filters()

        mtime = self.dm.file_mtime(f"candidates_{bucket}.csv")
        self.stats_lbl.setText(
            f"{bucket}桶: {len(df)} 只  |  更新于 {mtime}")

    def _apply_filters(self):
        df = self._full_df
        if df.empty:
            self._render(df)
            return

        # 机构持仓过滤
        if self.inst_filter.isChecked():
            mask = pd.Series(False, index=df.index)
            for col in INST_COLS:
                if col in df.columns:
                    mask |= (df[col] == "是")
            df = df[mask]

        # 搜索过滤
        text = self.search_edit.text().strip()
        if text:
            mask = pd.Series(False, index=df.index)
            if "code" in df.columns:
                mask |= df["code"].astype(str).str.contains(text, case=False)
            if "name" in df.columns:
                mask |= df["name"].astype(str).str.contains(text, case=False)
            df = df[mask]

        self._render(df)

    def _render(self, df: pd.DataFrame):
        self.table.setSortingEnabled(False)
        self.table.clear()

        if df.empty:
            self.table.setRowCount(0)
            self.table.setColumnCount(0)
            return

        cols = [c for c in df.columns if c not in HIDDEN_COLS]
        self.table.setRowCount(len(df))
        self.table.setColumnCount(len(cols))
        self.table.setHorizontalHeaderLabels(cols)

        for r, (_, row) in enumerate(df.iterrows()):
            for c, col in enumerate(cols):
                val = str(row[col]) if pd.notna(row[col]) else ""
                item = QTableWidgetItem(val)

                # 机构持仓列高亮
                if col in INST_COLS:
                    if val == "是":
                        item.setBackground(Qt.darkGreen)
                        item.setForeground(Qt.white)
                    item.setTextAlignment(Qt.AlignCenter)

                # 数值列：设置排序值
                elif col in NUMERIC_COLS:
                    try:
                        item.setData(Qt.UserRole, float(val))
                    except (ValueError, TypeError):
                        pass
                    item.setTextAlignment(Qt.AlignRight | Qt.AlignVCenter)

                # 代码/名称居中
                elif col in ("code", "price_above_ma60"):
                    item.setTextAlignment(Qt.AlignCenter)

                self.table.setItem(r, c, item)

        self.table.setSortingEnabled(True)

        # 列宽：全部改为可拖拽的 Interactive 模式。
        # （clear() 后 per-section 的 Stretch 模式会按索引残留并错位到别的列，
        #   且 Stretch 模式下用户无法手动拉宽，列多时该列会被压缩到看不见）
        header = self.table.horizontalHeader()
        header.setSectionResizeMode(QHeaderView.Interactive)
        self.table.resizeColumnsToContents()

        # 名称列保底宽度（容纳 4-6 个汉字），用户可自由拖动调整
        if "name" in cols:
            idx = cols.index("name")
            self.table.setColumnWidth(idx, max(110, self.table.columnWidth(idx)))

        # 防止单列过宽挤占空间
        for c in range(len(cols)):
            if self.table.columnWidth(c) > 260:
                self.table.setColumnWidth(c, 260)

    def _on_select(self):
        """选中行时显示明细（pick_reason / inst_detail）。"""
        rows = self.table.selectionModel().selectedRows()
        if not rows:
            self.detail_lbl.setText("")
            return

        row_idx = rows[0].row()
        # 通过 UserRole 获取原始行（排序后的行映射）
        # 简化：直接从表格数据重建
        code_item = self.table.item(row_idx, 0)
        if not code_item:
            return
        code = code_item.text()

        df = self._full_df
        match = df[df["code"].astype(str) == code]
        if match.empty:
            return
        row = match.iloc[0]

        parts = []
        if "inst_detail" in row and pd.notna(row["inst_detail"]):
            parts.append(f"🏛️ 机构持仓: {row['inst_detail']}")
        if "pick_reason" in row and pd.notna(row["pick_reason"]):
            parts.append(f"📋 入选理由: {row['pick_reason']}")

        self.detail_lbl.setText("  |  ".join(parts) if parts else "")

    def _on_context_menu(self, pos):
        """右键菜单：加入监控自选（需在构造时传入 watchlist_store）。"""
        if self.watchlist_store is None:
            return
        row = self.table.rowAt(pos.y())
        if row < 0:
            return
        code_item = self.table.item(row, 0)
        if not code_item:
            return
        code = code_item.text().strip()

        # 从完整数据里补名称（表格 name 列可能被拉伸，用列名定位）
        name = ""
        df = self._full_df
        if not df.empty and "name" in df.columns:
            hit = df[df["code"].astype(str) == code]
            if not hit.empty:
                name = str(hit.iloc[0]["name"])

        already = self.watchlist_store.in_watchlist(code)
        menu = QMenu(self)
        if already:
            act = QAction(f"✅ {name or code} 已在监控池", self)
            act.setEnabled(False)
            menu.addAction(act)
        else:
            act = QAction(f"🎯 加入监控自选（{name or code}）", self)
            act.triggered.connect(
                lambda: self._add_to_watchlist(code, name))
            menu.addAction(act)
        menu.exec_(self.table.viewport().mapToGlobal(pos))

    def _add_to_watchlist(self, code: str, name: str):
        bucket = "ABC"[self.bucket_combo.currentIndex()]
        ok, msg = self.watchlist_store.add(
            code, name, added_from=f"candidates_{bucket}")
        if ok:
            self.stats_lbl.setText(f"✅ {msg}，可在「监控自选」页应用策略")
        else:
            QMessageBox.warning(self, "添加失败", msg)

    def _export(self):
        if self._full_df.empty:
            QMessageBox.information(self, "提示", "没有数据可导出。")
            return
        bucket = "ABC"[self.bucket_combo.currentIndex()]
        default_name = f"candidates_{bucket}_{time.strftime('%Y%m%d')}.xlsx"
        path, _ = QFileDialog.getSaveFileName(
            self, "导出 Excel", default_name, "Excel Files (*.xlsx)")
        if path:
            try:
                self._full_df.to_excel(path, index=False)
                QMessageBox.information(self, "成功", f"已导出到:\n{path}")
            except Exception as e:
                QMessageBox.critical(self, "错误", str(e))
