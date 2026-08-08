# アンカー保存を含むアプリデータを初期化する (Demo 0 M1 検証の事前リセット用)。
# 注意: pm clear はランタイム権限 (USE_SCENE) も初期化する。
#       次回起動時に権限ダイアログが再度表示されるので、ヘッドセット内で「許可」すること。
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\reset_anchor.ps1
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$packageName = "com.piperender.blockfield"

$versionLine = Get-Content (Join-Path $projectRoot "ProjectSettings\ProjectVersion.txt") -TotalCount 1
$editorVersion = ($versionLine -split ":\s*")[1].Trim()
$adb = "C:\Program Files\Unity\Hub\Editor\$editorVersion\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if (-not (Test-Path $adb)) {
    $cmd = Get-Command adb -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { Write-Host "adb が見つかりません"; exit 12 }
    $adb = $cmd.Source
}

Write-Host "アプリデータを初期化: $packageName (アンカー保存と権限が消えます)"
& $adb shell pm clear $packageName
if ($LASTEXITCODE -ne 0) {
    Write-Host "pm clear 失敗 (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}
Write-Host "完了。次回起動時に USE_SCENE 権限ダイアログが再表示されます。"
exit 0
