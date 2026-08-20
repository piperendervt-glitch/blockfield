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

# 【子プロセスまで落とす】以前はラッパの powershell だけを止めており、
# **子の adb logcat が生き残っていた**（2026-08-20 のシャットダウン前に発見）。
# ログを書き続けるプロセスが残ると、次の捕捉と混ざる。
$proc = Get-Process -Id $capturePid -ErrorAction SilentlyContinue
if ($null -eq $proc) {
    Write-Host "捕捉プロセス (PID $capturePid) は既に終了しています"
}
else {
    # 子（adb logcat）を先に落とす。親を先に殺すと孤児になって拾えない
    $kids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$capturePid" -ErrorAction SilentlyContinue)
    foreach ($k in $kids) {
        try {
            Stop-Process -Id $k.ProcessId -Force -ErrorAction Stop
            Write-Host "  子プロセス $($k.Name) (PID $($k.ProcessId)) を停止"
        }
        catch { Write-Host "  子プロセス $($k.ProcessId) の停止に失敗: $($_.Exception.Message)" }
    }
    try {
        Stop-Process -Id $capturePid -Force -ErrorAction Stop
        Write-Host "捕捉プロセス (PID $capturePid) を停止しました"
    }
    catch {
        Write-Host "捕捉プロセスの停止に失敗: $($_.Exception.Message)"
    }
}

# 【外の状態で確認する】終了コードではなく、logcat が本当に居ないことを見る。
# 孤児になっていた場合もここで拾って落とす
Start-Sleep -Milliseconds 800
$orphans = @(Get-CimInstance Win32_Process -Filter "Name='adb.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'logcat' })
foreach ($o in $orphans) {
    try {
        Stop-Process -Id $o.ProcessId -Force -ErrorAction Stop
        Write-Host "  残っていた adb logcat (PID $($o.ProcessId)) を停止"
    }
    catch { Write-Host "  adb logcat (PID $($o.ProcessId)) の停止に失敗" }
}
Start-Sleep -Milliseconds 500
$still = @(Get-CimInstance Win32_Process -Filter "Name='adb.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match 'logcat' })
if ($still.Count -eq 0) {
    Write-Host "確認: adb logcat は 0 件"
}
else {
    Write-Host "警告: adb logcat が $($still.Count) 件残っています: $($still.ProcessId -join ', ')"
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
