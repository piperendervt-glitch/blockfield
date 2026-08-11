$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Numerics -ErrorAction SilentlyContinue

$json = Get-Content 'runs/sheep_pig_48s20k/summary.json' -Raw | ConvertFrom-Json
$rows = $json.speciesBySeed | Where-Object { $_.condition -eq 'default' }

$n = $rows.Count
$pigWins = 0; $sheepWins = 0; $ties = 0
$sheepAll = 0.0; $pigAll = 0.0
$ratios = @()

"seed      sheepMean   pigMean   pig/sheep  優勢"
foreach ($r in $rows) {
  $s = [double]$r.sheepMean
  $p = [double]$r.pigMean
  $sheepAll += $s; $pigAll += $p
  $ratio = if ($s -gt 0) { $p / $s } else { [double]::NaN }
  $ratios += $ratio
  $w = if ($p -gt $s) { $pigWins++; '豚' } elseif ($s -gt $p) { $sheepWins++; '羊' } else { $ties++; '同' }
  "{0,-9} {1,9:F4} {2,9:F4} {3,10:F4}   {4}" -f $r.seed, $s, $p, $ratio, $w
}

# --- 符号検定（p=0.5 の二項分布、両側）---
# 二項係数は long だと 48C24 = 1.6e13 まで伸び、PowerShell では
# silent に double 化されて精度を失う。BigInteger で厳密に出してから
# 最後に一度だけ double へ落とす
function Get-Binom([int]$n, [int]$k) {
  $r = [System.Numerics.BigInteger]::One
  for ($i = 0; $i -lt $k; $i++) {
    $r = $r * [System.Numerics.BigInteger]($n - $i) / [System.Numerics.BigInteger]($i + 1)
  }
  return $r
}

$k = [Math]::Min($pigWins, $n - $pigWins)
$tail = [System.Numerics.BigInteger]::Zero
for ($i = 0; $i -le $k; $i++) { $tail += (Get-Binom $n $i) }
$total = [System.Numerics.BigInteger]::Pow([System.Numerics.BigInteger]2, $n)
$oneSided = [double]$tail / [double]$total
$pValue = [Math]::Min(1.0, 2.0 * $oneSided)

# --- 比のばらつき ---
$ratioMean = ($ratios | Measure-Object -Average).Average
$sd = [Math]::Sqrt((($ratios | ForEach-Object { ($_ - $ratioMean) * ($_ - $ratioMean) } | Measure-Object -Sum).Sum) / ($n - 1))
$ratioMin = ($ratios | Measure-Object -Minimum).Minimum
$ratioMax = ($ratios | Measure-Object -Maximum).Maximum

# 差 (pig - sheep) の1標本 t 検定も出す。符号検定は大きさを捨てるため
$diffs = @(); for ($i = 0; $i -lt $n; $i++) { $diffs += ([double]$rows[$i].pigMean - [double]$rows[$i].sheepMean) }
$dMean = ($diffs | Measure-Object -Average).Average
$dSd = [Math]::Sqrt((($diffs | ForEach-Object { ($_ - $dMean) * ($_ - $dMean) } | Measure-Object -Sum).Sum) / ($n - 1))
$tStat = $dMean / ($dSd / [Math]::Sqrt($n))

""
"=== 48シード x 20000ティック（warmup 300 以降の時間平均）==="
"  豚が優勢なシード : {0} / {1}   (羊 {2}, 同数 {3})" -f $pigWins, $n, $sheepWins, $ties
"  プール比 豚/羊   : {0:F4}   (羊 {1:F4} / 豚 {2:F4})" -f ($pigAll / $sheepAll), ($sheepAll / $n), ($pigAll / $n)
"  シードごとの比   : 平均 {0:F4}  SD {1:F4}  範囲 {2:F4}〜{3:F4}" -f $ratioMean, $sd, $ratioMin, $ratioMax
"  差 (豚-羊)       : 平均 {0:+0.0000;-0.0000;0.0000}  SD {1:F4}  t({2}) = {3:F3}" -f $dMean, $dSd, ($n - 1), $tStat

# 「差が無い」ではなく「どこまでの差なら見えるか」を出す。
# 有意でない結果は、検出下限を添えないと解釈できない
$tCrit = 2.0117   # t(47), 両側 95%
$margin = $tCrit * $dSd / [Math]::Sqrt($n)
$sheepBase = $sheepAll / $n
$mdd = (1.96 + 0.8416) * $dSd / [Math]::Sqrt($n)   # 検出力80%の最小検出差
"  差の95%CI        : {0:+0.0000;-0.0000} 〜 {1:+0.0000;-0.0000} 頭  (羊平均比 {2:+0.0%;-0.0%} 〜 {3:+0.0%;-0.0%})" -f `
  ($dMean - $margin), ($dMean + $margin), (($dMean - $margin) / $sheepBase), (($dMean + $margin) / $sheepBase)
"  検出下限(power80%): {0:F4} 頭 = 比にして {1:P1}" -f $mdd, ($mdd / $sheepBase)
""
"  符号検定（帰無仮説 p=0.5、両側）: p = {0:F4}" -f $pValue
"  片側 P(X<={0}) = {1:F6}" -f $k, $oneSided
if ($pValue -lt 0.05) { "  → p < 0.05: 系統差あり" } else { "  → p >= 0.05: 中立浮動と区別できない" }
