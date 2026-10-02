#Requires -Version 7.0
<#
.SYNOPSIS  发布 WebdavSharp（framework-dependent，产物进 artifacts，不入库）。
.DESCRIPTION
  发布后自动裁剪 SQLite 原生库 runtimes/，只保留
  win-x64 / win-arm64 / linux-x64 / linux-arm64
  （musl、osx、win-x86、移动端等不再随包；那些平台请切 PostgreSQL/MySQL，
  启动时 SQLite 原生库缺失会 fail fast 并给出提示）。
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/publish/WebdavSharp"
if ($Runtime) { $out = "$out-$Runtime" }
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$selfContained = $SelfContained.IsPresent
$args = @("publish", (Join-Path $root "src/WebdavSharp.Server/WebdavSharp.Server.csproj"),
    "-c", $Configuration, "-o", $out)
if ($Runtime) { $args += @("-r", $Runtime, "--self-contained", "$selfContained") }

dotnet @args

# 裁剪 SQLite 原生库：只留 win/linux 的 x64+arm64。
# 说明：按 RID 发布时 runtimes/ 本就只含本 RID（原生库放输出根），此步主要为
# 不带 -Runtime 的 portable 发布兜底。
$keep = @("win-x64", "win-arm64", "linux-x64", "linux-arm64")
$rt = Join-Path $out "runtimes"
if (Test-Path $rt) {
    $removed = Get-ChildItem $rt -Directory | Where-Object { $_.Name -notin $keep }
    $removed | Remove-Item -Recurse -Force
    "已裁剪 runtimes/（去掉 {0} 个）：保留 {1}" -f $removed.Count, ($keep -join ", ") | Write-Host
}
"已发布到 $out" | Write-Host
