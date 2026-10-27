#Requires -Version 5.1
<#
.SYNOPSIS
    MemEco の受け入れ自動検証。各項目の PASS/FAIL とサマリ、終了コードを返します。

.DESCRIPTION
    検証項目:
      1. ビルド成功（scripts\build.ps1 -SkipPublish）
      2. --selftest <tmp>\selftest.txt : 終了コード 0、内容に「管理者」「メモリ」「プロセス走査」「完了」
      3. --preview <tmp>\preview.png   : 存在 / 20KB 以上 / PNG ヘッダ 440x680 / 白紙でない
      4. 常駐: --autostart を起動し 12 秒後に WS、5 秒平均 CPU を計測（CPU<1% / WS<120MB）
         ※ CPU はタスク マネージャー表示相当（全論理 CPU 平均）。1 コア換算も併記する
      5. トリム実測: 300MB のメモリホッグを用意し --reclaim で実際に WS が減るか（50MB 以上）
         ※ ProcessSafetyPolicy の「\System32\ 保護」「起動 60 秒未満保護」を避けるため、
            ホッグは一時フォルダの MemEcoHog.exe として起動し、62 秒待ってから --reclaim する
      6. %APPDATA%\MemEco\config.json が生成されること

    検証は --autostart / --selftest / --preview / --reclaim のみを使います。
    レジストリ・タスクスケジューラ・スタートメニューは変更しません。
    起動したプロセスはすべて停止し、一時ファイルも削除します（-KeepTemp で保持）。

.PARAMETER Exe
    検証する MemEco.exe。省略時は bin\Release を優先し、無ければ publish を使います。

.PARAMETER SkipBuild
    ビルドをスキップし、既存の exe を検証します。

.PARAMETER StopExisting
    検証開始前に起動中の MemEco を停止します（既定は「既に起動中なら中断」）。

.PARAMETER KeepTemp
    一時ファイル（selftest.txt / preview.png / reclaim.json / hog.ps1）を残します。

.PARAMETER MaxIdleCpuPercent
    常駐アイドル CPU の上限（%）。全論理 CPU に対する平均（タスク マネージャー表示相当）。
    既定は 1.0。1 コア換算値も表示されます。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\verify.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\tools\verify.ps1 -SkipBuild -KeepTemp
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [switch]$SkipBuild,
    [switch]$StopExisting,
    [switch]$KeepTemp,
    [int]$IdleWaitSeconds = 12,
    [int]$CpuSampleSeconds = 5,
    [double]$MaxIdleCpuPercent = 1.0,
    [double]$MaxIdleWorkingSetMb = 120,
    [int]$HogSizeMb = 300,
    [int]$HogStartupGraceSeconds = 62,
    [int]$MinReclaimMb = 50,
    [int]$PreviewWidth = 440,
    [int]$PreviewHeight = 680
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$script:results = New-Object System.Collections.ArrayList
$script:spawned = New-Object System.Collections.ArrayList
$script:tmp = $null
$script:measured = [ordered]@{}

# ================================================================ ユーティリティ
function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Add-Result {
    param(
        [string]$Name,
        [ValidateSet('PASS', 'FAIL', 'SKIP')]
        [string]$Status,
        [string]$Detail = ''
    )
    [void]$script:results.Add([pscustomobject]@{
        Name   = $Name
        Status = $Status
        Detail = $Detail
    })

    switch ($Status) {
        'PASS' { $color = 'Green' }
        'FAIL' { $color = 'Red' }
        default { $color = 'Yellow' }
    }
    Write-Host ("    [{0}] {1}" -f $Status, $Name) -ForegroundColor $color
    if ($Detail) {
        foreach ($line in ($Detail -split "`n")) {
            Write-Host ("           {0}" -f $line.TrimEnd()) -ForegroundColor DarkGray
        }
    }
}

function Get-LiveProcess {
    param([int]$ProcessId)
    try {
        return Get-Process -Id $ProcessId -ErrorAction Stop
    }
    catch {
        return $null
    }
}

function Get-MemEcoProcesses {
    return @(Get-Process -Name 'MemEco' -ErrorAction SilentlyContinue)
}

function Stop-ProcessTree {
    param([int]$ProcessId)

    $p = Get-LiveProcess -ProcessId $ProcessId
    if (-not $p) { return }
    try { $null = $p.Kill() } catch { }
    try { $null = $p.WaitForExit(5000) } catch { }
}

