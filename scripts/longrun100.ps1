# 100x100 のロングラン (Demo 8 第4段の後、就寝中バッチ)。
#
# 使い方: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun100.ps1
# 既定は 100x100 × 10万ティック × 540シード。引数で上書きできる。
#
# 【観察目的】この実験で見たいこと。結果を読むときはこの順で見る。
#
# 1. スケーリング: 均衡個体数は面積比例か（密度一定か）。
#    予備計測(20,000ティック)では 適性セル比 4.16 に対し
#    草食獣は 4.40 倍とほぼ比例、**狼は 2.04 倍と明確に劣線形**だった。
#    10万ティックでこの劣線形が保たれるのか、さらに開くのかを見る。
#    捕食者が広い世界ほど「薄く」なるなら、それは縄張りの空間的制約を示唆する。
#
# 2. 群れの構造: 広い世界で群れの「数」が増えるのか「サイズ」が大きくなるのか。
#    checkpoints.csv の flock_*_neighbor（1個体あたりの同種近傍数＝群れのサイズ）と
#    flock_*_concentration（上位10%セルへの集中＝拠点の数の少なさ）の
#    組み合わせで読む。近傍数だけ増えるなら「大きな群れ」、
#    集中度だけ上がるなら「拠点が減った」。
#    狼のパックが空間分離するか（＝縄張りの萌芽）は
#    flock_wolf_pairdist が世界の広さに対して小さいまま留まるかで見る。
#
# 3. けもの道の網目: 広い空間で踏み荒らし場がどう組織化されるか。
#    **冗長性を残すか**（複数経路を維持するか、1本に収束するか）が
#    Physarum（真正粘菌の経路網）予測の初観察になる。
#    trample_mean と trample_max の比、および最終時点の PNG で見る。
#
# 4. 長期安定性: 10万ティックで無絶滅か。
#    **注意: guildExtinct 基準には既知の欠陥がある** — 時間を通した最小値が
#    0 になった瞬間を捉える判定なので、1ティックでも 0 になれば
#    「全滅」と記録される（その後回復しても）。個体数が上限付近で
#    振動する 100x100 では誤検出しやすい。
#    checkpoints.csv の推移を必ず併せて読むこと。
#
# 【予備計測の記録 (2026-08-12)】
# - 100x100 は 50x50 の適性セル 4.16 倍（適性率 0.981 対 0.943）
# - 3,000ティック時点: 草食獣 74.6 / 狼 17.4（4c の 50x50 値のほぼ4倍）
# - 20,000ティック時点: 草食獣 121.5 / 狼 5.7 に落ち着く
#   （草食獣は上限 ~132 付近、狼は 3,000t の 1/3 に減る）
# - 同じ減少は 50x50 でも起きる（狼 4.6→2.8）ので**サイズ依存ではない**
# - 平衡到達は t≈10,000。10万ティックはその10倍の観察窓になる
# - 速度: 1シードあたり 4.47 秒/20,000ティック（14並列、実測）
#   → 10万ティックで約 22.4 秒/シード

param(
    [int]$Seeds = 540,
    [int]$Ticks = 100000,
    [int]$Size = 100,
    [int]$CheckpointInterval = 10000,
    [int]$Images = 2,
    [string]$OutDir = "runs/longrun100_20260812"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

$exe = Join-Path $projectRoot "tools\SimRunner\bin\Release\net9.0\SimRunner.exe"
if (-not (Test-Path $exe)) {
    throw "SimRunner がビルドされていません: $exe  (dotnet build -c Release tools/SimRunner)"
}

# OutDir は相対でも絶対でも受ける（Join-Path は絶対パスを渡すと壊れる）
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $projectRoot $OutDir }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null
$progressFile = Join-Path $outPath "progress.txt"
$logFile = Join-Path $outPath "run.log"

# 1シードあたりの実測値（20,000ティック × 14並列）から総時間を見積もる
$secPerSeed = 4.47 * ($Ticks / 20000.0)
$estimateSec = $secPerSeed * $Seeds
$start = Get-Date

function Write-Progress-File([string]$state, [string]$detail, [double]$fraction) {
    $elapsed = (Get-Date) - $start
    $lines = @(
        "状態: $state",
        "開始: $($start.ToString('yyyy-MM-dd HH:mm:ss'))",
        "経過: $([int]$elapsed.TotalMinutes) 分",
        "構成: ${Size}x${Size} / $Ticks ティック / $Seeds シード / チェックポイント $CheckpointInterval ティックごと",
        $detail
    )
    if ($fraction -gt 0 -and $fraction -lt 1) {
        # 実績ベースで残りを推定する（起動直後は事前見積もりに寄る）
        $projectedTotal = $elapsed.TotalSeconds / $fraction
        $eta = $start.AddSeconds($projectedTotal)
        $lines += "完了予測: $($eta.ToString('yyyy-MM-dd HH:mm:ss'))  (実績ベース)"
    } elseif ($fraction -le 0) {
        $eta = $start.AddSeconds($estimateSec)
        $lines += "完了予測: $($eta.ToString('yyyy-MM-dd HH:mm:ss'))  (事前見積もり)"
    }
    Set-Content -LiteralPath $progressFile -Value $lines -Encoding utf8
}

Write-Progress-File "起動中" "シミュレーション開始前" 0

$simArgs = @(
    "--seeds", $Seeds,
    "--ticks", $Ticks,
    "--size", $Size,
    "--images", $Images,
    "--checkpoint-interval", $CheckpointInterval,
    "--out", $outPath
)

# 出力を1行ずつ受けて progress.txt を更新する。
# SimRunner の進捗行は "  ... 123/540 (22%) 456s" の形。
& $exe @simArgs 2>&1 | ForEach-Object {
    $line = $_
    Add-Content -LiteralPath $logFile -Value $line -Encoding utf8

    if ($line -match '\.\.\.\s+(\d+)/(\d+)\s+\((\d+)%\)') {
        $done = [int]$Matches[1]
        $total = [int]$Matches[2]
        Write-Progress-File "実行中" "進捗: $done / $total ラン ($($Matches[3])%)" ($done / [double]$total)
    }
    elseif ($line -match 'シミュレーション完了') {
        Write-Progress-File "集計中" "シミュレーション完了。画像生成と集計に入った（数分かかる）" 0.99
    }
}

$code = $LASTEXITCODE
$elapsed = (Get-Date) - $start
$final = @(
    "状態: 完了 (終了コード $code)",
    "開始: $($start.ToString('yyyy-MM-dd HH:mm:ss'))",
    "終了: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
    "所要: $([int]$elapsed.TotalMinutes) 分",
    "構成: ${Size}x${Size} / $Ticks ティック / $Seeds シード",
    "",
    "出力:",
    "  $outPath\report.html      ← これ1枚で全部見られる",
    "  $outPath\summary.json     ← 集計と群れ指標",
    "  $outPath\checkpoints.csv  ← $CheckpointInterval ティックごとの推移（群れ指標を含む）",
    "  $outPath\population.csv",
    "  $outPath\run.log",
    "",
    "終了コード: 0=問題なし / 1=M5 不合格 / 2=決定論の破れ"
)
Set-Content -LiteralPath $progressFile -Value $final -Encoding utf8
exit $code
