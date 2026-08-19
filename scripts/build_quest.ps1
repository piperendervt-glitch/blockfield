# Quest 3 向け APK をバッチビルドする。
# 使い方:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build_quest.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build_quest.ps1 -Aquarium
#
# -Aquarium は系列2 Phase B の水槽シーン (Assets/Scenes/Aquarium.unity) を焼く。
# **同じパッケージ名で上書きインストールになる**ので、実機に入るのは
# 最後にビルドしたほうだけ。どちらを焼いたかは Logs\build.log の
# 「[BuildScript] 焼くシーン:」の行で確認できる。
#
# 終了コード: 0 = 成功 / 9 = ターゲット未指定 / 10 = Unity Editor が開いている / 11 = Unity.exe 不明 / それ以外 = Unity の終了コード
[CmdletBinding()]
param([switch]$Aquarium, [switch]$Main)

$ErrorActionPreference = "Stop"

if ($Aquarium -and $Main) {
    Write-Host "-Aquarium と -Main は同時に指定できません。"
    exit 9
}
if (-not $Aquarium -and -not $Main) {
    Write-Host "ビルドするシーンを明示してください:"
    Write-Host "  -Aquarium : 系列2 水槽シーン (Assets/Scenes/Aquarium.unity) -> Builds/blockfield_aquarium.apk"
    Write-Host "  -Main     : 系列1 本編シーン (Assets/Scenes/Main.unity)     -> Builds/blockfield_main.apk"
    Write-Host ""
    Write-Host "既定を持たせないのは、付け忘れで別シーンを実機へ入れる事故が起きたため（2026-08-19）。"
    exit 9
}

$projectRoot = Split-Path -Parent $PSScriptRoot

if (Test-Path (Join-Path $projectRoot "Temp\UnityLockfile")) {
    Write-Host "Unity Editorを閉じてください"
    exit 10
}

$versionLine = Get-Content (Join-Path $projectRoot "ProjectSettings\ProjectVersion.txt") -TotalCount 1
$editorVersion = ($versionLine -split ":\s*")[1].Trim()
$unity = "C:\Program Files\Unity\Hub\Editor\$editorVersion\Editor\Unity.exe"
if (-not (Test-Path $unity)) {
    Write-Host "Unity.exe が見つかりません: $unity"
    exit 11
}

$logFile = Join-Path $projectRoot "Logs\build.log"

$method = if ($Aquarium) { "BuildScript.BuildAquarium" } else { "BuildScript.BuildQuest" }
$label = if ($Aquarium) { "水槽シーン (Aquarium.unity) -> blockfield_aquarium.apk" } else { "本編シーン (Main.unity) -> blockfield_main.apk" }

$unityArgs = @(
    "-batchmode",
    "-nographics",
    "-quit",
    "-projectPath", $projectRoot,
    "-buildTarget", "Android",
    "-executeMethod", $method,
    "-logFile", $logFile
)

Write-Host "Building Quest APK (Unity $editorVersion) — $label"
Write-Host "  初回は IL2CPP で10分以上かかることがある"
$proc = Start-Process -FilePath $unity -ArgumentList $unityArgs -Wait -PassThru
$code = $proc.ExitCode

# 完了行に出すパスも BuildScript 側と合わせる。ここが古いと
# 「どちらを焼いたか」の確認にならない
$apkName = if ($Aquarium) { "blockfield_aquarium.apk" } else { "blockfield_main.apk" }
$apk = Join-Path $projectRoot "Builds\$apkName"
if ($code -eq 0 -and (Test-Path $apk)) {
    $sizeMb = [math]::Round((Get-Item $apk).Length / 1MB, 1)
    Write-Host "APK: $apk ($sizeMb MB)"
} elseif ($code -eq 0) {
    Write-Host "警告: 終了コード0だが APK が存在しない。Logs\build.log を確認してください。"
    exit 12
} else {
    Write-Host "ビルド失敗 (exit $code)。Logs\build.log の末尾を確認してください。"
}

exit $code
