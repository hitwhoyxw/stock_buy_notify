@echo off
REM ========================================
REM  三桶策略系统 — PyInstaller 打包脚本
REM  用法: 双击 build.bat 或在命令行运行
REM ========================================

echo ========================================
echo   三桶策略系统 — 打包中...
echo ========================================

REM 安装依赖
pip install PyQt5 pandas openpyxl pyinstaller -q

REM 打包
pyinstaller --noconfirm --onedir --windowed ^
  --name "三桶策略系统" ^
  --add-data "..\scripts;scripts" ^
  --add-data "..\skills;skills" ^
  --add-data "..\trading-system;trading-system" ^
  --add-data "..\data;data" ^
  --hidden-import PyQt5 ^
  --hidden-import pandas ^
  main.py

echo.
echo ========================================
echo   打包完成！
echo   输出目录: dist\三桶策略系统\
echo   双击 dist\三桶策略系统\三桶策略系统.exe 运行
echo ========================================
pause
