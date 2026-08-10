# 夜間バッチ: 標準セット（48シード×3000ティック、現行 SimParams）を毎晩回し、
# 前日の結果と比較して回帰を検知する。
#
# 目的: パラメータやコードを触った翌朝に「生態系が壊れていないか」を
# 判断できる状態にしておく。特に ContentHash の不一致（決定論の破れ）は
# 実装中には気づきにくく、後になるほど原因の切り分けが難しくなる。
#
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\nightly_sim.ps1
# 終了コード: SimRunner のものをそのまま返す
#   0 = 問題なし / 1 = M5（生態系の安定条件）不合格 / 2 = 決定論の破れ
#   10 = 前提エラー（dotnet が無い等）
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$runsRoot = Join-Path $projectRoot "runs"

# 標準セット。ここを変えたら docs/remote_work.md の記述も合わせること
$Seeds = 48
$Ticks = 3000
$RetentionDays = 30

if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "dotnet が見つかりません"
    exit 10
}

$stamp = Get-Date -Format "yyyyMMdd"
$outDir = Join-Path $runsRoot "nightly_$stamp"
New-Item -ItemType Directory -Force $outDir | Out-Null
$logFile = Join-Path $outDir "run.log"

# 直近の nightly を比較対象にする（今日の分は除く）。
# 「前日」に限定しないのは、PC が落ちていた日は飛ぶため
$previous = Get-ChildItem $runsRoot -Directory -Filter "nightly_*" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne "nightly_$stamp" -and (Test-Path (Join-Path $_.FullName "summary.json")) } |
    Sort-Object Name -Descending |
    Select-Object -First 1

$simArgs = @(
    "run", "-c", "Release", "--project", (Join-Path $projectRoot "tools\SimRunner"), "--",
    "--seeds", $Seeds, "--ticks", $Ticks, "--out", $outDir
)
if ($null -ne $previous) {
    $prevSummary = Join-Path $previous.FullName "summary.json"
    $simArgs += @("--compare", $prevSummary)
}

"=== nightly_sim.ps1 開始 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Tee-Object -FilePath $logFile
"標準セット: $Seeds シード × $Ticks ティック" | Tee-Object -FilePath $logFile -Append
if ($null -ne $previous) {
    "比較対象: $($previous.Name)" | Tee-Object -FilePath $logFile -Append
} else {
    "比較対象: なし（初回）" | Tee-Object -FilePath $logFile -Append
}

# 出力をログにも残す。Tee-Object を使うのは、実行中に別窓から
# run.log を覗いて進捗が見えるようにするため
& dotnet @simArgs 2>&1 | Tee-Object -FilePath $logFile -Append
$code = $LASTEXITCODE

"" | Tee-Object -FilePath $logFile -Append
switch ($code) {
    0 { "結果: 問題なし" | Tee-Object -FilePath $logFile -Append }
    1 { "結果: M5（生態系の安定条件）が不合格" | Tee-Object -FilePath $logFile -Append }
    2 { "結果: 決定論の破れ（コードは同一なのに ContentHash が不一致）。" +
        "diff_report.html を最優先で確認すること" | Tee-Object -FilePath $logFile -Append }
    default { "結果: SimRunner が異常終了 (exit $code)" | Tee-Object -FilePath $logFile -Append }
}

# 古い結果の削除。ディスクを圧迫しないため。
# 画像を埋め込んだ report.html が1回あたり数百KB〜数MBになる
$cutoff = (Get-Date).AddDays(-$RetentionDays)
$removed = 0
Get-ChildItem $runsRoot -Directory -Filter "nightly_*" -ErrorAction SilentlyContinue | ForEach-Object {
    # ディレクトリ名の日付で判断する。LastWriteTime だと中を覗いただけで
    # 更新されることがあり、消えるべきものが残る
    if ($_.Name -match '^nightly_(\d{8})$') {
        $d = [datetime]::ParseExact($Matches[1], 'yyyyMMdd', $null)
        if ($d -lt $cutoff) {
            Remove-Item $_.FullName -Recurse -Force
            $removed++
        }
    }
}
if ($removed -gt 0) {
    "$RetentionDays 日より前の nightly_* を $removed 件削除しました" | Tee-Object -FilePath $logFile -Append
}

"=== 完了 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Tee-Object -FilePath $logFile -Append
"レポート: $(Join-Path $outDir 'report.html')" | Tee-Object -FilePath $logFile -Append
if (Test-Path (Join-Path $outDir "diff_report.html")) {
    "差分:     $(Join-Path $outDir 'diff_report.html')" | Tee-Object -FilePath $logFile -Append
}

exit $code
