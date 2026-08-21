"""三桶策略系统 — Windows 桌面端入口。

用法：
    python desktop/main.py

打包：
    pyinstaller desktop/build.spec  （或运行 desktop/build.bat）
"""
from __future__ import annotations

import os
import sys

# 确保 desktop/ 目录在 sys.path 中（同级导入）
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

from PyQt5.QtWidgets import QApplication, QSystemTrayIcon
from PyQt5.QtCore import Qt

# 高 DPI 适配
try:
    QApplication.setAttribute(Qt.AA_EnableHighDpiScaling, True)
    QApplication.setAttribute(Qt.AA_UseHighDpiPixmaps, True)
except Exception:
    pass


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("三桶策略系统")
    app.setOrganizationName("WorkBuddy")

    # 全局字体
    font = app.font()
    font.setFamily("Microsoft YaHei UI")
    font.setPointSize(9)
    app.setFont(font)

    from main_window import MainWindow

    win = MainWindow()
    win.show()
    win.show_tray()

    # 托盘消息
    win.tray.showMessage(
        "三桶策略系统",
        "桌面端已启动。可在「设置」页配置项目路径。",
        QSystemTrayIcon.Information,
        3000,
    )

    sys.exit(app.exec_())


if __name__ == "__main__":
    main()
