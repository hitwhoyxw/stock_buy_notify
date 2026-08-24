#!/usr/bin/env bash
# ============================================================
#  三桶策略系统 — macOS 打包脚本（Avalonia / .NET 10）
#  用法: ./build.sh [版本号]        例: ./build.sh v1.1
#  产物: dist/ThreeBucket-osx-arm64-<版本>.tar.gz（Apple Silicon）
#        dist/ThreeBucket-osx-x64-<版本>.tar.gz（Intel）
#  注：可在任意平台运行本脚本交叉打包（dotnet publish 支持交叉发布）。
# ============================================================
set -euo pipefail
VER="${1:-v1.1}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/../../src"
mkdir -p "$HERE/dist"

for RID in osx-arm64 osx-x64; do
  echo "[$RID] dotnet publish ..."
  dotnet publish "$SRC/ThreeBucket.UI/ThreeBucket.UI.csproj" -c Release -r "$RID" \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
  PUB="$SRC/ThreeBucket.UI/bin/Release/net10.0/$RID/publish"
  rm -f "$HERE/dist/ThreeBucket-$RID-$VER.tar.gz"
  tar -czf "$HERE/dist/ThreeBucket-$RID-$VER.tar.gz" -C "$PUB" .
  echo "[$RID] 完成: $HERE/dist/ThreeBucket-$RID-$VER.tar.gz"
done

echo
echo "解压后运行 ./ThreeBucket.UI（无需安装运行时；首次可能需 xattr -dr com.apple.quarantine .）"
