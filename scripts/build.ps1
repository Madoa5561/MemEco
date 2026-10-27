#Requires -Version 5.1
<#
.SYNOPSIS
    MemEco を Release ビルドし、可能なら単一 exe に publish します。

.DESCRIPTION
    - dotnet は「%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe」→ PATH の順で自動検出します。
    - まず MemEco.sln をビルドし、次に framework-dependent の単一 exe を publish します。
    - publish はランタイムパック取得のためネットワークが必要な場合があります。
      失敗しても通常ビルドの exe を残して警告のみで終了します（終了コード 0）。
    - 外部 NuGet パッケージは使わないため、SDK が揃っていればオフラインでもビルドできます。

.PARAMETER Configuration
    ビルド構成。既定は Release。

.PARAMETER SkipPublish
    publish を行わず、dotnet build のみ実行します（オフライン向け）。

.PARAMETER DotnetPath
    dotnet.exe を明示指定します。省略時は自動検出。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipPublish,

    [string]$DotnetPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$script:dotnet = $null

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

function Resolve-DotnetPath {
    param([string]$Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit) {
            return (Resolve-Path -LiteralPath $Explicit).Path
        }
        throw "指定された dotnet が見つかりません: $Explicit"
    }

    # 1) ユーザー領域に導入された SDK（このプロジェクトの既定）
    $local = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) {
        return $local
    }

    # 2) PATH 上の dotnet
    $cmd = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }
    $cmd = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    throw @'
.NET SDK (dotnet) が見つかりませんでした。

  探した場所:
    1) %LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe
    2) PATH 上の dotnet

  .NET 9 SDK をインストールするか、-DotnetPath で dotnet.exe を明示してください。
  https://dotnet.microsoft.com/download/dotnet/9.0
'@
}

Write-Host 'MemEco build' -ForegroundColor White
Write-Host "リポジトリ : $repoRoot"
Write-Host "構成       : $Configuration"

# ---------------------------------------------------------------- dotnet 検出
Write-Step 'dotnet を検出'
try {
    $script:dotnet = Resolve-DotnetPath -Explicit $DotnetPath
}
catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

$sdkVersion = (& $script:dotnet --version 2>&1) | Out-String
$sdkVersion = $sdkVersion.Trim()
Write-Ok "dotnet : $script:dotnet"
Write-Ok "SDK    : $sdkVersion"

# ---------------------------------------------------------------- ビルド
Write-Step "ビルド (.\\MemEco.sln -c $Configuration)"

$buildExe = Join-Path $repoRoot "src\MemEco.App\bin\$Configuration\net9.0-windows\MemEco.exe"
$buildCode = 1
Push-Location -LiteralPath $repoRoot
try {
    & $script:dotnet build '.\MemEco.sln' -c $Configuration --nologo
    $buildCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($buildCode -ne 0) {
    Write-Host ''
    Write-Host "ビルドに失敗しました (exit $buildCode)" -ForegroundColor Red
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'src\MemEco.App\App.xaml.cs'))) {
        Write-Host 'ヒント: src\MemEco.App の実装がまだ無い可能性があります（UI 担当の実装待ち）。' -ForegroundColor Yellow
    }
    exit $buildCode
}

if (-not (Test-Path -LiteralPath $buildExe)) {
    Write-Host ''
    Write-Host "ビルドは成功しましたが、想定の exe がありません: $buildExe" -ForegroundColor Red
    Write-Host '出力パスが変わっていないか csproj を確認してください。' -ForegroundColor Yellow
    exit 1
}

$buildInfo = Get-Item -LiteralPath $buildExe
Write-Ok ("exe : {0} ({1} MB)" -f $buildExe, [math]::Round($buildInfo.Length / 1MB, 2))

# ---------------------------------------------------------------- publish
$publishExe = $null
$publishDir = Join-Path $repoRoot 'publish'
if ($SkipPublish) {
    Write-Step 'publish をスキップ (-SkipPublish)'
}
else {
    Write-Step 'publish (単一 exe / framework-dependent)'
    Write-Host '    -r win-x64 --self-contained false -p:PublishSingleFile=true' -ForegroundColor DarkGray

    $publishCode = 1
    Push-Location -LiteralPath $repoRoot
    try {
        & $script:dotnet publish '.\src\MemEco.App' -c $Configuration -r win-x64 --self-contained false `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir --nologo
        $publishCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    $candidate = Join-Path $publishDir 'MemEco.exe'
    if ($publishCode -eq 0 -and (Test-Path -LiteralPath $candidate)) {
        $publishExe = $candidate
        $publishInfo = Get-Item -LiteralPath $candidate
        Write-Ok ("publish exe : {0} ({1} MB)" -f $publishExe, [math]::Round($publishInfo.Length / 1MB, 2))
    }
    else {
        Write-Warn "publish に失敗しました (exit $publishCode)。通常ビルドの exe を使用します。"
        Write-Warn 'ランタイムパックの取得にはネットワークが必要です。オフラインでは -SkipPublish を指定してください。'
        if (Test-Path -LiteralPath $candidate) {
            Write-Warn "publish フォルダの exe は古い可能性があります: $candidate"
        }
    }
}

# ---------------------------------------------------------------- 結果
$finalExe = if ($publishExe) { $publishExe } else { $buildExe }
$finalInfo = Get-Item -LiteralPath $finalExe

Write-Step '完了'
Write-Host "使用する exe : $finalExe" -ForegroundColor White
Write-Host ("サイズ       : {0} MB / 更新 {1:yyyy-MM-dd HH:mm:ss}" -f [math]::Round($finalInfo.Length / 1MB, 2), $finalInfo.LastWriteTime)
Write-Host ''
Write-Host '次の手順:' -ForegroundColor DarkGray
Write-Host '  検証        : powershell -ExecutionPolicy Bypass -File .\tools\verify.ps1' -ForegroundColor DarkGray
Write-Host '  インストール: powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1' -ForegroundColor DarkGray

exit 0
