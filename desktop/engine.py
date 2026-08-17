"""任务引擎 + 数据管理 + 应用配置。

- TaskInfo / TASKS: T1-T8 任务定义
- TaskWorker: QThread 子线程执行脚本
- TaskEngine: 管理任务执行
- DataManager: 读写 CSV/MD 数据文件
- load_config / save_config: 持久化配置
"""
from __future__ import annotations

import os
import sys
import glob
import time
import json
import subprocess
from dataclasses import dataclass, field
from typing import Optional, List

import pandas as pd
from PyQt5.QtCore import QThread, pyqtSignal


# ============================================================
# 项目根目录 & 配置
# ============================================================

def is_frozen() -> bool:
    """是否在 PyInstaller 打包环境中运行。"""
    return getattr(sys, 'frozen', False) and hasattr(sys, '_MEIPASS')


def detect_project_root() -> str:
    """自动检测项目根目录（含 scripts/ 的目录）。

    打包模式：从 exe 所在目录逐级向上查找 scripts/。
    开发模式：从 __file__ 向上一级查找 scripts/。
    """
    # 打包模式：从 exe 目录向上查找
    if is_frozen():
        exe_dir = os.path.dirname(sys.executable)
        d = exe_dir
        for _ in range(10):
            if os.path.isdir(os.path.join(d, "scripts")):
                return d
            parent = os.path.dirname(d)
            if parent == d:
                break
            d = parent
    else:
        # 开发模式：从 __file__ 向上查找
        here = os.path.dirname(os.path.abspath(__file__))
        root = os.path.dirname(here)
        if os.path.isdir(os.path.join(root, "scripts")):
            return root

    # 检查 cwd
    if os.path.isdir("scripts"):
        return os.getcwd()

    # 兜底：打包用 _MEIPASS（内置只读资源），开发用 __file__ 父目录
    if is_frozen():
        return sys._MEIPASS
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def detect_python() -> str:
    """检测 Python 可执行文件路径。

    打包模式下 sys.executable 是 exe 本身，需要找系统 Python。
    """
    if not is_frozen():
        return sys.executable
    import shutil
    for name in ("python", "python3", "python.exe", "python3.exe"):
        path = shutil.which(name)
        if path:
            return path
    return sys.executable  # 兜底（可能无法运行脚本，但至少不崩）


# 配置文件放在 exe 旁边（打包）或源码旁边（开发），不放进 _MEIPASS 临时目录
if is_frozen():
    _CONFIG_PATH = os.path.join(os.path.dirname(sys.executable), "app_config.json")
else:
    _CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "app_config.json")


