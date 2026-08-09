# 実機セッションのログをバックグラウンドで捕捉する。
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\capture_session.ps1
# 終了時は scripts\stop_capture.ps1 を実行すること。
# 終了コード: 0 = 起動成功 / 12 = adb 不明 / 14 = デバイス未接続
#
# 設計意図: Claude Code がセッション中に logcat を読み続けるとトークンを浪費し
# 出力ループの原因になる。捕捉はこのスクリプトに任せ、セッション後に
# Logs\session_*.log を一度だけ読む運用にする（CLAUDE.md 実機テスト運用）。
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot

# adb: Unity 同梱 SDK を既定、無ければ PATH にフォールバック（deploy.ps1 と同方式）
$versionLine = Get-Content (Join-Path $projectRoot "ProjectSettings\ProjectVersion.txt") -TotalCount 1
$editorVersion = ($versionLine -split ":\s*")[1].Trim()
$adb = "C:\Program Files\Unity\Hub\Editor\$editorVersion\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if (-not (Test-Path $adb)) {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($null -eq $cmd) {
        Write-Host "adb が見つかりません（Unity同梱SDKにもPATHにも無い）"
        exit 12
    }
    $adb = $cmd.Source
}
Write-Host "adb: $adb"

# デバイス接続確認
$deviceLines = (& $adb devices) | Select-Object -Skip 1 | Where-Object { $_ -match "\S" }
$connected = @($deviceLines | Where-Object { $_ -match "\sdevice$" })
if ($connected.Count -eq 0) {
    Write-Host "Quest 3 が接続されていません。adb devices の出力:"
    if ($deviceLines) { $deviceLines | ForEach-Object { Write-Host "  $_" } } else { Write-Host "  (デバイスなし)" }
    if ($deviceLines -match "unauthorized") {
        Write-Host "ヘッドセット内で USB デバッグ許可のダイアログを承認してください。"
    }
    exit 14
}
$model = (& $adb shell getprop ro.product.model) -join ""
Write-Host "接続デバイス: $($connected[0].Split()[0]) (model: $model)"

# 既存の捕捉が動いていれば先に止める
$logsDir = Join-Path $projectRoot "Logs"
if (-not (Test-Path $logsDir)) { New-Item -ItemType Directory $logsDir | Out-Null }
$pidFile = Join-Path $logsDir ".capture_pid"
if (Test-Path $pidFile) {
    $oldPid = (Get-Content $pidFile -TotalCount 1).Trim()
    $oldProc = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
    if ($null -ne $oldProc) {
        Write-Host "既存の捕捉プロセス (PID $oldPid) を停止します"
        try { Stop-Process -Id $oldPid -Force -ErrorAction Stop } catch {}
    }
}

# セッション境界を明確にするためバッファをクリア
& $adb logcat -c
Write-Host "logcat バッファをクリアしました"

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logPath = Join-Path $logsDir "session_$stamp.log"

# 捕捉対象: Unity からのログ全部 ＋ パススルー合成の証跡 ＋ 例外・エラー行。
#
# タグ列挙をやめた理由: Demo 4.5b で [VrMode] [Passthrough] [RoomShell] を
# 追加したときにこの列挙を更新し忘れ、セッション後に「切替が呼ばれたのか」を
# 判定できなかった（2026-08-09）。タグを増やすたびに更新が要る仕組みは
# 同じ抜けを繰り返すので、Unity 行はまとめて拾う。
#
# パススルーは OpenXR のコンポジションレイヤーで、Meta OpenXR の
# CameraSubsystem.Start/Stop が Create/DestroyPassthroughLayer を呼ぶ。
# その package 側ログと、システム側の CameraStream [passthrough] も証跡として拾う。
$pattern = "I/Unity|E/Unity|W/Unity|PassthroughLayer|\[passthrough\]|E/|Exception|Error"

# 子プロセス用のワーカースクリプト。StreamWriter の AutoFlush で
# セッション中でもログが逐次書き出される（Out-File はバッファされるため使わない）
#
# 再接続ループ: adb logcat は HMD のスリープ/装着による USB 再列挙などで
# 予告なく終了する（2026-08-09 のセッションで捕捉が7分で無言停止し、
# アプリ起動を1行も記録できなかった）。パイプが切れたら再接続し、
# 切断/再接続の時刻をマーカー行として残す。
$workerPath = Join-Path $logsDir ".capture_worker.ps1"
$worker = @"
`$ErrorActionPreference = 'Continue'
# adb の出力は UTF-8。既定のコンソールエンコーディング（日本語環境では CP932）で
# 解釈すると日本語ログが文字化けするため、明示的に UTF-8 を指定する
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
`$OutputEncoding = [System.Text.Encoding]::UTF8
# append=true: 再接続のたびに切り詰めないため（ファイル名は毎回新規）
`$writer = New-Object System.IO.StreamWriter('$logPath', `$true, (New-Object System.Text.UTF8Encoding(`$true)))
`$writer.AutoFlush = `$true
try {
    while (`$true) {
        `$writer.WriteLine('--- capture: logcat 接続 ' + (Get-Date -Format 'HH:mm:ss') + ' ---')
        try {
            & '$adb' logcat -v time | ForEach-Object {
                if (`$_ -match '$pattern') { `$writer.WriteLine(`$_) }
            }
        }
        catch {
            `$writer.WriteLine('--- capture: 例外 ' + `$_.Exception.Message + ' ---')
        }
        `$writer.WriteLine('--- capture: logcat 切断 ' + (Get-Date -Format 'HH:mm:ss') + '。3秒後に再接続 ---')
        Start-Sleep -Seconds 3
    }
}
finally {
    `$writer.Close()
}
"@
[System.IO.File]::WriteAllText($workerPath, $worker, (New-Object System.Text.UTF8Encoding($true)))

$proc = Start-Process powershell `
    -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $workerPath `
    -WindowStyle Hidden -PassThru

# PID と出力先を記録（stop_capture.ps1 が読む）
Set-Content -Path $pidFile -Value @($proc.Id, $logPath) -Encoding ascii

Write-Host ""
Write-Host "捕捉を開始しました (PID $($proc.Id))"
Write-Host "  出力先: $logPath"
Write-Host "  対象: Unity ログ全部 ＋ パススルー合成の証跡 ＋ エラー行"
Write-Host "  終了: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\stop_capture.ps1"
exit 0
