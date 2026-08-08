# ビルド済み APK を Quest 3 へインストールして起動する。
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\deploy.ps1
# 終了コード: 0 = 成功 / 12 = adb 不明 / 13 = APK 無し / 14 = デバイス未接続 / それ以外 = adb の終了コード
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$packageName = "com.piperender.blockfield"

# adb: Unity 同梱 SDK を既定、無ければ PATH にフォールバック
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

$apk = Join-Path $projectRoot "Builds\blockfield.apk"
if (-not (Test-Path $apk)) {
    Write-Host "APK がありません: $apk  （先に scripts\build_quest.ps1 を実行してください）"
    exit 13
}

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

Write-Host "Installing $apk ..."
& $adb install -r $apk
if ($LASTEXITCODE -ne 0) {
    Write-Host "adb install 失敗 (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host "Launching $packageName ..."
& $adb shell monkey -p $packageName 1
if ($LASTEXITCODE -ne 0) {
    Write-Host "起動失敗 (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host "Deploy 完了"
exit 0
