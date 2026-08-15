# 100x100 のロングラン (Demo 8 第4段の後、就寝中バッチ)。
#
# 使い方:
#   起動:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun100.ps1 -Detach
#   確認:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun100.ps1 -Status
#   停止:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun100.ps1 -Stop
#
# -Detach は自分自身を独立プロセスとして起動し直して即座に戻る。
#
# 【なぜ Start-Process ではなく WMI で起こすか — 2026-08-12 の事故】
# 以前は Start-Process で本体を起こしていたが、それは**ただの子プロセス**であり、
# 起動した端末のプロセスツリー（Windows Terminal では同一 Job Object）に属したままだった。
# タブを閉じるとツリーごと TerminateProcess され、
# **例外もイベントログも残さずに静かに死ぬ**。
# 実際 540シード×10万ティックの本番が起動2分33秒で消え、run.err は空、
# WER も .NET Runtime イベントも無し、という状態になった。
# Win32_Process.Create で起こすと親が WmiPrvSE になり、
# 端末のツリーからも Job Object からも外れる。端末を閉じても生き残る。
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
    # SimRunner の --conditions にそのまま渡す（カンマ区切り）。
    # 空なら SimRunner の既定（default 単独）になる。
    # 第4.5段の E1 のように条件を指定して長時間回す用途で使う
    [string]$Conditions = "",
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

    # 本体とラッパーを別々に見る。「本体は生きているのにラッパーだけ死んだ」
    # （＝進捗が更新されないだけで実験は無事）と
    # 「両方死んだ」（＝実験が止まった）を区別するため
    function Test-Pid([string]$path, [string]$label) {
        $raw = Read-Utf8 $path
        if (-not $raw) { return }
        $alive = @(Get-Process -Id ([int]$raw.Trim()) -ErrorAction SilentlyContinue).Count -gt 0
        Write-Host ("${label}: " + $(if ($alive) { "実行中 (PID $($raw.Trim()))" } else { "終了済み" }))
    }
    Test-Pid $pidFile "本体 (SimRunner)"
    Test-Pid (Join-Path $outPath "wrapper.pid") "ラッパー (進捗更新)"

    # 進捗が止まっていないかは、progress.txt ではなく
    # **本体が書いている csv の更新時刻**で見るのが確実
    $cp = Join-Path $outPath "checkpoints.csv"
    if (Test-Path -LiteralPath $cp) {
        $age = (Get-Date) - (Get-Item -LiteralPath $cp).LastWriteTime
        Write-Host ("checkpoints.csv 最終更新: $([int]$age.TotalMinutes) 分前")
    }
    exit 0
}

# ---- -Stop: 走っているものを止める ----
if ($Stop) {
    # ラッパー → 本体 の順に落とす。逆順だとラッパーが
    # 「本体が終わった」と判断して完了扱いの最終行を書いてしまう
    $stopped = @()
    foreach ($f in @((Join-Path $outPath "wrapper.pid"), $pidFile)) {
        $raw = Read-Utf8 $f
        if (-not $raw) { continue }
        $p = Get-Process -Id ([int]$raw.Trim()) -ErrorAction SilentlyContinue
        if ($p) { $p | Stop-Process -Force; $stopped += $raw.Trim() }
    }
    if ($stopped.Count -gt 0) {
        Write-Host "停止しました (PID $($stopped -join ', '))"
    } else {
        Write-Host "実行中のプロセスが見つかりません"
    }
    exit 0
}

