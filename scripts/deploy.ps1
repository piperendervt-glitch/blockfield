# ビルド済み APK を Quest 3 へインストールして起動する。
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Aquarium
#         powershell -NoProfile -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Main
# **起動はしない。** capture_session.ps1 でアームしてから restart_app.ps1 で起動すること。
# 終了コード: 0 = 成功 / 9 = ターゲット未指定 / 12 = adb 不明 / 13 = APK 無し / 14 = デバイス未接続 / それ以外 = adb の終了コード
[CmdletBinding()]
param([switch]$Aquarium, [switch]$Main, [switch]$Watch)

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

# 【ターゲットを明示させる】build_quest.ps1 と同じ理由（2026-08-19 のシーン取り違え）。
# 既定を持たせず、どちらの APK を入れるかを毎回書かせる
$targets = @($Aquarium, $Main, $Watch) | Where-Object { $_ }
if ($targets.Count -gt 1) { Write-Host "-Aquarium / -Main / -Watch は同時に指定できません。"; exit 9 }
if ($targets.Count -eq 0) {
    Write-Host "インストールする APK を明示してください: -Aquarium / -Main / -Watch"
    exit 9
}
$apkName = if ($Aquarium) { "blockfield_aquarium.apk" } elseif ($Watch) { "blockfield_watch.apk" } else { "blockfield_main.apk" }
$apk = Join-Path $projectRoot "Builds\$apkName"
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

# 【ここで起動しない】以前は monkey で起動していたが、**キャプチャをアームする前に
# アプリが立ち上がる**ため、セッション冒頭のログを取り落とす（2026-08-19 に発生）。
# monkey 自体もブロックして戻らないことがあった。起動は restart_app.ps1 に任せる
Write-Host "Deploy 完了（起動していない。capture_session.ps1 -> restart_app.ps1 の順で）"
exit 0
