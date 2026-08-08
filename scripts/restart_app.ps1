# アプリを再起動する (Demo 0 M1 のアンカー復元検証用)。
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart_app.ps1
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$packageName = "com.piperender.blockfield"
$activity = "$packageName/com.unity3d.player.UnityPlayerGameActivity"

$versionLine = Get-Content (Join-Path $projectRoot "ProjectSettings\ProjectVersion.txt") -TotalCount 1
$editorVersion = ($versionLine -split ":\s*")[1].Trim()
$adb = "C:\Program Files\Unity\Hub\Editor\$editorVersion\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if (-not (Test-Path $adb)) {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { Write-Host "adb が見つかりません"; exit 12 }
    $adb = $cmd.Source
}

Write-Host "再起動: $packageName"
& $adb shell "am force-stop $packageName; sleep 2; am start -n $activity"
if ($LASTEXITCODE -ne 0) {
    Write-Host "再起動失敗 (exit $LASTEXITCODE)。HMDがスリープ中だと起動できない — 装着した状態で再実行すること。"
    exit $LASTEXITCODE
}
Write-Host "起動コマンド送信完了。HMDを装着して確認してください。"
exit 0
