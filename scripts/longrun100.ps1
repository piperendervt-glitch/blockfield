# 100x100 のロングラン (Demo 8 第4段の後、就寝中バッチ)。
#
# 使い方:
#   起動:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun100.ps1 -Detach
#   確認:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun100.ps1 -Status
#   停止:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun100.ps1 -Stop
#
# -Detach は自分自身を独立プロセスとして起動し直して即座に戻る。
# 端末を閉じても SSH を切っても走り続ける（親の子ではなく独立したプロセスになる）。
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
# - 20,000ティック時点: 草食獣 121.5 / 狼 5.7 に落ち着く（平衡到達は t≈10,000）
# - 同じ減少は 50x50 でも起きる（狼 4.6→2.8）ので**サイズ依存ではない**
# - 速度: 1シードあたり 4.47 秒/20,000ティック（14並列、実測）
#   → 10万ティックで約 22.4 秒/シード

[CmdletBinding()]
param(
    [switch]$Detach,
    [switch]$Status,
    [switch]$Stop,
    [int]$Seeds = 540,
    [int]$Ticks = 100000,
    [int]$Size = 100,
    [int]$CheckpointInterval = 10000,
    [int]$Images = 2,
    [string]$OutDir = "runs/longrun100_20260812"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot

# OutDir は相対でも絶対でも受ける（Join-Path は絶対パスを渡すと壊れる）
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $projectRoot $OutDir }
$progressFile = Join-Path $outPath "progress.txt"
$logFile      = Join-Path $outPath "run.log"
$errFile      = Join-Path $outPath "run.err"
$pidFile      = Join-Path $outPath "sim.pid"

function Read-Utf8([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
}

# 実行中のログを読むための共有読み取り。
# Start-Process のリダイレクト先は SimRunner が**書き込みで掴んだまま**なので、
# ReadAllText では "being used by another process" で落ちる。
# 読み取り中の書き込みを許す FileShare で開く必要がある。
function Read-Shared([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
            return $sr.ReadToEnd()
        } finally { $fs.Dispose() }
    } catch {
        return $null   # 一時的に読めなくても監視は続ける
    }
}

function Write-Utf8([string]$path, [string[]]$lines) {
    [System.IO.File]::WriteAllText($path, ($lines -join "`r`n") + "`r`n",
        (New-Object System.Text.UTF8Encoding $false))
}

# ---- -Status: 進捗を表示するだけ ----
if ($Status) {
    $text = Read-Utf8 $progressFile
    if ($null -eq $text) {
        Write-Host "progress.txt がありません。まだ起動していない可能性があります: $progressFile"
        exit 1
    }
    Write-Host $text
    $simPid = (Read-Utf8 $pidFile)
    if ($simPid) {
        $alive = @(Get-Process -Id ([int]$simPid.Trim()) -ErrorAction SilentlyContinue).Count -gt 0
        Write-Host ("プロセス: " + $(if ($alive) { "実行中 (PID $($simPid.Trim()))" } else { "終了済み" }))
    }
    exit 0
}

# ---- -Stop: 走っているものを止める ----
if ($Stop) {
    $simPid = (Read-Utf8 $pidFile)
    if ($simPid) {
        $p = Get-Process -Id ([int]$simPid.Trim()) -ErrorAction SilentlyContinue
        if ($p) { $p | Stop-Process -Force; Write-Host "停止しました (PID $($simPid.Trim()))"; exit 0 }
    }
    Write-Host "実行中のプロセスが見つかりません"
    exit 0
}

