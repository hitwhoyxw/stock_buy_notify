@echo off
REM ============================================================
REM  三桶策略系统 — Windows 打包脚本（Avalonia / .NET 10）
REM  用法: build.bat [版本号]        例: build.bat v1.1
REM  产物: dist\ThreeBucket-win-x64-<版本>.zip（自包含单文件，免装 .NET 运行时）
REM  注意：本文件必须保存为 ANSI/GBK 编码（简体中文 Windows 默认）。
REM  切勿使用 UTF-8 + chcp 65001：代码页切换会导致 cmd 字节偏移错位、
REM  脚本死循环重复执行（实测验证）。GBK 编码下无需 chcp，直接正确解析。
REM ============================================================
setlocal
set "VER=%~1"
if "%VER%"=="" set "VER=v1.1"
set "HERE=%~dp0"
set "SRC=%HERE%..\..\src"
set "PUB=%SRC%\ThreeBucket.UI\bin\Release\net10.0\win-x64\publish"

echo ========================================
echo  三桶策略系统 Windows 打包
echo  版本: %VER%
echo  项目根: %SRC%
echo ========================================
echo.

REM 检查 dotnet 是否可用
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [错误] 未找到 dotnet 命令！请先安装 .NET 10 SDK
    echo 下载: https://dotnet.microsoft.com/download
    echo.
    pause
    exit /b 1
)
echo [OK] dotnet 已就绪
dotnet --version
echo.

echo [1/2] dotnet publish win-x64 ...
echo  （首次打包约 2-3 分钟，请耐心等待）
dotnet publish "%SRC%\ThreeBucket.UI\ThreeBucket.UI.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 (
    echo.
    echo [错误] publish 失败！请检查上方的编译错误信息
    echo.
    pause
    exit /b 1
)

echo.
echo [2/2] 压缩产物 ...
if not exist "%HERE%dist" mkdir "%HERE%dist"
powershell -NoProfile -Command "Compress-Archive -Path '%PUB%\*' -DestinationPath '%HERE%dist\ThreeBucket-win-x64-%VER%.zip' -Force"
if errorlevel 1 (
    echo.
    echo [错误] 压缩失败！
    echo.
    pause
    exit /b 1
)

echo.
echo ========================================
echo  打包完成!
echo  产物: %HERE%dist\ThreeBucket-win-x64-%VER%.zip
echo  解压后直接运行 ThreeBucket.UI.exe（无需安装运行时）
echo ========================================
echo.
pause
