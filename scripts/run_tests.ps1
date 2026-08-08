# EditMode テストをバッチ実行する。
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run_tests.ps1
# 終了コード: 0 = 全テストパス / 10 = Unity Editor が開いている / それ以外 = Unity の終了コードをそのまま返す
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot

# Unity Editor が同プロジェクトを開いているとバッチ実行できない
if (Test-Path (Join-Path $projectRoot "Temp\UnityLockfile")) {
    Write-Host "Unity Editorを閉じてください"
    exit 10
}

# ProjectVersion.txt からエディタバージョンを取得
$versionLine = Get-Content (Join-Path $projectRoot "ProjectSettings\ProjectVersion.txt") -TotalCount 1
$editorVersion = ($versionLine -split ":\s*")[1].Trim()
$unity = "C:\Program Files\Unity\Hub\Editor\$editorVersion\Editor\Unity.exe"
if (-not (Test-Path $unity)) {
    Write-Host "Unity.exe が見つかりません: $unity"
    exit 11
}

$resultsDir = Join-Path $projectRoot "TestResults"
if (-not (Test-Path $resultsDir)) { New-Item -ItemType Directory $resultsDir | Out-Null }
$resultsFile = Join-Path $resultsDir "EditMode.xml"
$logFile = Join-Path $projectRoot "Logs\tests.log"

# -runTests は完了時に自動終了するため -quit は付けない（付けるとテストが走らない）
$unityArgs = @(
    "-batchmode",
    "-projectPath", $projectRoot,
    "-runTests",
    "-testPlatform", "EditMode",
    "-testResults", $resultsFile,
    "-logFile", $logFile
)

Write-Host "Running EditMode tests (Unity $editorVersion)..."
$proc = Start-Process -FilePath $unity -ArgumentList $unityArgs -Wait -PassThru
$code = $proc.ExitCode

if (Test-Path $resultsFile) {
    try {
        [xml]$xml = Get-Content $resultsFile
        $run = $xml.'test-run'
        Write-Host ("Result: {0}  (passed={1} failed={2} total={3})" -f $run.result, $run.passed, $run.failed, $run.total)
    } catch {}
} else {
    Write-Host "警告: テスト結果XMLが生成されていません（コンパイルエラー等の可能性）。Logs\tests.log を確認してください。"
}

Write-Host "Unity exit code: $code"
exit $code
