@echo off
chcp 65001 >nul
REM ============================================================
REM  三桶策略系统 — Android 打包脚本（Avalonia / .NET 10）
REM  用法: build.bat [版本号]        例: build.bat v1.1
REM  产物: dist\ThreeBucket-android-<版本>.apk（debug 签名，可直接安装）
REM  前置: dotnet workload install android + Android SDK + JDK 11/17
REM  正式签名: 设置环境变量 ANDROID_KEYSTORE / ANDROID_KEYSTORE_PASSWORD /
REM            ANDROID_KEY_ALIAS / ANDROID_KEY_PASSWORD 后运行
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

REM ---- 探测 Android SDK（ANDROID_SDK_ROOT / ANDROID_HOME / 常见位置）----
set "SDK_DIR="
if defined ANDROID_SDK_ROOT set "SDK_DIR=%ANDROID_SDK_ROOT%"
if not defined SDK_DIR if defined ANDROID_HOME set "SDK_DIR=%ANDROID_HOME%"
if not defined SDK_DIR if exist "%LOCALAPPDATA%\Android\Sdk\platforms" set "SDK_DIR=%LOCALAPPDATA%\Android\Sdk"
if not defined SDK_DIR if exist "D:\SDK\platforms" set "SDK_DIR=D:\SDK"
if not defined SDK_DIR (
    echo 未找到 Android SDK。两种安装方式任选：
    echo   1. 打开已安装的 Android Studio → More Actions → SDK Manager（默认装到 %%LOCALAPPDATA%%\Android\Sdk）
    echo   2. 命令行: 下载 commandlinetools 解压后
    echo      sdkmanager --sdk_root=%%LOCALAPPDATA%%\Android\Sdk "platform-tools" "platforms;android-36" "build-tools;36.0.0"
    echo 或设置 ANDROID_SDK_ROOT 指向现有 SDK 后重试
    echo 提示: 无本地 SDK 也可用 CI 打包（GitHub Actions → Build Mobile Release → Run workflow）
    exit /b 1
)
echo 使用 Android SDK: %SDK_DIR%

REM ---- 探测 JDK（JAVA_HOME / PATH 中的 java），规避注册表残留的无效路径 ----
set "JDK_DIR="
if defined JAVA_HOME if exist "%JAVA_HOME%\bin\java.exe" set "JDK_DIR=%JAVA_HOME%"
if not defined JDK_DIR (
    for /f "delims=" %%i in ('where java 2^>nul') do (
        if not defined JDK_DIR set "JAVA_BIN=%%i"
    )
)
if defined JAVA_BIN set "JDK_DIR=%JAVA_BIN:\bin\java.exe=%"
if defined JDK_DIR echo 使用 JDK: %JDK_DIR%

set "PROPS=-p:AndroidSdkDirectory="%SDK_DIR%""
if defined JDK_DIR set "PROPS=%PROPS% -p:JavaSdkDirectory="%JDK_DIR%""
REM 显示版本去掉 v 前缀；versionCode 用 1+随机数（≥11，保证升级安装时递增）
set "PROPS=%PROPS% -p:ApplicationDisplayVersion=%VER:v=% -p:ApplicationVersion=1%RANDOM:~-4%"

REM ---- 正式签名（可选）：配置四个环境变量后自动启用 ----
set "SIGN_ARGS="
if defined ANDROID_KEYSTORE (
    if defined ANDROID_KEYSTORE_PASSWORD (
        if defined ANDROID_KEY_ALIAS (
            if defined ANDROID_KEY_PASSWORD (
                set "SIGN_ARGS=-p:AndroidKeyStore=true -p:AndroidSigningKeyStore="%ANDROID_KEYSTORE%" -p:AndroidSigningStorePass="%ANDROID_KEYSTORE_PASSWORD%" -p:AndroidSigningKeyAlias="%ANDROID_KEY_ALIAS%" -p:AndroidSigningKeyPass="%ANDROID_KEY_PASSWORD%""
                echo 使用正式签名: %ANDROID_KEYSTORE%
            )
        )
    )
)
if not defined SIGN_ARGS echo 使用 debug 签名（可直接 adb install；正式发布请配置签名环境变量）

echo.
echo [1/2] 构建 net10.0-android（SignAndroidPackage）...
dotnet build "%PROJ%" -c Release -f net10.0-android -t:SignAndroidPackage %PROPS% %SIGN_ARGS%
if errorlevel 1 (
    echo 构建失败
    exit /b 1
)

echo [2/2] 收集产物 ...
if not exist "%HERE%dist" mkdir "%HERE%dist"
REM 产物名基于 ApplicationId（workbuddy.threebucket-Signed.apk），用通配匹配
set "APK="
for %%f in ("%HERE%..\..\src\ThreeBucket.Mobile\bin\Release\net10.0-android\*-Signed.apk") do set "APK=%%f"
if not defined APK (
    echo 未找到签名 apk，请检查上方构建输出
    exit /b 1
)
copy /y "%APK%" "%HERE%dist\ThreeBucket-android-%VER%.apk" >nul
if errorlevel 1 (
    echo 复制产物失败
    exit /b 1
)

echo.
echo 完成: %HERE%dist\ThreeBucket-android-%VER%.apk
echo debug 签名可直接 adb install 安装; 正式发布请配置签名环境变量
echo iOS ipa 打包请走 CI 工作流 Build Mobile Release 或 Mac 上运行 desktop\ios\build.sh
