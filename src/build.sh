#!/usr/bin/env bash
# =============================================================================
#  三桶策略系统 · 跨平台打包脚本 (bash)
#  适用于 Git Bash / Linux / macOS
#
#  用法:
#    ./build.sh                     构建当前平台的桌面端并输出到 publish/<rid>/
#    ./build.sh win-x64             仅构建 Windows x64
#    ./build.sh osx-arm64           仅构建 macOS Apple Silicon
#    ./build.sh linux-x64           仅构建 Linux x64
#    ./build.sh --all               构建全部: win-x64 osx-x64 osx-arm64 linux-x64
#    ./build.sh --self-contained    内嵌 .NET 运行时 (目标机无需安装 .NET)
#    ./build.sh --run               构建当前平台后直接运行 (桌面端预览)
#    ./build.sh --trim              开启 IL 裁剪, 进一步缩小体积 (仅 self-contained 生效)
#
#  组合示例:
#    ./build.sh --all --self-contained
#    ./build.sh win-x64 --self-contained --run
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

PROJECT="ThreeBucket.UI/ThreeBucket.UI.csproj"
CONFIG="Release"
OUT_BASE="publish"

SELF_CONTAINED=""   # 默认 framework-dependent (目标机需装 .NET 10 运行时)
TRIM=""             # 默认不裁剪
RUN_AFTER=0
TARGETS=()

# ---- 解析参数 ----------------------------------------------------------------
while [[ $# -gt 0 ]]; do
  case "$1" in
    --all)            TARGETS=(win-x64 osx-x64 osx-arm64 linux-x64) ;;
    --self-contained) SELF_CONTAINED="--self-contained" ;;
    --trim)           TRIM="-p:PublishTrimmed=true -p:TrimMode=link" ;;
    --run)            RUN_AFTER=1 ;;
    --help|-h)
      grep '^#' "$0" | sed 's/^#\{1,2\} //'
      exit 0 ;;
    win-x64|osx-x64|osx-arm64|linux-x64)
      TARGETS+=("$1") ;;
    *)
      echo "未知参数: $1" >&2
      echo "用 ./build.sh --help 查看用法" >&2
      exit 1 ;;
  esac
  shift
done

# ---- 默认目标: 当前平台 ------------------------------------------------------
if [[ ${#TARGETS[@]} -eq 0 ]]; then
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT)
      TARGETS=(win-x64) ;;
    Darwin)
      if [[ "$(uname -m)" == "arm64" ]]; then TARGETS=(osx-arm64); else TARGETS=(osx-x64); fi ;;
    Linux)
      TARGETS=(linux-x64) ;;
    *)
      echo "无法确定当前平台 RID，请显式指定，如: ./build.sh win-x64" >&2
      exit 1 ;;
  esac
fi

# ---- 构建每个目标 ------------------------------------------------------------
echo "==> 目标框架: net10.0 | 配置: $CONFIG"
[[ -n "$SELF_CONTAINED" ]] && echo "==> 模式: self-contained (内嵌 .NET 运行时)"
[[ -z "$SELF_CONTAINED" ]] && echo "==> 模式: framework-dependent (目标机需 .NET 10 运行时)"
echo "==> 目标平台: ${TARGETS[*]}"
echo

for rid in "${TARGETS[@]}"; do
  out="$OUT_BASE/$rid"
  echo "----------------------------------------------------------------------"
  echo ">>> [$rid] 发布中 ..."
  echo "----------------------------------------------------------------------"
  dotnet publish "$PROJECT" \
    -c "$CONFIG" \
    -r "$rid" \
    $SELF_CONTAINED \
    $TRIM \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$out"
  echo ">>> [$rid] 完成 -> $SCRIPT_DIR/$out"
  echo
done

# ---- 构建后直接运行 (仅单个桌面目标时生效) ------------------------------------
if [[ $RUN_AFTER -eq 1 ]]; then
  if [[ ${#TARGETS[@]} -ne 1 ]]; then
    echo "提示: --run 仅对单个目标生效，已跳过自动运行。" >&2
  else
    rid="${TARGETS[0]}"
    exe="$SCRIPT_DIR/$OUT_BASE/$rid/ThreeBucket.UI"
    [[ "$rid" == win-x64 ]] && exe="$exe.exe"
    if [[ -f "$exe" ]]; then
      echo ">>> 运行: $exe"
      "$exe"
    else
      echo "未找到可执行文件: $exe" >&2
      exit 1
    fi
  fi
fi

echo "==> 全部完成。"
