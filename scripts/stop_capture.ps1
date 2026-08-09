# capture_session.ps1 で開始したログ捕捉を停止する。
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stop_capture.ps1
# 終了コード: 常に 0（未起動・既に終了済みでもエラーにせず状況を報告する）
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$logsDir = Join-Path $projectRoot "Logs"
$pidFile = Join-Path $logsDir ".capture_pid"

if (-not (Test-Path $pidFile)) {
    Write-Host "捕捉は開始されていません（$pidFile が無い）"
    exit 0
}

$lines = @(Get-Content $pidFile)
$capturePid = $lines[0].Trim()
$logPath = if ($lines.Count -gt 1) { $lines[1].Trim() } else { $null }

$proc = Get-Process -Id $capturePid -ErrorAction SilentlyContinue
if ($null -eq $proc) {
    Write-Host "捕捉プロセス (PID $capturePid) は既に終了しています"
}
else {
    try {
        Stop-Process -Id $capturePid -Force -ErrorAction Stop
        Write-Host "捕捉プロセス (PID $capturePid) を停止しました"
    }
    catch {
        Write-Host "捕捉プロセスの停止に失敗: $($_.Exception.Message)"
    }
}

if ($logPath -and (Test-Path $logPath)) {
    $count = (Get-Content $logPath | Measure-Object -Line).Lines
    Write-Host ""
    Write-Host "ログ: $logPath"
    Write-Host "  行数: $count"
}
elseif ($logPath) {
    Write-Host "ログファイルが見つかりません: $logPath （捕捉行が0件だった可能性）"
}

Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
exit 0
