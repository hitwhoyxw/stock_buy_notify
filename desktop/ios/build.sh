#!/usr/bin/env bash
# ============================================================
#  三桶策略系统 — iOS 打包脚本（必须在 macOS 上运行）
#  用法: ./build.sh [版本号]        例: ./build.sh v1.1
#  产物: dist/ThreeBucket-ios-<版本>.ipa（需开发者证书签名）
#  前置: Xcode + dotnet workload install ios
#  说明: Windows 上仅能编译验证（desktop/build.bat 选 6），
#        ipa 打包与签名依赖 Apple 工具链，必须在 Mac 上进行。
# ============================================================
set -euo pipefail
VER="${1:-v1.1}"
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$HERE/../../src/ThreeBucket.Mobile/ThreeBucket.Mobile.csproj"

echo "[1/2] 构建 net10.0-ios ..."
dotnet build "$PROJ" -c Release -f net10.0-ios

echo "[2/2] 打包 ipa ..."
# 无签名配置时止步于编译；配置 CodesignKey 后可产出 ipa：
#   <PropertyGroup Condition="...ios...">
#     <CodesignKey>iPhone Distribution: Your Name (TEAMID)</CodesignKey>
#   </PropertyGroup>
mkdir -p "$HERE/dist"
dotnet publish "$PROJ" -c Release -f net10.0-ios -p:ArchiveOnBuild=true || {
  echo "ipa 打包失败（通常因未配置签名证书）；编译产物已生成，可先用于模拟器验证"
  exit 1
}
APP="$HERE/../../src/ThreeBucket.Mobile/bin/Release/net10.0-ios/ThreeBucket.Mobile.ipa"
if [ -f "$APP" ]; then
  cp -f "$APP" "$HERE/dist/ThreeBucket-ios-$VER.ipa"
  echo "完成: $HERE/dist/ThreeBucket-ios-$VER.ipa"
else
  echo "未找到 ipa（未配置签名时 publish 只产出 app 目录），产物见 bin/Release/net10.0-ios/"
fi
