#Requires -Version 5.1
<#
.SYNOPSIS
    MemEco をインストール / アンインストールします。

.DESCRIPTION
    インストール（既定）:
      1. ビルド済み exe を %LOCALAPPDATA%\MemEco\app へコピー（publish があれば優先）
      2. 既存の MemEco プロセスを停止
      3. ログオン時のタスクを登録（schtasks /SC ONLOGON /RL HIGHEST）→ 管理者モードで自動起動
      4. HKCU\...\Run の "MemEco" を削除（二重起動防止）
      5. スタートメニューにショートカットを作成
    管理者権限が必要なため、必要に応じて UAC で自己昇格します（-NoElevate で抑止）。

    アンインストール (-Uninstall):
      タスク削除 / Run キー削除 / ショートカット削除 / インストールフォルダ削除。
      設定 (%APPDATA%\MemEco) は -Purge を指定したときだけ削除します。

    ※ このスクリプトはビルドをしません。先に scripts\build.ps1 を実行してください。

.PARAMETER InstallDir
    インストール先。既定は %LOCALAPPDATA%\MemEco\app。

.PARAMETER Uninstall
    アンインストールします。

.PARAMETER Purge
    -Uninstall 時、設定とログ (%APPDATA%\MemEco) も削除します。

.PARAMETER NoElevate
    UAC による自己昇格を行いません。管理者でない場合、タスク登録はスキップされます。

.PARAMETER NoShortcut
    スタートメニューのショートカットを作成しません。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Uninstall -Purge
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'MemEco\app'),

    [switch]$Uninstall,

    [switch]$Purge,

    [switch]$NoElevate,

    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'

$taskName = 'MemEco'
$defaultInstallDir = Join-Path $env:LOCALAPPDATA 'MemEco\app'
$repoRoot = Split-Path -Parent $PSScriptRoot
$settingsDir = Join-Path $env:APPDATA 'MemEco'
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

$installDirWasSpecified = $PSBoundParameters.ContainsKey('InstallDir')
if (-not $installDirWasSpecified) {
    $InstallDir = $defaultInstallDir
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Message)
    Write-Host "    OK  $Message" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Message)
    Write-Host "    !!  $Message" -ForegroundColor Yellow
}

function Write-Info {
    param([string]$Message)
    Write-Host "        $Message" -ForegroundColor DarkGray
}

# ------------------------------------------------------------ 昇格前チェック
# ビルド成果物が無いのに UAC を出さないよう、先に確認する。
if (-not $Uninstall) {
    $prePublishExe = Join-Path $repoRoot 'publish\MemEco.exe'
    $preBinExe = Join-Path $repoRoot 'src\MemEco.App\bin\Release\net9.0-windows\MemEco.exe'
    if ((-not (Test-Path -LiteralPath $prePublishExe)) -and (-not (Test-Path -LiteralPath $preBinExe))) {
        Write-Host 'ビルド済みの MemEco.exe が見つかりません。' -ForegroundColor Red
        Write-Host '先にビルドしてください:' -ForegroundColor Yellow
        Write-Host '  powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1' -ForegroundColor Yellow
        exit 1
    }
}

# ------------------------------------------------------------ 管理者へ自己昇格
$isAdmin = Test-IsAdmin
if (-not $isAdmin -and -not $NoElevate) {
    Write-Host 'MemEco セットアップ' -ForegroundColor White
    Write-Host '管理者権限が必要なため、UAC で昇格して再起動します...' -ForegroundColor Yellow

    $hostExe = 'powershell.exe'
    $resolved = Get-Command 'powershell.exe' -ErrorAction SilentlyContinue
    if ($resolved) {
        $hostExe = $resolved.Source
    }

    $argumentLine = @(
        '-NoProfile'
        '-ExecutionPolicy'
        'Bypass'
        '-File'
        ('"{0}"' -f $PSCommandPath)
    )
    if ($Uninstall) { $argumentLine += '-Uninstall' }
    if ($Purge) { $argumentLine += '-Purge' }
    if ($NoShortcut) { $argumentLine += '-NoShortcut' }
    if ($installDirWasSpecified) {
        $argumentLine += '-InstallDir'
        $argumentLine += ('"{0}"' -f $InstallDir)
    }

    try {
        $elevated = Start-Process -FilePath $hostExe -Verb RunAs -ArgumentList ($argumentLine -join ' ') -Wait -PassThru
        exit $elevated.ExitCode
    }
    catch {
        Write-Host ''
        Write-Host '昇格がキャンセルされたか、開始できませんでした。' -ForegroundColor Red
        Write-Host '管理者として PowerShell を開き直して実行してください。' -ForegroundColor Yellow
        Write-Host "  $PSCommandPath" -ForegroundColor Yellow
        exit 1
    }
}

