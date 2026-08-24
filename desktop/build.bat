@echo off
chcp 65001 >nul
REM ============================================================
REM  三桶策略系统 — 构建中心（选择平台打包）
REM  用法: 双击进入菜单；或命令行直达: build.bat ^<1-6^> [版本号]
REM    1=Windows  2=Linux  3=macOS  4=全部桌面  5=Android  6=iOS编译验证
REM ============================================================
setlocal
set "CHOICE=%~1"
set "VER=%~2"
if "%VER%"=="" set "VER=v1.1"
if "%CHOICE%"=="" (set "MENUMODE=1") else (set "MENUMODE=")

:menu
if defined MENUMODE (
    cls
    echo ==============================================
    echo    三桶策略系统 — 构建中心     版本: %VER%
    echo ==============================================
    echo   [1] Windows    win-x64 zip（自包含）
    echo   [2] Linux      linux-x64 tar.gz（自包含）
    echo   [3] macOS      osx-arm64 + osx-x64 tar.gz
    echo   [4] 全部桌面平台（1+2+3）
    echo   [5] Android    apk（需已装 android 工作负载）
    echo   [6] iOS        编译验证（出 ipa 需 Mac，见 desktop\ios\build.sh）
    echo   [0] 退出
    echo ==============================================
    set /p CHOICE=请选择:
)

if "%CHOICE%"=="1" (call "%~dp0windows\build.bat" %VER% & goto end)
if "%CHOICE%"=="2" (call :linux %VER% & goto end)
if "%CHOICE%"=="3" (call :macos %VER% & goto end)
if "%CHOICE%"=="4" (call "%~dp0windows\build.bat" %VER% & call :linux %VER% & call :macos %VER% & goto end)
if "%CHOICE%"=="5" (call "%~dp0android\build.bat" %VER% & goto end)
if "%CHOICE%"=="6" (call :iosbuild & goto end)
if "%CHOICE%"=="0" goto end
if not defined MENUMODE (
    echo 无效选择: %CHOICE%（应为 0-6）
    goto end
)
set "CHOICE="
goto menu

:linux
echo.
echo === 构建 linux-x64 ===
dotnet publish "%~dp0..\..\src\ThreeBucket.UI\ThreeBucket.UI.csproj" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 ( echo linux 构建失败 & exit /b 1 )
if not exist "%~dp0linux\dist" mkdir "%~dp0linux\dist"
tar -czf "%~dp0linux\dist\ThreeBucket-linux-x64-%~1.tar.gz" -C "%~dp0..\..\src\ThreeBucket.UI\bin\Release\net10.0\linux-x64\publish" .
echo 完成: %~dp0linux\dist\ThreeBucket-linux-x64-%~1.tar.gz
goto :eof

:macos
echo.
echo === 构建 osx-arm64 ===
dotnet publish "%~dp0..\..\src\ThreeBucket.UI\ThreeBucket.UI.csproj" -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 ( echo macOS 构建失败 & exit /b 1 )
echo === 构建 osx-x64 ===
dotnet publish "%~dp0..\..\src\ThreeBucket.UI\ThreeBucket.UI.csproj" -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if errorlevel 1 ( echo macOS 构建失败 & exit /b 1 )
if not exist "%~dp0macos\dist" mkdir "%~dp0macos\dist"
tar -czf "%~dp0macos\dist\ThreeBucket-osx-arm64-%~1.tar.gz" -C "%~dp0..\..\src\ThreeBucket.UI\bin\Release\net10.0\osx-arm64\publish" .
tar -czf "%~dp0macos\dist\ThreeBucket-osx-x64-%~1.tar.gz" -C "%~dp0..\..\src\ThreeBucket.UI\bin\Release\net10.0\osx-x64\publish" .
echo 完成: %~dp0macos\dist\ThreeBucket-osx-{arm64,x64}-%~1.tar.gz
goto :eof

:iosbuild
echo.
echo === iOS 编译验证 ===
echo （Windows 只能编译验证；完整 ipa 打包/签名须在 macOS 上运行 desktop\ios\build.sh）
dotnet build "%~dp0..\..\src\ThreeBucket.Mobile\ThreeBucket.Mobile.csproj" -c Release -f net10.0-ios
if errorlevel 1 ( echo iOS 编译失败 & exit /b 1 )
echo iOS 编译通过 ✓
goto :eof

:end
if defined MENUMODE pause
endlocal