def load_config() -> dict:
    if os.path.exists(_CONFIG_PATH):
        try:
            with open(_CONFIG_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {}


def save_config(cfg: dict):
    try:
        with open(_CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2, ensure_ascii=False)
    except Exception:
        pass


# ============================================================
# 任务定义
# ============================================================

@dataclass
class TaskInfo:
    """单个任务的元信息。"""
    key: str
    name: str
    script: str
    description: str
    schedule: str
    needs_llm: bool = False
    default_args: List[str] = field(default_factory=list)
    # LLM 桥接：运行此任务后需要 LLM 处理的 skill 名称
    skill: str = ""


TASKS: dict[str, TaskInfo] = {
    "T1": TaskInfo("T1", "每日风控", "scripts/t1_daily_risk.py",
                   "MA择时、仓位计算、风控检查", "工作日 16:30"),
    "T2": TaskInfo("T2", "周度红利", "scripts/t2_weekly_dividend.py",
                   "红利股息率检查", "周一 08:30"),
    "T3": TaskInfo("T3", "月度再平衡", "scripts/t3_monthly_rebalance.py",
                   "组合再平衡", "每月1日"),
    "T4": TaskInfo("T4", "财报文本扫描", "scripts/t4_ingest.py",
                   "财报抓取 → LLM 景气判定", "财报季",
                   needs_llm=True, skill="T4C",
                   default_args=["--prepare"]),
    "T5": TaskInfo("T5", "季度归因", "scripts/t5_prepare.py",
                   "归因准备 → LLM 分析", "季末",
                   needs_llm=True, skill="T5"),
    "T6": TaskInfo("T6", "候选池筛选", "scripts/t6_candidate_pool.py",
                   "三桶筛选 → LLM 排序", "周一 08:30",
                   needs_llm=True, skill="T6",
                   default_args=["--bucket", "ABC", "--top", "200"]),
    "T7": TaskInfo("T7", "回测验证", "scripts/t7_backtest.py",
                   "策略回测", "月度/季度"),
    "T8": TaskInfo("T8", "信号台账", "scripts/t8_signal_log.py",
                   "信号记录与台账更新", "工作日 17:00"),
}


# ============================================================
# 任务执行器
# ============================================================

class TaskWorker(QThread):
    """在子线程中运行单个脚本，实时输出 stdout。"""
    output = pyqtSignal(str)                          # 实时日志行
    finished_signal = pyqtSignal(str, bool, str)      # task_key, success, message

    def __init__(self, task: TaskInfo, project_root: str,
                 python_exe: str, args: list = None):
        super().__init__()
        self.task = task
        self.project_root = project_root
        self.python_exe = python_exe
        self.args = args if args is not None else list(task.default_args)

    def run(self):
        script = os.path.join(self.project_root, self.task.script)
        if not os.path.exists(script):
            self.finished_signal.emit(
                self.task.key, False, f"脚本不存在: {script}")
            return

        cmd = [self.python_exe, script] + [str(a) for a in self.args]
        self.output.emit(f"$ {' '.join(cmd)}")

        try:
            proc = subprocess.Popen(
                cmd,
                cwd=self.project_root,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )
            for line in proc.stdout:
                self.output.emit(line.rstrip())
            proc.wait()
            ok = proc.returncode == 0
            self.finished_signal.emit(
                self.task.key, ok,
                f"[{self.task.key}] {'✓ 成功' if ok else '✗ 失败'} (exit {proc.returncode})")
        except Exception as e:
            self.finished_signal.emit(
                self.task.key, False, f"执行异常: {e}")


class TaskEngine:
    """任务引擎：管理单任务执行（同一时间只跑一个）。"""

    def __init__(self, project_root: str, python_exe: str = None):
        self.project_root = project_root
        self.python_exe = python_exe or sys.executable
        self.worker: Optional[TaskWorker] = None

    def run(self, task_key: str, args: list = None,
            on_output=None, on_finished=None) -> bool:
        """启动任务，返回是否成功启动。"""
        if self.worker and self.worker.isRunning():
            return False
        task = TASKS.get(task_key)
        if not task:
            return False
        self.worker = TaskWorker(task, self.project_root, self.python_exe, args)
        if on_output:
            self.worker.output.connect(on_output)
        if on_finished:
            self.worker.finished_signal.connect(on_finished)
        self.worker.start()
        return True

    def is_running(self) -> bool:
        return self.worker is not None and self.worker.isRunning()


# ============================================================
# 数据管理
# ============================================================

class DataManager:
    """读写 data/ 目录下的 CSV/MD 文件。"""

    # 需要 LLM 处理的 skill 文件映射（T6 按桶分文件，带 _A/_B/_C 后缀）
    SKILLS = {
        "T4C": {"input": "skill_input_T4C.md", "output": "skill_output_T4C.md",
                "label": "T4 财报文本扫描"},
        "T5":  {"input": "skill_input_T5.md",  "output": "skill_output_T5.md",
                "label": "T5 季度归因"},
        "T6A": {"input": "skill_input_T6_A.md", "output": "skill_output_T6_A.md",
                "label": "T6 A桶·红利逆向"},
        "T6B": {"input": "skill_input_T6_B.md", "output": "skill_output_T6_B.md",
                "label": "T6 B桶·成长"},
        "T6C": {"input": "skill_input_T6_C.md", "output": "skill_output_T6_C.md",
                "label": "T6 C桶·热点周期"},
    }

    def __init__(self, data_dir: str):
        self.data_dir = data_dir

    def set_data_dir(self, path: str):
        self.data_dir = path

    # ── CSV ──

    def load_csv(self, filename: str) -> pd.DataFrame:
        path = os.path.join(self.data_dir, filename)
        if not os.path.exists(path):
            return pd.DataFrame()
        try:
            return pd.read_csv(path)
        except Exception:
            return pd.DataFrame()

    # ── 文本 ──

    def load_text(self, filename: str) -> str:
        path = os.path.join(self.data_dir, filename)
        if not os.path.exists(path):
            return ""
        try:
            with open(path, "r", encoding="utf-8") as f:
                return f.read()
        except Exception:
            return ""

    def save_text(self, filename: str, content: str) -> bool:
        path = os.path.join(self.data_dir, filename)
        try:
            with open(path, "w", encoding="utf-8") as f:
                f.write(content)
            return True
        except Exception:
            return False

    # ── 文件信息 ──

    def file_mtime(self, filename: str) -> str:
        path = os.path.join(self.data_dir, filename)
        if not os.path.exists(path):
            return ""
        return time.strftime("%Y-%m-%d %H:%M:%S",
                             time.localtime(os.path.getmtime(path)))

    def file_size(self, filename: str) -> str:
        path = os.path.join(self.data_dir, filename)
        if not os.path.exists(path):
            return ""
        size = os.path.getsize(path)
        if size > 1024 * 1024:
            return f"{size / 1024 / 1024:.1f} MB"
        if size > 1024:
            return f"{size / 1024:.1f} KB"
        return f"{size} B"

    def exists(self, filename: str) -> bool:
        return os.path.exists(os.path.join(self.data_dir, filename))

    # ── 报告 ──

    def list_reports(self) -> list[str]:
        """列出所有报告文件，按修改时间倒序。"""
        pattern = os.path.join(self.data_dir, "report_*.md")
        files = glob.glob(pattern)
        files.sort(key=os.path.getmtime, reverse=True)
        return files

    # ── Skill 文件 ──

    def skill_input_text(self, skill: str) -> str:
        info = self.SKILLS.get(skill)
        if not info:
            return ""
        return self.load_text(info["input"])

    def skill_output_text(self, skill: str) -> str:
        info = self.SKILLS.get(skill)
        if not info:
            return ""
        return self.load_text(info["output"])

    def save_skill_output(self, skill: str, content: str) -> bool:
        info = self.SKILLS.get(skill)
        if not info:
            return False
        return self.save_text(info["output"], content)

    # ── 持仓 & 净值 ──

    def load_positions(self) -> pd.DataFrame:
        """从 live_trade_log.csv 解析当前持仓（dtype=str 读，保留代码前导零）。

        返回列：代码, 名称, 桶, 申万一级行业, 净股数, 累计成本金额, 平均成本
        """
        df = self._read_trade_csv()
        if df.empty:
            return pd.DataFrame(columns=["代码", "名称", "桶", "申万一级行业",
                                         "净股数", "累计成本金额", "平均成本"])
        for c in ["股数", "金额"]:
            if c in df.columns:
                df[c] = pd.to_numeric(df[c], errors="coerce").fillna(0)
        df["signed_shares"] = df.apply(
            lambda r: r.get("股数", 0) if str(r.get("方向", "")).strip() in ("买入", "buy", "BUY")
            else -r.get("股数", 0), axis=1)
        df["signed_amount"] = df.apply(
            lambda r: r.get("金额", 0) if str(r.get("方向", "")).strip() in ("买入", "buy", "BUY")
            else -r.get("金额", 0), axis=1)
        agg = df.groupby("代码").agg(
            名称=("名称", "last"),
            桶=("桶", "last"),
            申万一级行业=("申万一级行业", "last"),
            净股数=("signed_shares", "sum"),
            累计成本金额=("signed_amount", "sum"),
        ).reset_index()
        agg = agg[agg["净股数"] > 0].copy()
        agg["平均成本"] = agg["累计成本金额"] / agg["净股数"].replace(0, pd.NA)
        return agg

    def load_nav(self) -> pd.DataFrame:
        """读取 portfolio_nav.csv 净值序列。"""
        return self.load_csv("portfolio_nav.csv")

    def bucket_weights(self) -> dict:
        """按累计成本计算四桶占比。"""
        pos = self.load_positions()
        weights = {"A": 0.0, "B": 0.0, "C": 0.0, "D": 0.0}
        if pos.empty:
            return weights
        total = float(pos["累计成本金额"].sum())
        if total <= 0:
            return weights
        for _, row in pos.iterrows():
            b = str(row.get("桶", "")).strip().upper()
            if b in weights:
                weights[b] += float(row["累计成本金额"]) / total
        return weights

    # ── 交易日志 CRUD ──

    TRADE_COLUMNS = [
        "日期", "方向", "桶", "代码", "名称", "申万一级行业",
        "价格", "股数", "金额", "占总资产%", "触发规则ID",
        "触发时指标值", "阈值", "决策理由(一句话)", "当时组合状态",
        "当时四桶权重ABCD", "情绪自评(1-5)", "是否违反纪律",
        "事后30日涨跌%", "事后90日涨跌%", "复盘结论",
    ]

    def _trade_log_path(self) -> str:
        return os.path.join(self.data_dir, "live_trade_log.csv")

    def _read_trade_csv(self) -> pd.DataFrame:
        """dtype=str 读交易日志，保持代码前导零（000001 等）。"""
        path = self._trade_log_path()
        if not os.path.exists(path):
            return pd.DataFrame(columns=self.TRADE_COLUMNS)
        try:
            df = pd.read_csv(path, dtype=str, keep_default_na=False)
            for c in self.TRADE_COLUMNS:
                if c not in df.columns:
                    df[c] = ""
            return df[self.TRADE_COLUMNS]
        except Exception:
            return pd.DataFrame(columns=self.TRADE_COLUMNS)

    def read_trades(self) -> pd.DataFrame:
        """读取完整交易日志（流水，非聚合持仓）。"""
        return self._read_trade_csv()

    def _write_trade_csv(self, df: pd.DataFrame) -> bool:
        try:
            df.to_csv(self._trade_log_path(), index=False, encoding="utf-8-sig")
            return True
        except Exception:
            return False

    def append_trade(self, record: dict) -> bool:
        """追加一条交易记录。record 至少含 日期/方向/桶/代码/价格/股数/金额。"""
        df = self._read_trade_csv()
        row = {c: str(record.get(c, "")) for c in self.TRADE_COLUMNS}
        df = pd.concat([df, pd.DataFrame([row])], ignore_index=True)
        return self._write_trade_csv(df)

    def update_trade(self, row_index: int, record: dict) -> bool:
        """更新指定行（0-based，对应 read_trades() 顺序）。"""
        df = self._read_trade_csv()
        if row_index < 0 or row_index >= len(df):
            return False
        for c in self.TRADE_COLUMNS:
            if c in record:
                df.iat[row_index, df.columns.get_loc(c)] = str(record.get(c, ""))
        return self._write_trade_csv(df)

    def delete_trade(self, row_index: int) -> bool:
        """删除指定行。"""
        df = self._read_trade_csv()
        if row_index < 0 or row_index >= len(df):
            return False
        df = df.drop(df.index[row_index]).reset_index(drop=True)
        return self._write_trade_csv(df)

    def shares_of(self, code: str) -> float:
        """查询某代码当前净持仓股数（用于录入卖出时提示）。"""
        pos = self.load_positions()
        if pos.empty:
            return 0.0
        code = code.split(".")[0].zfill(6)
        hit = pos[pos["代码"].astype(str).str.zfill(6) == code]
        return float(hit["净股数"].sum()) if not hit.empty else 0.0