function Stop-MemEcoProcesses {
    $procs = @(Get-Process -Name 'MemEco' -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) {
        return 0
    }
    Write-Info "$($procs.Count) 個の MemEco を停止します"
    foreach ($p in $procs) {
        try { $null = $p.CloseMainWindow() } catch { }
    }
    Start-Sleep -Milliseconds 700
    foreach ($p in @(Get-Process -Name 'MemEco' -ErrorAction SilentlyContinue)) {
        try { $p.Kill() } catch { }
    }
    Start-Sleep -Milliseconds 400
    return $procs.Count
}

function Remove-StartMenuShortcut {
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) "$taskName.lnk"
    if (Test-Path -LiteralPath $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue
        Write-Ok "ショートカットを削除: $shortcutPath"
    }
}

function Remove-RunEntry {
    $existing = Get-ItemProperty -Path $runKeyPath -Name $taskName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Remove-ItemProperty -Path $runKeyPath -Name $taskName -Force -ErrorAction SilentlyContinue
        Write-Ok 'HKCU\...\Run の "MemEco" を削除（二重起動防止）'
    }
}

# =====================================================================
#  アンインストール
# =====================================================================
if ($Uninstall) {
    Write-Host 'MemEco アンインストール' -ForegroundColor White
    Write-Host "対象 : $InstallDir"
    if (-not $isAdmin) { Write-Warn '管理者ではありません。タスクの削除に失敗する可能性があります。' }

    Write-Step '常駐プロセスを停止'
    $stopped = Stop-MemEcoProcesses
    if ($stopped -eq 0) { Write-Info '起動中の MemEco はありませんでした' }

    Write-Step 'ログオン時タスクを削除'
    & cmd.exe /c ('schtasks.exe /Delete /TN "{0}" /F' -f $taskName) | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "タスクを削除: $taskName"
    }
    else {
        Write-Info "タスクは存在しないか削除できませんでした (exit $LASTEXITCODE)"
    }

    Write-Step 'HKCU の自動起動エントリを削除'
    Remove-RunEntry

    Write-Step 'スタートメニューのショートカットを削除'
    Remove-StartMenuShortcut

    Write-Step 'インストールフォルダを削除'
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
        Write-Ok "削除: $InstallDir"
    }
    else {
        Write-Info "存在しません: $InstallDir"
    }

    # 空になった %LOCALAPPDATA%\MemEco も片付ける
    $localRoot = Split-Path -Parent $InstallDir
    if ((Test-Path -LiteralPath $localRoot) -and
        -not (Get-ChildItem -LiteralPath $localRoot -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $localRoot -Force -ErrorAction SilentlyContinue
    }

    if ($Purge) {
        Write-Step '設定とログを削除 (-Purge)'
        if (Test-Path -LiteralPath $settingsDir) {
            Remove-Item -LiteralPath $settingsDir -Recurse -Force
            Write-Ok "削除: $settingsDir"
        }
        else {
            Write-Info "存在しません: $settingsDir"
        }
    }
    else {
        Write-Step '設定とログ'
        if (Test-Path -LiteralPath $settingsDir) {
            Write-Info "設定は残しています: $settingsDir （消す場合は -Purge を付けて実行）"
        }
    }

    Write-Host ''
    Write-Host 'アンインストール完了' -ForegroundColor Green
    exit 0
}

# =====================================================================
#  インストール
# =====================================================================
Write-Host 'MemEco インストール' -ForegroundColor White
Write-Host "リポジトリ : $repoRoot"
Write-Host "インストール先 : $InstallDir"
Write-Host "管理者権限 : $isAdmin"

# ------------------------------------------------------------ コピー元の決定
Write-Step 'ビルド済みファイルを確認'

$publishExe = Join-Path $repoRoot 'publish\MemEco.exe'
$binDir = Join-Path $repoRoot 'src\MemEco.App\bin\Release\net9.0-windows'
$binExe = Join-Path $binDir 'MemEco.exe'

