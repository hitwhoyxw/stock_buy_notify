@echo off
chcp 65001 >nul
REM ============================================================
REM  三桶策略系统 — Android 打包脚本（Avalonia / .NET 10）
REM  用法: build.bat [版本号]        例: build.bat v1.1
REM  产物: dist\ThreeBucket-android-<版本>.apk（debug 签名，可直接安装）
REM  前置: dotnet workload install android + Android SDK
REM        （Visual Studio 装"使用 .NET 的移动开发"即可）
REM ============================================================
setlocal
set "VER=%~1"
if "%VER%"=="" set "VER=v1.1"
set "HERE=%~dp0"
set "PROJ=%HERE%..\..\src\ThreeBucket.Mobile\ThreeBucket.Mobile.csproj"

dotnet workload list 2>nul | findstr /I /C:"android" >nul
if errorlevel 1 (
    echo 未检测到 android 工作负载，请先运行: dotnet workload install android
    exit /b 1
)

echo [1/2] 构建 net10.0-android（SignAndroidPackage 用 debug 密钥签名）...
dotnet build "%PROJ%" -c Release -f net10.0-android -t:SignAndroidPackage
if errorlevel 1 (
    echo 构建失败
    exit /b 1
)

echo [2/2] 收集产物 ...
if not exist "%HERE%dist" mkdir "%HERE%dist"
copy /y "%HERE%..\..\src\ThreeBucket.Mobile\bin\Release\net10.0-android\ThreeBucket.Mobile-Signed.apk" "%HERE%dist\ThreeBucket-android-%VER%.apk" >nul
if errorlevel 1 (
    echo 未找到签名 apk，请检查上方构建输出
    exit /b 1
)

echo.
echo 完成: %HERE%dist\ThreeBucket-android-%VER%.apk
echo debug 签名可直接 adb install 安装；正式发布请自行替换签名密钥。
