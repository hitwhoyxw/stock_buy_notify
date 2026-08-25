#!/usr/bin/env bash
# ============================================================
#  三桶策略系统 — iOS 打包脚本（必须在 macOS 上运行）
#  用法: ./build.sh [版本号]        例: ./build.sh v1.1
#  产物: dist/ThreeBucket-ios-<版本>.ipa
#  签名: 默认 ad-hoc（CodesignKey=-），无需 Apple 开发者账号；
#        有账号时 export CODESIGN_KEY="Apple Distribution" 走正式签名
#  前置: Xcode + dotnet workload install ios
#  说明: Windows 上仅能编译验证（desktop/build.bat 选 6），
#        无 Mac 时用 CI 打包：GitHub Actions → Build Mobile Release
# ============================================================
set -euo pipefail
VER="${1:-v1.1}"
HERE="$(cd "$(dirname "$0")" && pwd)"
PROJ="$HERE/../../src/ThreeBucket.Mobile/ThreeBucket.Mobile.csproj"
DISPLAY="${VER#v}"

# 正式签名（可选）：export CODESIGN_KEY="iPhone Distribution: ..." 后自动启用
SIGN_ARGS=(-p:CodesignKey=-)
if [ -n "${CODESIGN_KEY:-}" ]; then
  echo "使用正式签名: $CODESIGN_KEY"
  SIGN_ARGS=(-p:CodesignKey="$CODESIGN_KEY")
else
  echo "使用 ad-hoc 签名（产物需 Sideloadly/爱思助手 重签后安装）"
fi

echo "[1/2] 构建 net10.0-ios ..."
dotnet build "$PROJ" -c Release -f net10.0-ios \
  -p:ApplicationDisplayVersion="$DISPLAY" -p:ApplicationVersion="$(( $(date +%s) / 10 % 1000000 ))"

echo "[2/2] 打包 ipa ..."
mkdir -p "$HERE/dist"
dotnet publish "$PROJ" -c Release -f net10.0-ios -p:ArchiveOnBuild=true \
  -p:ApplicationDisplayVersion="$DISPLAY" -p:ApplicationVersion="$(( $(date +%s) / 10 % 1000000 ))" \
  "${SIGN_ARGS[@]}" || {
  echo "ipa 打包失败（通常因签名配置问题）；编译产物已生成，可先用于模拟器验证"
  exit 1
}

BIN="$HERE/../../src/ThreeBucket.Mobile/bin/Release/net10.0-ios"
IPA=$(ls "$BIN"/*.ipa 2>/dev/null | head -1)
if [ -n "$IPA" ]; then
  cp -f "$IPA" "$HERE/dist/ThreeBucket-ios-$VER.ipa"
  echo "完成: $HERE/dist/ThreeBucket-ios-$VER.ipa"
elif ls "$BIN"/*.app >/dev/null 2>&1; then
  APP=$(ls -d "$BIN"/*.app | head -1)
  ditto -c -k --keepParent "$APP" "$HERE/dist/ThreeBucket-ios-$VER.app.zip"
  echo "未生成 ipa，已收集 .app: $HERE/dist/ThreeBucket-ios-$VER.app.zip（可用 Sideloadly 侧载）"
else
  echo "未找到构建产物，请检查上方输出"
  exit 1
fi
