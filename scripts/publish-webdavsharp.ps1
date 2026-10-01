#Requires -Version 7.0
<#
.SYNOPSIS  发布 WebdavSharp（产物进 artifacts，不入库）。
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts/publish/WebdavSharp"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

$args = @("publish", (Join-Path $root "src/WebdavSharp.Server/WebdavSharp.Server.csproj"),
    "-c", $Configuration, "-o", $out)
if ($Runtime) { $args += @("-r", $Runtime, "--self-contained", "true") }

dotnet @args
"已发布到 $out" | Write-Host