function Invoke-MemEcoCli {
    param(
        [string]$ExePath,
        [string]$Arguments,
        [int]$TimeoutSeconds = 120
    )

    $proc = Start-Process -FilePath $ExePath -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    [void]$script:spawned.Add($proc.Id)

    $exited = $false
    try { $exited = $proc.WaitForExit($TimeoutSeconds * 1000) } catch { }

    if (-not $exited) {
        try { $null = $proc.Kill() } catch { }
        try { $null = $proc.WaitForExit(5000) } catch { }
        return [pscustomobject]@{ ExitCode = -1; TimedOut = $true; ProcessId = $proc.Id }
    }

    return [pscustomobject]@{ ExitCode = $proc.ExitCode; TimedOut = $false; ProcessId = $proc.Id }
}

function Get-ProcessCpuPercent {
    param(
        [int]$ProcessId,
        [int]$SampleSeconds = 5
    )

    $p = Get-LiveProcess -ProcessId $ProcessId
    if (-not $p) { return $null }

    try {
        $before = $p.TotalProcessorTime.TotalSeconds
    }
    catch {
        return $null
    }

    Start-Sleep -Seconds $SampleSeconds

    $p = Get-LiveProcess -ProcessId $ProcessId
    if (-not $p) { return $null }

    try {
        $after = $p.TotalProcessorTime.TotalSeconds
    }
    catch {
        return $null
    }

    $singleCorePercent = (($after - $before) / [double]$SampleSeconds) * 100.0
    $cores = [Environment]::ProcessorCount
    if ($cores -lt 1) { $cores = 1 }

    return [pscustomobject]@{
        # タスク マネージャー表示相当（全論理 CPU に対する割合）
        TotalPercent      = [math]::Round($singleCorePercent / [double]$cores, 3)
        # 1 コア換算（従来の計算方法）
        SingleCorePercent = [math]::Round($singleCorePercent, 2)
        LogicalCores      = $cores
    }
}

function Get-ProcessWorkingSetMb {
    param([int]$ProcessId)

    $p = Get-LiveProcess -ProcessId $ProcessId
    if (-not $p) { return $null }
    try { return [math]::Round($p.WorkingSet64 / 1MB, 1) } catch { return $null }
}

function Get-PngImageSize {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24) { return $null }

    $signature = @(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    for ($i = 0; $i -lt $signature.Count; $i++) {
        if ($bytes[$i] -ne $signature[$i]) { return $null }
    }

    $chunkType = [System.Text.Encoding]::ASCII.GetString($bytes, 12, 4)
    if ($chunkType -ne 'IHDR') { return $null }

    $width = ([int64]$bytes[16] -shl 24) -bor ([int64]$bytes[17] -shl 16) -bor ([int64]$bytes[18] -shl 8) -bor [int64]$bytes[19]
    $height = ([int64]$bytes[20] -shl 24) -bor ([int64]$bytes[21] -shl 16) -bor ([int64]$bytes[22] -shl 8) -bor [int64]$bytes[23]

    return [pscustomobject]@{ Width = [int]$width; Height = [int]$height }
}

