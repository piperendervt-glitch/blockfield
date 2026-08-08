# Quest 3 向け APK をバッチビルドする。
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build_quest.ps1
# 終了コード: 0 = 成功 / 10 = Unity Editor が開いている / 11 = Unity.exe 不明 / それ以外 = Unity の終了コード
$ErrorActionPreference = "Stop"

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

$unityArgs = @(
    "-batchmode",
    "-nographics",
    "-quit",
    "-projectPath", $projectRoot,
    "-buildTarget", "Android",
    "-executeMethod", "BuildScript.BuildQuest",
    "-logFile", $logFile
)

Write-Host "Building Quest APK (Unity $editorVersion)... 初回は IL2CPP で10分以上かかることがある"
$proc = Start-Process -FilePath $unity -ArgumentList $unityArgs -Wait -PassThru
$code = $proc.ExitCode

$apk = Join-Path $projectRoot "Builds\blockfield.apk"
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
