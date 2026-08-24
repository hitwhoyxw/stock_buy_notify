#!/usr/bin/env bash
# ============================================================
#  三桶策略系统 — Linux 打包脚本（Avalonia / .NET 10）
#  用法: ./build.sh [版本号]        例: ./build.sh v1.1
#  产物: dist/ThreeBucket-linux-x64-<版本>.tar.gz（自包含单文件，免装 .NET 运行时）
#  注：可在任意平台运行本脚本交叉打包（dotnet publish 支持交叉发布）。
# ============================================================
set -euo pipefail
VER="${1:-v1.1}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/../../src"
PUB="$SRC/ThreeBucket.UI/bin/Release/net10.0/linux-x64/publish"

echo "[1/2] dotnet publish linux-x64 ..."
dotnet publish "$SRC/ThreeBucket.UI/ThreeBucket.UI.csproj" -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

echo "[2/2] 打包产物 ..."
mkdir -p "$HERE/dist"
rm -f "$HERE/dist/ThreeBucket-linux-x64-$VER.tar.gz"
tar -czf "$HERE/dist/ThreeBucket-linux-x64-$VER.tar.gz" -C "$PUB" .

echo
echo "完成: $HERE/dist/ThreeBucket-linux-x64-$VER.tar.gz"
echo "解压后 chmod +x ThreeBucket.UI && ./ThreeBucket.UI 运行（无需安装运行时）"