function Get-ImageVariation {
    param(
        [string]$Path,
        [int]$GridSize = 16
    )

    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList $Path
    try {
        $luminances = New-Object System.Collections.ArrayList
        $colors = New-Object System.Collections.ArrayList

        for ($ix = 0; $ix -lt $GridSize; $ix++) {
            for ($iy = 0; $iy -lt $GridSize; $iy++) {
                $x = [int](($bitmap.Width - 1) * ($ix + 0.5) / $GridSize)
                $y = [int](($bitmap.Height - 1) * ($iy + 0.5) / $GridSize)
                $pixel = $bitmap.GetPixel($x, $y)
                $luminance = 0.2126 * $pixel.R + 0.7152 * $pixel.G + 0.0722 * $pixel.B
                [void]$luminances.Add([double]$luminance)
                $key = '{0},{1},{2}' -f $pixel.R, $pixel.G, $pixel.B
                if (-not $colors.Contains($key)) { [void]$colors.Add($key) }
            }
        }

        $mean = 0.0
        foreach ($value in $luminances) { $mean += $value }
        $mean = $mean / $luminances.Count

        $variance = 0.0
        foreach ($value in $luminances) { $variance += ($value - $mean) * ($value - $mean) }
        $variance = $variance / $luminances.Count

        return [pscustomobject]@{
            Mean           = [math]::Round($mean, 2)
            StdDev         = [math]::Round([math]::Sqrt($variance), 2)
            DistinctColors = $colors.Count
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Invoke-Build {
    $buildScript = Join-Path $repoRoot 'scripts\build.ps1'
    if (-not (Test-Path -LiteralPath $buildScript)) {
        Add-Result 'ビルド: scripts\build.ps1 が存在' 'FAIL' "見つかりません: $buildScript"
        return $false
    }

    Write-Host "    scripts\build.ps1 -SkipPublish を実行します..." -ForegroundColor DarkGray

    # 子プロセスを挟むと環境によって停止するため、同一プロセス内で呼び出す。
    # build.ps1 は exit 0/1 で終了し、その値は $LASTEXITCODE に入る。
    $LASTEXITCODE = 0
    $buildOk = $false
    try {
        & $buildScript -SkipPublish
        $buildOk = ($LASTEXITCODE -eq 0)
    }
    catch {
        Write-Host "    ビルドスクリプトで例外: $($_.Exception.Message)" -ForegroundColor Red
        $buildOk = $false
    }

    if ($buildOk) {
        Add-Result 'ビルド: dotnet build (Release)' 'PASS' 'exit 0'
        return $true
    }

    Add-Result 'ビルド: dotnet build (Release)' 'FAIL' "exit $LASTEXITCODE。scripts\build.ps1 の出力を確認してください。"
    return $false
}

function Resolve-ExePath {
    if ($Exe) {
        if (Test-Path -LiteralPath $Exe) {
            return (Resolve-Path -LiteralPath $Exe).Path
        }
        return $null
    }

    # 既定のビルド出力（DESIGN §8）を優先し、無ければ publish を使う。
    $binExe = Join-Path $repoRoot 'src\MemEco.App\bin\Release\net9.0-windows\MemEco.exe'
    if (Test-Path -LiteralPath $binExe) {
        return (Resolve-Path -LiteralPath $binExe).Path
    }

    $publishExe = Join-Path $repoRoot 'publish\MemEco.exe'
    if (Test-Path -LiteralPath $publishExe) {
        return (Resolve-Path -LiteralPath $publishExe).Path
    }

    return $null
}

# ================================================================ 前処理
$startedAt = Get-Date
Write-Host 'MemEco 受け入れ検証' -ForegroundColor White
Write-Host "日時     : $($startedAt.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "PS       : $($PSVersionTable.PSVersion)"
Write-Host "OS       : $([Environment]::OSVersion.VersionString) (Build $([Environment]::OSVersion.Version.Build))"
Write-Host "リポジトリ: $repoRoot"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "管理者   : $isAdmin"

$script:tmp = Join-Path $env:TEMP ('MemEco-verify-' + $startedAt.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $script:tmp -Force | Out-Null
Write-Host "一時領域 : $script:tmp"

$exePath = $null
$configPath = Join-Path (Join-Path $env:APPDATA 'MemEco') 'config.json'
$fatal = $false

try {
    # ------------------------------------------------------------ 1. ビルド
    Write-Step '1. ビルド'
    if ($SkipBuild) {
        Add-Result 'ビルド: スキップ (-SkipBuild)' 'SKIP' '既存の exe を検証します'
    }
    else {
        $buildOk = Invoke-Build
        if (-not $buildOk) { $fatal = $true }
    }

    # ------------------------------------------------------------ exe 解決
    $exePath = Resolve-ExePath
    if (-not $exePath) {
        Add-Result 'exe の存在' 'FAIL' 'MemEco.exe が見つかりません。scripts\build.ps1 を実行してください。'
        $fatal = $true
    }
    else {
        $exeInfo = Get-Item -LiteralPath $exePath
        Write-Host ''
        Write-Host "検証対象 exe : $exePath" -ForegroundColor White
        Write-Host ("               {0} MB / 更新 {1:yyyy-MM-dd HH:mm:ss}" -f [math]::Round($exeInfo.Length / 1MB, 2), $exeInfo.LastWriteTime)
    }

    # 二重起動チェック（全 CLI が Mutex で 2 個目を終了させる実装のため、先に確認する）
    if (-not $fatal) {
        $existing = Get-MemEcoProcesses
        if ($existing.Count -gt 0) {
            if ($StopExisting) {
                Write-Host "    既存の MemEco ($($existing.Count) 個) を停止します (-StopExisting)" -ForegroundColor Yellow
                foreach ($p in $existing) { Stop-ProcessTree -ProcessId $p.Id }
                Start-Sleep -Milliseconds 800
            }
            else {
                Add-Result '前提: MemEco が起動していない' 'FAIL' (
                    "既に MemEco が $($existing.Count) 個動作中です。二重起動は既存インスタンスへ通知して終了するため検証できません。`n" +
                    'トレイメニューから終了するか、-StopExisting を付けて再実行してください。')
                $fatal = $true
            }
        }
        else {
            Add-Result '前提: MemEco が起動していない' 'PASS' ''
        }
    }

    if (-not $fatal) {
        # ------------------------------------------------------------ 2. selftest
        Write-Step '2. --selftest'
        $selftestPath = Join-Path $script:tmp 'selftest.txt'
        $result = Invoke-MemEcoCli -ExePath $exePath -Arguments ('--selftest "{0}"' -f $selftestPath) -TimeoutSeconds 150

        $selftestExists = Test-Path -LiteralPath $selftestPath
        if ($result.TimedOut) {
            Add-Result 'セルフテスト: 終了コード' 'FAIL' '150 秒以内に終了しませんでした'
        }
        elseif ($result.ExitCode -ne 0) {
            Add-Result 'セルフテスト: 終了コード' 'FAIL' "exit $($result.ExitCode)（0 を期待）"
        }
        elseif (-not $selftestExists) {
            Add-Result 'セルフテスト: 終了コード' 'FAIL' "exit 0 ですが出力ファイルがありません: $selftestPath"
        }
        else {
            Add-Result 'セルフテスト: 終了コード' 'PASS' "exit 0 / $([math]::Round((Get-Item -LiteralPath $selftestPath).Length / 1KB, 1)) KB"
        }

        if ($selftestExists) {
            $selftestText = [System.IO.File]::ReadAllText($selftestPath, [System.Text.Encoding]::UTF8)
            $needles = @('管理者', 'メモリ', 'プロセス走査', '完了')
            $missing = @($needles | Where-Object { $selftestText -notmatch [regex]::Escape($_) })
            if ($missing.Count -eq 0) {
                Add-Result 'セルフテスト: 内容' 'PASS' '「管理者」「メモリ」「プロセス走査」「完了」を確認'
                $adminLine = @($selftestText -split "`r?`n" | Where-Object { $_ -match '^管理者:' })
                if ($adminLine.Count -gt 0) {
                    Write-Host ("            {0}" -f $adminLine[0].Trim()) -ForegroundColor DarkGray
                }
            }
            else {
                Add-Result 'セルフテスト: 内容' 'FAIL' ("含まれない項目: " + ($missing -join ', '))
            }
        }
        else {
            Add-Result 'セルフテスト: 内容' 'SKIP' '出力ファイルがないため確認できません'
        }

        # ------------------------------------------------------------ 3. preview
        Write-Step '3. --preview'
        $previewPath = Join-Path $script:tmp 'preview.png'
        $result = Invoke-MemEcoCli -ExePath $exePath -Arguments ('--preview "{0}"' -f $previewPath) -TimeoutSeconds 150

        $previewOk = $true
        if ($result.TimedOut) {
            Add-Result 'プレビュー: 生成' 'FAIL' '150 秒以内に終了しませんでした'
            $previewOk = $false
        }
        elseif ($result.ExitCode -ne 0) {
            Add-Result 'プレビュー: 生成' 'FAIL' "exit $($result.ExitCode)（0 を期待）"
            $previewOk = $false
        }
        elseif (-not (Test-Path -LiteralPath $previewPath)) {
            Add-Result 'プレビュー: 生成' 'FAIL' "PNG がありません: $previewPath"
            $previewOk = $false
        }
        else {
            Add-Result 'プレビュー: 生成' 'PASS' "exit 0"
        }

        if ($previewOk) {
            $previewInfo = Get-Item -LiteralPath $previewPath
            $previewKb = [math]::Round($previewInfo.Length / 1KB, 1)
            if ($previewInfo.Length -ge 20KB) {
                Add-Result 'プレビュー: ファイルサイズ (20KB 以上)' 'PASS' "$previewKb KB"
            }
            else {
                Add-Result 'プレビュー: ファイルサイズ (20KB 以上)' 'FAIL' "$previewKb KB（20KB 未満）"
            }

            $pngSize = Get-PngImageSize -Path $previewPath
            if (-not $pngSize) {
                Add-Result 'プレビュー: PNG 形式' 'FAIL' 'PNG シグネチャ / IHDR が不正です'
            }
            else {
                $sizeOk = ($pngSize.Width -eq $PreviewWidth) -and ($pngSize.Height -eq $PreviewHeight)
                $sizeDetail = "{0}x{1}（期待 {2}x{3}）" -f $pngSize.Width, $pngSize.Height, $PreviewWidth, $PreviewHeight
                if ($sizeOk) {
                    Add-Result 'プレビュー: PNG サイズ' 'PASS' $sizeDetail
                }
                else {
                    $ratioExpected = $PreviewWidth / [double]$PreviewHeight
                    $ratioActual = $pngSize.Width / [double]$pngSize.Height
                    if ([math]::Abs($ratioActual - $ratioExpected) -lt 0.02) {
                        $sizeDetail += "（縦横比は一致。DPI 倍率の可能性があります）"
                    }
                    Add-Result 'プレビュー: PNG サイズ' 'FAIL' $sizeDetail
                }
            }

            try {
                $variation = Get-ImageVariation -Path $previewPath -GridSize 16
                $variationDetail = "平均輝度 {0} / 標準偏差 {1} / 色数 {2}" -f $variation.Mean, $variation.StdDev, $variation.DistinctColors
                if (($variation.StdDev -ge 3.0) -and ($variation.DistinctColors -ge 8)) {
                    Add-Result 'プレビュー: 白紙でない' 'PASS' $variationDetail
                }
                else {
                    Add-Result 'プレビュー: 白紙でない' 'FAIL' ($variationDetail + "（描画がほぼ単色です）")
                }
            }
            catch {
                Add-Result 'プレビュー: 白紙でない' 'FAIL' "画像を読めません: $($_.Exception.Message)"
            }
        }
        else {
            Add-Result 'プレビュー: ファイルサイズ (20KB 以上)' 'SKIP' 'PNG がないため'
            Add-Result 'プレビュー: PNG 形式' 'SKIP' 'PNG がないため'
            Add-Result 'プレビュー: PNG サイズ' 'SKIP' 'PNG がないため'
            Add-Result 'プレビュー: 白紙でない' 'SKIP' 'PNG がないため'
        }

        # ------------------------------------------------------------ 4. 常駐
        Write-Step "4. 常駐 (--autostart / ${IdleWaitSeconds} 秒後 + ${CpuSampleSeconds} 秒平均)"
        $existing = Get-MemEcoProcesses
        if ($existing.Count -gt 0) {
            Add-Result '常駐: 起動状態' 'FAIL' "既に MemEco が動作中です（PID $($existing[0].Id)）"
        }
        else {
            $appProc = Start-Process -FilePath $exePath -ArgumentList '--autostart' -PassThru -WindowStyle Hidden
            [void]$script:spawned.Add($appProc.Id)
            Write-Host "    起動: PID $($appProc.Id) / ${IdleWaitSeconds} 秒待機..." -ForegroundColor DarkGray

            Start-Sleep -Seconds $IdleWaitSeconds

            $live = Get-LiveProcess -ProcessId $appProc.Id
            if (-not $live) {
                Add-Result '常駐: 起動状態' 'FAIL' '起動直後に終了しました（設定破損 / 例外の可能性。%APPDATA%\MemEco\logs\app.log を確認）'
                Add-Result '常駐: アイドル CPU' 'SKIP' 'プロセスが終了'
                Add-Result '常駐: ワーキングセット' 'SKIP' 'プロセスが終了'
            }
            else {
                Add-Result '常駐: 起動状態' 'PASS' "PID $($appProc.Id) が ${IdleWaitSeconds} 秒後も生存"

                $workingSetMb = Get-ProcessWorkingSetMb -ProcessId $appProc.Id
                $cpuPercent = Get-ProcessCpuPercent -ProcessId $appProc.Id -SampleSeconds $CpuSampleSeconds

                if ($null -eq $cpuPercent) {
                    Add-Result '常駐: アイドル CPU' 'FAIL' 'CPU を計測できませんでした（プロセス終了）'
                }
                else {
                    $script:measured['IdleCpuTotalPercent'] = $cpuPercent.TotalPercent
                    $script:measured['IdleCpuSingleCorePercent'] = $cpuPercent.SingleCorePercent
                    $detail = "{0}%（全 {1} 論理 CPU 平均） / 1 コア換算 {2}%（上限 {3}% は合計値で判定）" -f `
                        $cpuPercent.TotalPercent, $cpuPercent.LogicalCores, $cpuPercent.SingleCorePercent, $MaxIdleCpuPercent
                    if ($cpuPercent.TotalPercent -lt $MaxIdleCpuPercent) {
                        Add-Result '常駐: アイドル CPU' 'PASS' $detail
                    }
                    else {
                        Add-Result '常駐: アイドル CPU' 'FAIL' $detail
                    }
                }

                if ($null -eq $workingSetMb) {
                    Add-Result '常駐: ワーキングセット' 'FAIL' 'WS を計測できませんでした（プロセス終了）'
                }
                else {
                    $script:measured['IdleWorkingSetMb'] = $workingSetMb
                    $detail = "{0} MB（上限 {1} MB / 設計目標 80 MB）" -f $workingSetMb, $MaxIdleWorkingSetMb
                    if ($workingSetMb -lt $MaxIdleWorkingSetMb) {
                        Add-Result '常駐: ワーキングセット' 'PASS' $detail
                    }
                    else {
                        Add-Result '常駐: ワーキングセット' 'FAIL' $detail
                    }
                }

                Write-Host '    常駐プロセスを停止...' -ForegroundColor DarkGray
                Stop-ProcessTree -ProcessId $appProc.Id
                Start-Sleep -Milliseconds 800
                $left = Get-MemEcoProcesses
                if ($left.Count -eq 0) {
                    Add-Result '常駐: 停止' 'PASS' 'PID を停止しました'
                }
                else {
                    Add-Result '常駐: 停止' 'FAIL' "停止できていない MemEco が $($left.Count) 個あります"
                }
            }
        }

        # ------------------------------------------------------------ 5. トリム実測
        Write-Step "5. トリム実測 (${HogSizeMb} MB のメモリホッグ + --reclaim)"
        $existing = Get-MemEcoProcesses
        if ($existing.Count -gt 0) {
            Add-Result 'トリム実測: 準備' 'FAIL' "MemEco が動作中のため --reclaim を実行できません（PID $($existing[0].Id)）"
        }
        else {
            $hogScript = Join-Path $script:tmp 'hog.ps1'
            $hogSource = @'
param([int]$SizeMb = 300)
$totalBytes = $SizeMb * 1MB
$buffer = New-Object byte[] $totalBytes
for ($i = 0; $i -lt $totalBytes; $i += 4096) { $buffer[$i] = 1 }
Start-Sleep -Seconds 600
'@
            Set-Content -LiteralPath $hogScript -Value $hogSource -Encoding ASCII

            # ProcessSafetyPolicy は \System32\ 配下の exe を保護するため、
            # PowerShell を一時フォルダにコピーして「MemEcoHog.exe」として動かす。
            $hogExe = Join-Path $script:tmp 'MemEcoHog.exe'
            Copy-Item -LiteralPath (Join-Path $PSHOME 'powershell.exe') -Destination $hogExe -Force

            $hogProc = Start-Process -FilePath $hogExe `
                -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $hogScript), '-SizeMb', $HogSizeMb) `
                -PassThru -WindowStyle Hidden
            [void]$script:spawned.Add($hogProc.Id)

            $hogReadyMb = [math]::Round($HogSizeMb * 0.8, 0)
            $hogBeforeMb = 0
            for ($i = 0; $i -lt 60; $i++) {
                Start-Sleep -Milliseconds 500
                $hogLive = Get-LiveProcess -ProcessId $hogProc.Id
                if (-not $hogLive) { break }
                $hogBeforeMb = Get-ProcessWorkingSetMb -ProcessId $hogProc.Id
                if ($null -eq $hogBeforeMb) { break }
                if ($hogBeforeMb -ge $hogReadyMb) { break }
            }

            if (($null -eq $hogBeforeMb) -or ($hogBeforeMb -lt $hogReadyMb)) {
                Add-Result 'トリム実測: メモリホッグ準備' 'FAIL' ("ホッグの WS が ${hogReadyMb} MB に達しません（計測値 {0} MB）" -f $hogBeforeMb)
            }
            else {
                Add-Result 'トリム実測: メモリホッグ準備' 'PASS' ("ホッグ WS {0} MB（MemEcoHog.exe / PID {1}）" -f $hogBeforeMb, $hogProc.Id)

                # 起動 60 秒未満のプロセスは ProcessSafetyPolicy が保護するため待機する
                $hogLive = Get-LiveProcess -ProcessId $hogProc.Id
                if ($hogLive) {
                    $hogAgeSeconds = ((Get-Date) - $hogLive.StartTime).TotalSeconds
                    $graceWait = [int][math]::Ceiling($HogStartupGraceSeconds - $hogAgeSeconds)
                    if ($graceWait -gt 0) {
                        Write-Host ("    起動 {0} 秒保護のため {1} 秒待機（--reclaim の対象にするため）..." -f $HogStartupGraceSeconds, $graceWait) -ForegroundColor DarkGray
                        $remaining = $graceWait
                        while ($remaining -gt 0) {
                            $chunk = [math]::Min(10, $remaining)
                            Start-Sleep -Seconds $chunk
                            $remaining = $remaining - $chunk
                            if ($remaining -gt 0) {
                                Write-Host ("      残り {0} 秒..." -f $remaining) -ForegroundColor DarkGray
                            }
                        }
                    }

                    $hogBeforeMb = Get-ProcessWorkingSetMb -ProcessId $hogProc.Id
                    if ($null -eq $hogBeforeMb) { $hogBeforeMb = 0 }
                }
            }

            $reclaimPath = Join-Path $script:tmp 'reclaim.json'
            $result = Invoke-MemEcoCli -ExePath $exePath -Arguments ('--reclaim "{0}"' -f $reclaimPath) -TimeoutSeconds 180
            Start-Sleep -Seconds 2

            $reclaimJson = $null
            if (Test-Path -LiteralPath $reclaimPath) {
                try {
                    $reclaimJson = Get-Content -LiteralPath $reclaimPath -Raw -Encoding UTF8 | ConvertFrom-Json
                }
                catch {
                    $reclaimJson = $null
                }
            }

            $jsonDetail = ''
            if ($reclaimJson) {
                $jsonDetail = "Success={0} / FreedBytes={1} / TrimmedProcesses={2} / StandbyPurged={3} / Message={4}" -f `
                    $reclaimJson.Success, $reclaimJson.FreedBytes, $reclaimJson.TrimmedProcesses, $reclaimJson.StandbyPurged, $reclaimJson.Message
            }
            else {
                $jsonDetail = 'JSON を読めませんでした'
            }

            if ($result.TimedOut) {
                Add-Result 'トリム実測: --reclaim 実行' 'FAIL' '180 秒以内に終了しませんでした'
            }
            elseif (($result.ExitCode -eq 0) -and $reclaimJson -and $reclaimJson.Success) {
                Add-Result 'トリム実測: --reclaim 実行' 'PASS' ("exit 0 / " + $jsonDetail)
            }
            elseif ($result.ExitCode -eq 0) {
                Add-Result 'トリム実測: --reclaim 実行' 'FAIL' ("exit 0 ですが Success=false / " + $jsonDetail)
            }
            else {
                Add-Result 'トリム実測: --reclaim 実行' 'FAIL' ("exit $($result.ExitCode) / " + $jsonDetail)
            }

            $hogAfterMb = $null
            $hogStillAlive = Get-LiveProcess -ProcessId $hogProc.Id
            if ($hogStillAlive) { $hogAfterMb = Get-ProcessWorkingSetMb -ProcessId $hogProc.Id }

            if (($null -eq $hogAfterMb) -or ($null -eq $hogBeforeMb) -or ($hogBeforeMb -lt $hogReadyMb)) {
                Add-Result 'トリム実測: ホッグ WS の削減' 'SKIP' 'ホッグの計測値が不足（準備失敗 / プロセス終了）'
            }
            else {
                $freedMb = [math]::Round($hogBeforeMb - $hogAfterMb, 1)
                $script:measured['HogBeforeMb'] = $hogBeforeMb
                $script:measured['HogAfterMb'] = $hogAfterMb
                $script:measured['HogFreedMb'] = $freedMb

                $detail = "ホッグ WS {0} MB -> {1} MB（{2} MB 減 / 判定 {3} MB 以上）" -f $hogBeforeMb, $hogAfterMb, $freedMb, $MinReclaimMb
                if ($freedMb -ge $MinReclaimMb) {
                    Add-Result 'トリム実測: ホッグ WS の削減' 'PASS' $detail
                }
                else {
                    $reason = 'EmptyWorkingSet で減らない環境要因（他プロセスの干渉 / 権限 / ページ状態）の可能性があります'
                    if ($reclaimJson -and ($reclaimJson.FreedBytes -gt 0)) {
                        $reason += "（ただし ReclaimResult.FreedBytes={0} を報告）" -f $reclaimJson.FreedBytes
                    }
                    Add-Result 'トリム実測: ホッグ WS の削減' 'FAIL' ($detail + "`n" + $reason)
                }
            }

            if (Get-LiveProcess -ProcessId $hogProc.Id) {
                Stop-ProcessTree -ProcessId $hogProc.Id
                Start-Sleep -Milliseconds 500
            }
        }

        # ------------------------------------------------------------ 6. 設定ファイル
        Write-Step '6. 設定ファイル (%APPDATA%\MemEco\config.json)'
        if (-not (Test-Path -LiteralPath $configPath)) {
            # 設定保存は起動時の実装に依存するため、UI 起動 (--show) でもう一度試す
            Write-Host '    まだ無いため、--show で 8 秒起動して確認します...' -ForegroundColor DarkGray
            $existing = Get-MemEcoProcesses
            if ($existing.Count -eq 0) {
                $showProc = Start-Process -FilePath $exePath -ArgumentList '--show' -PassThru -WindowStyle Hidden
                [void]$script:spawned.Add($showProc.Id)
                Start-Sleep -Seconds 8
                Stop-ProcessTree -ProcessId $showProc.Id
                Start-Sleep -Milliseconds 800
            }
        }

        if (Test-Path -LiteralPath $configPath) {
            $configOk = $false
            try {
                $configText = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8
                $null = $configText | ConvertFrom-Json
                $configOk = $true
            }
            catch {
                $configOk = $false
            }

            $configKb = [math]::Round((Get-Item -LiteralPath $configPath).Length / 1KB, 1)
            if ($configOk) {
                Add-Result '設定: config.json 生成' 'PASS' "$configPath ($configKb KB / JSON 妥当)"
            }
            else {
                Add-Result '設定: config.json 生成' 'FAIL' "$configPath はありますが JSON として読めません"
            }
        }
        else {
            Add-Result '設定: config.json 生成' 'FAIL' "生成されませんでした: $configPath（UI 側の保存タイミングを確認）"
        }

        # ------------------------------------------------------------ 後片付け確認
        Write-Step '7. 後片付け'
        foreach ($processId in @($script:spawned)) {
            Stop-ProcessTree -ProcessId ([int]$processId)
        }
        Start-Sleep -Milliseconds 500
        $remaining = Get-MemEcoProcesses
        if ($remaining.Count -eq 0) {
            Add-Result '後片付け: プロセス停止' 'PASS' '検証で起動したプロセスは残っていません'
        }
        else {
            Add-Result '後片付け: プロセス停止' 'FAIL' "MemEco が $($remaining.Count) 個残っています: PID $((@($remaining | ForEach-Object { $_.Id })) -join ', ')"
        }
    }
}
catch {
    Add-Result '検証中の例外' 'FAIL' $_.Exception.Message
}
finally {
    # 何があっても起動したプロセスを止める
    foreach ($processId in @($script:spawned)) {
        Stop-ProcessTree -ProcessId ([int]$processId)
    }

    if ($KeepTemp) {
        Write-Host ''
        Write-Host "一時ファイルを保持しました: $script:tmp" -ForegroundColor Yellow
    }
    else {
        if (Test-Path -LiteralPath $script:tmp) {
            Remove-Item -LiteralPath $script:tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ================================================================ サマリ
$passCount = @($script:results | Where-Object { $_.Status -eq 'PASS' }).Count
$failCount = @($script:results | Where-Object { $_.Status -eq 'FAIL' }).Count
$skipCount = @($script:results | Where-Object { $_.Status -eq 'SKIP' }).Count

Write-Host ''
Write-Host '===== 受け入れ検証サマリ =====' -ForegroundColor White
foreach ($row in $script:results) {
    switch ($row.Status) {
        'PASS' { $color = 'Green' }
        'FAIL' { $color = 'Red' }
        default { $color = 'Yellow' }
    }
    Write-Host ("  [{0,-4}] {1}" -f $row.Status, $row.Name) -ForegroundColor $color
}

if ($script:measured.Count -gt 0) {
    Write-Host ''
    Write-Host '実測値:' -ForegroundColor White
    foreach ($key in $script:measured.Keys) {
        Write-Host ("  {0,-18} : {1}" -f $key, $script:measured[$key]) -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host ("合計: PASS {0} / FAIL {1} / SKIP {2}" -f $passCount, $failCount, $skipCount) -ForegroundColor White
Write-Host ("所要: {0:F1} 秒" -f ((Get-Date) - $startedAt).TotalSeconds) -ForegroundColor DarkGray

if ($failCount -gt 0) {
    Write-Host '結果: FAIL' -ForegroundColor Red
    exit 1
}

Write-Host '結果: PASS' -ForegroundColor Green
exit 0