# ---- -Detach: 自分自身を独立プロセスとして起動し直す ----
if ($Detach) {
    # 出力先はここで作る。子プロセスの起動より前に用意しておくことで、
    # 呼び出し側が直後に progress.txt を読んでも競合しない
    New-Item -ItemType Directory -Force -Path $outPath | Out-Null
    Write-Utf8 $progressFile @(
        "状態: 起動要求",
        "要求時刻: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
        "構成: ${Size}x${Size} / $Ticks ティック / $Seeds シード",
        "（本体の起動待ち。数秒で「実行中」に変わる）"
    )

    $self = $MyInvocation.MyCommand.Path
    $childArgs = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $self,
        "-Seeds", $Seeds, "-Ticks", $Ticks, "-Size", $Size,
        "-CheckpointInterval", $CheckpointInterval, "-Images", $Images,
        "-OutDir", $outPath
    )
    $child = Start-Process -FilePath "powershell.exe" -ArgumentList $childArgs `
        -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru

    Write-Host "起動しました (ラッパー PID $($child.Id))"
    Write-Host "進捗: $progressFile"
    Write-Host "確認: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun100.ps1 -Status"
    exit 0
}

# ================= 本体 =================

$exe = Join-Path $projectRoot "tools\SimRunner\bin\Release\net9.0\SimRunner.exe"
if (-not (Test-Path $exe)) {
    throw "SimRunner がビルドされていません: $exe  (dotnet build -c Release tools/SimRunner)"
}

# 1) 出力ディレクトリ → 2) progress.txt 初期化 → 3) 本体起動 の順を守る
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$start = Get-Date
# 1シードあたりの実測値（20,000ティック × 14並列）から総時間を見積もる
$secPerSeed = 4.47 * ($Ticks / 20000.0)
$estimateSec = $secPerSeed * $Seeds

function Write-ProgressFile([string]$state, [string]$detail, [double]$fraction) {
    $elapsed = (Get-Date) - $start
    $lines = @(
        "状態: $state",
        "開始: $($start.ToString('yyyy-MM-dd HH:mm:ss'))",
        "経過: $([int]$elapsed.TotalMinutes) 分 $([int]($elapsed.TotalSeconds % 60)) 秒",
        "構成: ${Size}x${Size} / $Ticks ティック / $Seeds シード / チェックポイント $CheckpointInterval ティックごと",
        $detail
    )
    if ($fraction -gt 0 -and $fraction -lt 1) {
        # 実績ベースで残りを推定する
        $projected = $elapsed.TotalSeconds / $fraction
        $eta = $start.AddSeconds($projected)
        $lines += "完了予測: $($eta.ToString('yyyy-MM-dd HH:mm:ss'))  (実績ベース)"
        $lines += "残り: 約 $([int](($projected - $elapsed.TotalSeconds) / 60)) 分"
    } else {
        $eta = $start.AddSeconds($estimateSec)
        $lines += "完了予測: $($eta.ToString('yyyy-MM-dd HH:mm:ss'))  (事前見積もり)"
    }
    Write-Utf8 $progressFile $lines
}

Write-ProgressFile "起動中" "SimRunner を起動している" 0

$simArgs = @(
    "--seeds", $Seeds,
    "--ticks", $Ticks,
    "--size", $Size,
    "--images", $Images,
    "--checkpoint-interval", $CheckpointInterval,
    "--out", $outPath
)

# 標準出力をファイルへ流し、こちらは**定期的に読む**。
# パイプで受けて1行ずつ処理する形にすると、SimRunner の進捗行が
# 10%刻みでしか出ないため、次の行が来るまで progress.txt が
# 「起動中」のまま止まって見える（実際に走っていても分からない）。
$proc = Start-Process -FilePath $exe -ArgumentList $simArgs `
    -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $logFile -RedirectStandardError $errFile

# Start-Process -PassThru で返る Process は、**終了前に Handle を掴んでおかないと**
# 終了後の ExitCode が取れない（PowerShell の既知の癖）。ここで一度触っておく
$null = $proc.Handle

Write-Utf8 $pidFile @("$($proc.Id)")

# 進捗の監視。行の到着に依存せず一定間隔で更新するので、
# 何も出力が無い時間帯でも「経過」が動いて生存が分かる
$lastDetail = "シミュレーション開始待ち"
$fraction = 0.0
while (-not $proc.HasExited) {
    Start-Sleep -Seconds 15

    # 監視でこけても本体は走り続けるべきなので、ここは握りつぶす。
    # （$ErrorActionPreference = "Stop" なので、囲まないと監視の失敗が
    #   そのままラッパーの死になり、進捗が「起動中」で固まる）
    try {
        $log = Read-Shared $logFile
        if ($log) {
            # SimRunner の進捗行は "  ... 123/540 (22%) 456s" の形
            $found = [regex]::Matches($log, '\.\.\.\s+(\d+)/(\d+)\s+\((\d+)%\)')
            if ($found.Count -gt 0) {
                $m = $found[$found.Count - 1]
                $done = [int]$m.Groups[1].Value
                $total = [int]$m.Groups[2].Value
                if ($total -gt 0) {
                    $fraction = $done / [double]$total
                    $lastDetail = "進捗: $done / $total ラン ($($m.Groups[3].Value)%)"
                }
            }
            if ($log -match 'シミュレーション完了') {
                $fraction = 0.99
                $lastDetail = "シミュレーション完了。画像生成と集計に入った（数分かかる）"
            }
        }
        Write-ProgressFile "実行中" $lastDetail $fraction
    } catch {
        # 次の周回で取り直す
    }
}

$proc.WaitForExit()
$code = $proc.ExitCode
$elapsed = (Get-Date) - $start

$errText = Read-Shared $errFile
$final = @(
    "状態: 完了 (終了コード $code)",
    "開始: $($start.ToString('yyyy-MM-dd HH:mm:ss'))",
    "終了: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
    "所要: $([math]::Floor($elapsed.TotalHours)) 時間 $($elapsed.Minutes) 分 $($elapsed.Seconds) 秒",
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
if ($errText -and $errText.Trim().Length -gt 0) {
    $final += ""
    $final += "**標準エラーに出力があった**（run.err を確認すること）:"
    $final += ($errText.Trim() -split "`n" | Select-Object -First 5)
}
Write-Utf8 $progressFile $final
exit $code
