@echo off
chcp 65001 >nul
REM ========================================
REM  三桶策略系统 — PyInstaller 打包脚本
REM  用法: 双击 build.bat 或在命令行运行
REM ========================================

echo ========================================
echo   三桶策略系统 — 打包中...
echo ========================================

REM 安装依赖
pip install PyQt5 pandas openpyxl pyinstaller -q

REM 第一步：用英文名生成 .spec（避免命令行中文编码问题）
pyinstaller --noconfirm --onedir --windowed ^
  --name "ThreeBucketStrategy" ^
  --add-data "..\scripts;scripts" ^
  --add-data "..\skills;skills" ^
  --add-data "..\trading-system;trading-system" ^
  --add-data "..\data;data" ^
  --hidden-import PyQt5 ^
  --hidden-import pandas ^
  main.py

REM 第二步：把 .spec 里的英文名改成中文
powershell -Command "(Get-Content 'ThreeBucketStrategy.spec' -Encoding UTF8) -replace 'name=''ThreeBucketStrategy''', 'name=''三桶策略系统''' | Set-Content 'ThreeBucketStrategy.spec' -Encoding UTF8"

REM 第三步：用修改后的 .spec 重新打包（生成中文名的 exe）
pyinstaller --noconfirm ThreeBucketStrategy.spec

echo.
echo ========================================
echo   打包完成！
echo   输出目录: dist\三桶策略系统\
echo   双击 dist\三桶策略系统\三桶策略系统.exe 运行
echo ========================================
pause