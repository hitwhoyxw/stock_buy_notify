"""桌面端内置定时调度器。

到点自动执行 T1+T8（工作日），不依赖 GitHub Actions / 云端。
QTimer 每分钟检查一次，匹配配置的运行时间则触发。
"""
from __future__ import annotations

import datetime as dt
from typing import List, Optional

from PyQt5.QtCore import QObject, QTimer, pyqtSignal


# 北京时间（UTC+8）
_CN_TZ = dt.timezone(dt.timedelta(hours=8))


class TaskScheduler(QObject):
    """内置定时器，到点自动跑任务。

    config 字段:
        scheduler_enabled: bool  — 是否启用
        scheduler_time: "HH:MM"  — 每日运行时间（北京时间）
        scheduler_tasks: ["T1", "T8"]  — 要跑的任务列表
    """

    task_started = pyqtSignal(str)       # task_key
    task_finished = pyqtSignal(str, bool)  # task_key, success
    status_message = pyqtSignal(str)    # 状态栏文案

    def __init__(self, engine, config: dict):
        super().__init__()
        self.engine = engine
        self.config = config
        self._timer = QTimer(self)
        self._timer.timeout.connect(self._tick)
        self._timer.start(60_000)  # 每分钟检查
        self._last_run_date: Optional[dt.date] = None
        self._task_queue: List[str] = []
        self._update_status()

    def _now_cn(self) -> dt.datetime:
        return dt.datetime.now(_CN_TZ)

    def _update_status(self):
        """更新状态栏文案。"""
        if not self.config.get("scheduler_enabled", False):
            self.status_message.emit("定时器未启用")
            return

        run_time = self.config.get("scheduler_time", "16:30")
        tasks = self.config.get("scheduler_tasks", ["T1", "T8"])
        now = self._now_cn()

        # 计算下次运行时间
        hour, minute = map(int, run_time.split(":"))
        next_run = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
        if now >= next_run:
            next_run = next_run + dt.timedelta(days=1)

        # 跳过周末
        while next_run.weekday() >= 5:
            next_run = next_run + dt.timedelta(days=1)

        delta = next_run - now
        hours_left = int(delta.total_seconds() // 3600)
        mins_left = int((delta.total_seconds() % 3600) // 60)

        self.status_message.emit(
            f"定时器: {run_time} {'+'.join(tasks)} | "
            f"下次: {next_run.strftime('%m-%d %H:%M')} "
            f"(还有 {hours_left}h{mins_left}m)"
        )

    def _tick(self):
        """每分钟检查是否到运行时间。"""
        if not self.config.get("scheduler_enabled", False):
            return

        now = self._now_cn()

        # 同一天不重复运行
        if self._last_run_date == now.date():
            return

        run_time = self.config.get("scheduler_time", "16:30")
        hour, minute = map(int, run_time.split(":"))

        if now.hour != hour or now.minute != minute:
            return

        # 跳过周末（周六=5, 周日=6）
        if now.weekday() >= 5:
            return

        self._last_run_date = now.date()
        tasks = self.config.get("scheduler_tasks", ["T1", "T8"])
        self._task_queue = list(tasks)
        self.status_message.emit(f"定时触发: {'+'.join(tasks)} 启动中...")
        self._run_next_task()

    def _run_next_task(self):
        """从队列中取下一个任务执行。"""
        if not self._task_queue:
            self._update_status()
            return

        if self.engine.is_running():
            return  # 上一个还没跑完，等下一轮 _tick

        task_key = self._task_queue.pop(0)
        self.task_started.emit(task_key)
        self.status_message.emit(f"定时运行 {task_key}...")

        self.engine.run(
            task_key,
            on_finished=lambda key, ok, msg: self._on_task_finished(key, ok, msg),
        )

    def _on_task_finished(self, key: str, ok: bool, msg: str):
        """单个任务完成回调。"""
        self.task_finished.emit(key, ok)
        if self._task_queue:
            # 还有后续任务，延迟 2 秒后继续
            QTimer.singleShot(2000, self._run_next_task)
        else:
            self._update_status()

    def reload_config(self, config: dict):
        """设置保存后重新加载配置。"""
        self.config = config
        self._update_status()

    def stop(self):
        """程序退出时停止定时器。"""
        self._timer.stop()
