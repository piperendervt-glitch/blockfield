# 長時間実験: 10万ティック × 5シードをバックグラウンドで走らせる。
#
# 目的:
#   生態系の長期安定性、場の飽和挙動（死の場は τ≈333 なので数万ティックで
#   飽和するはず）、進化導入後の重み分布の変化を観察する。
#   Demo 6「不在中の進行」の前哨実験。
#
#   3,000ティック（夜間バッチの標準セット）では見えないものを見るための実験である。
#   死の場は τ≈333 なので理屈上は 1,500ティック程度で定常に達するはずだが、
#   実際には個体数が揺れるため書き込み量そのものが変動する。その揺れが
#   長期的にどこへ収束するのか（あるいは収束しないのか）を確かめる。
#
# 使い方:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun_sim.ps1
#   powershell ... -File scripts\longrun_sim.ps1 -Ticks 10000 -Seeds 2   # 縮小版
#
# 途中経過は runs\longrun_<日時>\checkpoints.csv に追記されるので、
# 実行中でも別窓から読める。
param(
    [int]$Ticks = 100000,
    [int]$Seeds = 5,
    [int]$CheckpointInterval = 2000,
    [switch]$Foreground
)
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot

if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "dotnet が見つかりません"
    exit 10
}

$stamp = Get-Date -Format "yyyyMMdd_HHmm"
$outDir = Join-Path $projectRoot "runs\longrun_$stamp"
New-Item -ItemType Directory -Force $outDir | Out-Null
$logFile = Join-Path $outDir "run.log"

$simArgs = @(
    "run", "-c", "Release", "--project", (Join-Path $projectRoot "tools\SimRunner"), "--",
    "--seeds", $Seeds, "--ticks", $Ticks,
    "--checkpoint-interval", $CheckpointInterval,
    "--out", $outDir
)

Write-Host "長時間実験: $Seeds シード × $Ticks ティック（チェックポイント $CheckpointInterval ティックごと）"
Write-Host "  出力:   $outDir"
Write-Host "  途中経過: $(Join-Path $outDir 'checkpoints.csv')  ← 実行中でも読める"
Write-Host "  ログ:   $logFile"

if ($Foreground) {
    & dotnet @simArgs 2>&1 | Tee-Object -FilePath $logFile
    exit $LASTEXITCODE
}

# バックグラウンド実行。SSH セッションが切れても走り続けるよう、
# Claude Code や親シェルに紐付かない独立プロセスとして起動する
$proc = Start-Process -FilePath "dotnet" -ArgumentList $simArgs `
    -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $logFile -RedirectStandardError (Join-Path $outDir "run.err")

Write-Host ""
Write-Host "バックグラウンドで開始しました (PID $($proc.Id))"
Write-Host "進捗の確認: Get-Content '$logFile' -Tail 20"
Write-Host "途中経過:   Import-Csv '$(Join-Path $outDir 'checkpoints.csv')' | Select-Object -Last 10"
Write-Host "停止:       Stop-Process -Id $($proc.Id)"