# ---- -Detach: 自分自身を独立プロセスとして起動し直す ----
if ($Detach) {
    # 出力先はここで作る。子プロセスの起動より前に用意しておくことで、
    # 呼び出し側が直後に progress.txt を読んでも競合しない
    New-Item -ItemType Directory -Force -Path $outPath | Out-Null
    # 前回の PID を消しておく。残っていると -Status が
    # 使い回された別プロセスの PID を「実行中」と誤報する
    Remove-Item -LiteralPath $pidFile, (Join-Path $outPath "wrapper.pid") `
        -Force -ErrorAction SilentlyContinue
    Write-Utf8 $progressFile @(
        "状態: 起動要求",
        "要求時刻: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))",
        "構成: ${Size}x${Size} / $Ticks ティック / $Seeds シード",
        "（本体の起動待ち。数秒で「実行中」に変わる）"
    )

    $self = $MyInvocation.MyCommand.Path
    # WMI に渡すのは配列ではなく1本のコマンドライン文字列なので、
    # 空白を含むパスは自分で引用する
    $childCmd = ('powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden ' +
        '-File "{0}" -Seeds {1} -Ticks {2} -Size {3} ' +
        '-CheckpointInterval {4} -Images {5} -OutDir "{6}"') -f `
        $self, $Seeds, $Ticks, $Size, $CheckpointInterval, $Images, $outPath
    if ($Conditions -ne "") {
        $childCmd += ' -Conditions "{0}"' -f $Conditions
    }

    # 端末のプロセスツリー / Job Object の外へ出す（冒頭のコメント参照）。
    # 失敗したら従来どおり Start-Process へ落とすが、その場合は
    # 端末を閉じると死ぬので警告を出す
    $childPid = $null
    try {
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create `
            -Arguments @{ CommandLine = $childCmd; CurrentDirectory = $projectRoot }
        if ($r.ReturnValue -eq 0) {
            $childPid = [int]$r.ProcessId
        } else {
            Write-Warning "Win32_Process.Create が失敗しました (ReturnValue=$($r.ReturnValue))"
        }
    } catch {
        Write-Warning "Win32_Process.Create を呼べません: $($_.Exception.Message)"
    }

    if ($null -eq $childPid) {
        $childArgs = @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $self,
            "-Seeds", $Seeds, "-Ticks", $Ticks, "-Size", $Size,
            "-CheckpointInterval", $CheckpointInterval, "-Images", $Images,
            "-OutDir", $outPath
        )
        if ($Conditions -ne "") { $childArgs += @("-Conditions", $Conditions) }
        $child = Start-Process -FilePath "powershell.exe" -ArgumentList $childArgs `
            -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
        $childPid = $child.Id
        Write-Warning "Start-Process で代替起動しました。**この端末を閉じると実行も死にます**"
    }

    # ラッパー自身の PID も残す。-Status で「本体は生きているがラッパーが死んだ」を
    # 区別できるようにするため（2026-08-12 の事故ではこれが分からなかった）
    Write-Utf8 (Join-Path $outPath "wrapper.pid") @("$childPid")

    Write-Host "起動しました (ラッパー PID $childPid)"
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
        "条件: $(if ($Conditions -ne '') { $Conditions } else { 'default（既定）' })",
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
if ($Conditions -ne "") { $simArgs += @("--conditions", $Conditions) }

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

# 【最終状態は何があっても書く】ここで例外が出ると $ErrorActionPreference = "Stop" で
# ラッパーが最終行を書かずに死に、progress.txt が「実行中」のまま取り残される。
# それは 2026-08-12 の事故（外部からのツリー kill）と**見分けがつかない**ので、
# 後始末は握りつぶしてでも必ず1行残す
$code = -1
try {
    $proc.WaitForExit()
    $code = $proc.ExitCode
} catch {
    $code = -1
}
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
} elseif ($code -notin @(0, 1, 2)) {
    # 終了コードが想定外なのに標準エラーが空 ＝ 本体は例外を投げていない。
    # .NET の未処理例外なら必ず run.err に出るので、この形は
    # **外部からの TerminateProcess** を強く示唆する（2026-08-12 の事故）
    $final += ""
    $final += "**標準エラーは空なのに異常終了している** — 外部から強制終了された可能性が高い。"
    $final += "端末を閉じた / タスクマネージャで殺した / 電源断 などを疑うこと。"
    $final += "checkpoints.csv はそこまでの分が残っているので、途中経過は読める。"
}
try { Write-Utf8 $progressFile $final } catch { }
exit $code
