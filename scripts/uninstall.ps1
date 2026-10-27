#Requires -Version 5.1
<#
.SYNOPSIS
    MemEco をアンインストールします（install.ps1 -Uninstall のラッパー）。

.DESCRIPTION
    タスクスケジューラの "MemEco"、HKCU の自動起動エントリ、スタートメニューの
    ショートカット、インストールフォルダを削除します。
    設定とログ (%APPDATA%\MemEco) は -Purge を指定したときだけ削除します。
    管理者権限が必要なため、必要に応じて UAC で自己昇格します（-NoElevate で抑止）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1 -Purge
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'MemEco\app'),

    [switch]$Purge,

    [switch]$NoElevate
)

$ErrorActionPreference = 'Stop'

$installScript = Join-Path $PSScriptRoot 'install.ps1'
if (-not (Test-Path -LiteralPath $installScript)) {
    Write-Host "install.ps1 が見つかりません: $installScript" -ForegroundColor Red
    exit 1
}

$arguments = @{ Uninstall = $true }
if ($Purge) { $arguments.Purge = $true }
if ($NoElevate) { $arguments.NoElevate = $true }
if ($PSBoundParameters.ContainsKey('InstallDir')) { $arguments.InstallDir = $InstallDir }

& $installScript @arguments
exit $LASTEXITCODE
