#Requires -Version 7.0
<#
.SYNOPSIS  发布 WebdavSharp（framework-dependent，产物进 artifacts，不入库）。
.DESCRIPTION
  NovaDb 默认实现为纯托管代码，无 SQLite 原生库依赖；发布 RID 可按目标平台选择。
  若切换到 SQLite，需确保对应平台的 SQLitePCLRaw 原生运行库随包发布。
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

"已发布到 $out" | Write-Host
