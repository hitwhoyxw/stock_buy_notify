@echo off
chcp 65001 >nul
REM ============================================================
REM  三桶策略系统 — Windows 打包脚本（Avalonia / .NET 10）
REM  用法: build.bat [版本号]        例: build.bat v1.1
REM  产物: dist\ThreeBucket-win-x64-<版本>.zip（自包含单文件，免装 .NET 运行时）
REM ============================================================
setlocal
set "VER=%~1"
if "%VER%"=="" set "VER=v1.1"
set "HERE=%~dp0"
set "SRC=%HERE%..\..\src"
set "PUB=%SRC%\ThreeBucket.UI\bin\Release\net10.0\win-x64\publish"

echo [1/2] dotnet publish win-x64 ...
dotnet publish "%SRC%\ThreeBucket.UI\ThreeBucket.UI.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 (
    echo publish 失败
    exit /b 1
)

echo [2/2] 压缩产物 ...
if not exist "%HERE%dist" mkdir "%HERE%dist"
powershell -NoProfile -Command "Compress-Archive -Path '%PUB%\*' -DestinationPath '%HERE%dist\ThreeBucket-win-x64-%VER%.zip' -Force"
if errorlevel 1 (
    echo 压缩失败
    exit /b 1
)

echo.
echo 完成: %HERE%dist\ThreeBucket-win-x64-%VER%.zip
echo 解压后直接运行 ThreeBucket.UI.exe（无需安装运行时，无控制台黑窗）