if (Test-Path -LiteralPath $publishExe) {
    $sourceDir = Join-Path $repoRoot 'publish'
    $sourceExe = $publishExe
    Write-Ok "publish を使用: $publishExe"
}
elseif (Test-Path -LiteralPath $binExe) {
    $sourceDir = $binDir
    $sourceExe = $binExe
    Write-Ok "通常ビルドを使用: $binExe"
}
else {
    Write-Host ''
    Write-Host 'ビルド済みの MemEco.exe が見つかりません。' -ForegroundColor Red
    Write-Host '先にビルドしてください:' -ForegroundColor Yellow
    Write-Host '  powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1' -ForegroundColor Yellow
    exit 1
}

$sourceInfo = Get-Item -LiteralPath $sourceExe
Write-Info ("サイズ {0} MB / 更新 {1:yyyy-MM-dd HH:mm:ss}" -f [math]::Round($sourceInfo.Length / 1MB, 2), $sourceInfo.LastWriteTime)

# ------------------------------------------------------------ プロセス停止
Write-Step '常駐プロセスを停止'
$stopped = Stop-MemEcoProcesses
if ($stopped -eq 0) { Write-Info '起動中の MemEco はありませんでした' }

# ------------------------------------------------------------ コピー
Write-Step 'ファイルをコピー'
$installedExe = Join-Path $InstallDir 'MemEco.exe'
if (Test-Path -LiteralPath $InstallDir) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
}
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Recurse -Force
Write-Ok "コピー: $sourceDir -> $InstallDir"

if (-not (Test-Path -LiteralPath $installedExe)) {
    Write-Host ''
    Write-Host "コピー後に exe が見つかりません: $installedExe" -ForegroundColor Red
    exit 1
}
Write-Ok "exe: $installedExe"

# ------------------------------------------------------------ ログオン時タスク
Write-Step 'ログオン時タスクを登録（管理者モードで自動起動）'
if (-not $isAdmin) {
    Write-Warn '管理者権限がないため、タスク登録をスキップしました。'
    Write-Warn '管理者モードで自動起動するには、管理者としてこのスクリプトを実行し直してください。'
}
else {
    $taskCommand = 'schtasks.exe /Create /TN "{0}" /TR "\"{1}\" --autostart" /SC ONLOGON /RL HIGHEST /F' -f $taskName, $installedExe
    Write-Info $taskCommand
    & cmd.exe /c $taskCommand | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Ok "タスクを登録: $taskName (/SC ONLOGON /RL HIGHEST)"
    }
    else {
        Write-Warn "タスク登録に失敗しました (exit $LASTEXITCODE)。UAC を許可しているか確認してください。"
    }
}

# ------------------------------------------------------------ 二重起動防止
Write-Step 'HKCU の自動起動エントリを整理'
Remove-RunEntry

# ------------------------------------------------------------ ショートカット
Write-Step 'スタートメニューのショートカット'
if ($NoShortcut) {
    Write-Info '-NoShortcut のため作成しません'
}
else {
    try {
        $programs = [Environment]::GetFolderPath('Programs')
        $shortcutPath = Join-Path $programs "$taskName.lnk"
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $installedExe
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.IconLocation = "$installedExe,0"
        $shortcut.Description = 'MemEco - 自動メモリ管理'
        $shortcut.Save()
        $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut)
        $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        Write-Ok "作成: $shortcutPath"
    }
    catch {
        Write-Warn "ショートカット作成に失敗しました: $($_.Exception.Message)"
    }
}

# ------------------------------------------------------------ 完了
Write-Host ''
Write-Host 'インストール完了' -ForegroundColor Green
Write-Host ''
Write-Host '  exe            : ' -NoNewline -ForegroundColor DarkGray
Write-Host $installedExe
Write-Host '  自動起動       : ' -NoNewline -ForegroundColor DarkGray
if ($isAdmin) {
    Write-Host 'タスクスケジューラ "MemEco"（ログオン時 / 管理者モード）'
}
else {
    Write-Host '未登録（管理者権限が必要）'
}
Write-Host '  スタートメニュー : ' -NoNewline -ForegroundColor DarkGray
Write-Host 'MemEco'
Write-Host ''
Write-Host 'アンインストール:' -ForegroundColor DarkGray
Write-Host '  powershell -ExecutionPolicy Bypass -File .\scripts\uninstall.ps1' -ForegroundColor DarkGray
Write-Host '  （設定も消す場合: .\scripts\uninstall.ps1 -Purge）' -ForegroundColor DarkGray

exit 0
