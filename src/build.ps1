# =============================================================================
#  三桶策略系统 · 跨平台打包脚本 (PowerShell, Windows 原生)
#
#  用法:
#    .\build.ps1                     构建当前平台 (Windows -> win-x64)
#    .\build.ps1 -Target win-x64     仅构建 Windows x64
#    .\build.ps1 -All                构建全部: win-x64 osx-x64 osx-arm64 linux-x64
#    .\build.ps1 -SelfContained      内嵌 .NET 运行时 (目标机无需安装 .NET)
#    .\build.ps1 -Run                构建后直接运行桌面端预览
#    .\build.ps1 -Trim               开启 IL 裁剪 (仅 self-contained 生效)
#
#  组合示例:
#    .\build.ps1 -All -SelfContained
#    .\build.ps1 -Target win-x64 -SelfContained -Run
# =============================================================================
[CmdletBinding()]
param(
  [string[]] $Target = @(),
  [switch]   $All,
  [switch]   $SelfContained,
  [switch]   $Run,
  [switch]   $Trim,
  [string]   $Config = "Release"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Push-Location $ScriptDir

$Project  = "ThreeBucket.UI/ThreeBucket.UI.csproj"
$OutBase  = "publish"

# ---- 解析目标 ----------------------------------------------------------------
$Targets = @()
if ($All) {
  $Targets = @("win-x64", "osx-x64", "osx-arm64", "linux-x64")
} elseif ($Target.Count -gt 0) {
  $Targets = $Target
}

if ($Targets.Count -eq 0) {
  # 当前平台: 这里只处理 Windows, 其他平台请用 build.sh
  $Targets = @("win-x64")
}

$SelfArg = if ($SelfContained) { "--self-contained" } else { "" }
$TrimArg = if ($Trim) { "-p:PublishTrimmed=true", "-p:TrimMode=link" } else { @() }

Write-Host "==> 目标框架: net10.0 | 配置: $Config"
if ($SelfContained) { Write-Host "==> 模式: self-contained (内嵌 .NET 运行时)" }
else { Write-Host "==> 模式: framework-dependent (目标机需 .NET 10 运行时)" }
Write-Host "==> 目标平台: $($Targets -join ', ')"
Write-Host ""

foreach ($rid in $Targets) {
  $out = Join-Path $OutBase $rid
  Write-Host ("=" * 70)
  Write-Host ">>> [$rid] 发布中 ..."
  Write-Host ("=" * 70)
  $args = @("publish", $Project, "-c", $Config, "-r", $rid) +
          @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", $out)
  if ($SelfArg) { $args += $SelfArg }
  $args += $TrimArg
  & dotnet @args
  if ($LASTEXITCODE -ne 0) { throw "发布失败: $rid" }
  Write-Host ">>> [$rid] 完成 -> $ScriptDir\$out"
  Write-Host ""
}

# ---- 构建后运行 --------------------------------------------------------------
if ($Run) {
  if ($Targets.Count -ne 1) {
    Write-Warning "--Run 仅对单个目标生效，已跳过自动运行。"
  } else {
    $rid = $Targets[0]
    $exe = Join-Path $OutBase $rid "ThreeBucket.UI"
    if ($rid -eq "win-x64") { $exe = "$exe.exe" }
    if (Test-Path $exe) {
      Write-Host ">>> 运行: $exe"
      & $exe
    } else {
      throw "未找到可执行文件: $exe"
    }
  }
}

Write-Host "==> 全部完成。"
