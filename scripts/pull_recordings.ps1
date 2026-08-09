# Quest 3 で撮った録画を PC へ取り出す (Demo 4.5b)。
# 使い方:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pull_recordings.ps1        # 最新1件
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pull_recordings.ps1 -All   # 全件
# 終了コード: 0 = 成功 / 12 = adb 不明 / 14 = デバイス未接続 / 15 = 録画なし
#
# 録画の撮り方は docs/prereg/demo45b_checklist.md を参照
# （右手Metaボタン → カメラ → 動画を録画）。
param(
    [switch]$All
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$deviceDir = "/sdcard/Oculus/VideoShots"

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

# デバイス接続確認
$deviceLines = (& $adb devices) | Select-Object -Skip 1 | Where-Object { $_ -match "\S" }
$connected = @($deviceLines | Where-Object { $_ -match "\sdevice$" })
if ($connected.Count -eq 0) {
    Write-Host "Quest 3 が接続されていません。adb devices の出力:"
    if ($deviceLines) { $deviceLines | ForEach-Object { Write-Host "  $_" } } else { Write-Host "  (デバイスなし)" }
    exit 14
}

# 一覧を新しい順に取得（ls -1t）。エラー行やディレクトリは弾く
$listing = & $adb shell "ls -1t $deviceDir" 2>$null
$files = @($listing | ForEach-Object { $_.Trim() } | Where-Object { $_ -match "\.(mp4|mkv)$" })

if ($files.Count -eq 0) {
    Write-Host "$deviceDir に録画がありません。"
    Write-Host "録画方法: 右手Metaボタン → カメラ → 動画を録画"
    Write-Host "（ショートカット: Metaボタンを押したまま右トリガー長押し）"
    exit 15
}

if (-not $All) {
    $files = @($files[0])
}

$outDir = Join-Path $projectRoot "Recordings"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory $outDir | Out-Null
    Write-Host "Recordings\ を作成しました"
}

Write-Host "取り出し対象: $($files.Count) 件 $(if ($All) { '(全件)' } else { '(最新1件)' })"

$pulled = 0
$skipped = 0
foreach ($name in $files) {
    $dest = Join-Path $outDir $name

    # 既に同じサイズで取得済みならスキップ（大きいファイルを毎回引かない）
    if (Test-Path $dest) {
        $remoteSize = (& $adb shell "stat -c %s $deviceDir/$name" 2>$null | Select-Object -First 1)
        $localSize = (Get-Item $dest).Length
        if ($remoteSize -match "^\d+$" -and [long]$remoteSize -eq $localSize) {
            Write-Host "  skip  $name (取得済み)"
            $skipped++
            continue
        }
    }

    & $adb pull "$deviceDir/$name" $dest | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dest)) {
        Write-Host "  失敗  $name"
        continue
    }
    $mb = [math]::Round((Get-Item $dest).Length / 1MB, 1)
    Write-Host "  pull  $name ($mb MB)"
    $pulled++
}

Write-Host ""
Write-Host "完了: 取得 $pulled 件 / スキップ $skipped 件"
Write-Host "  保存先: $outDir"
exit 0
