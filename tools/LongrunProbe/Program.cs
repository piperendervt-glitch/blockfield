using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;

// longrun (10万ティック × 5シード) を毎ティックの分解能で追う使い捨てプローブ。
//
// 出したいのは3つ:
//   1. 草食ギルドが 0 になったティックと、回復までの長さ
//      （SimRunner の系列は1ランあたり2,000点に間引かれるため、10万ティックでは
//        50ティック間隔になり、それより短い一過性の事象が落ちる。
//        実際 longrun で guildExtinct=1/5 が出たのに系列上の最小値はどのシードも
//        2 だった。「50ティック未満のゼロ」かどうかをここで確かめる）
//   2. 死の場が定常に達するティック数（チェックポイントは2,000間隔なので
//      立ち上がりが見えない）
//   3. 場と個体数の長期ドリフト
//
// 個体数は毎ティック、場の平均は50ティックごとに測る
// （場の平均は全セル走査なので毎ティックでは重すぎる）。
//
// 使い方: dotnet run -c Release --project tools\LongrunProbe -- [ticks] [seeds]

int ticks = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 100000;
int seedCount = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;

var seeds = new uint[seedCount];
for (int i = 0; i < seedCount; i++) seeds[i] = 1000u + (uint)i * 7919u;

Console.WriteLine($"プローブ: {seedCount} シード × {ticks} ティック（個体数は毎ティック、場は50ティックごと）");
var sw = System.Diagnostics.Stopwatch.StartNew();

var results = new SeedProbe[seedCount];
Parallel.For(0, seedCount, i => results[i] = Probe.RunOne(seeds[i], ticks));

Console.WriteLine($"完了 {sw.Elapsed.TotalSeconds:F1} 秒");

// ---- 1. ゼロ個体の区間 ----
Console.WriteLine();
Console.WriteLine($"=== 草食ギルドが 0 になった区間（warmup {Probe.WarmupTicks} ティック以降）===");
int totalZeroRuns = 0;
foreach (var r in results)
{
    if (r.ZeroRuns.Count == 0)
    {
        Console.WriteLine($"  seed {r.Seed,-6} なし（期間中の最小 {r.MinHerbivores}）");
        continue;
    }
    totalZeroRuns += r.ZeroRuns.Count;
    Console.WriteLine($"  seed {r.Seed,-6} {r.ZeroRuns.Count} 区間（期間中の最小 {r.MinHerbivores}）");
    foreach (var (start, len) in r.ZeroRuns)
    {
        Console.WriteLine($"      tick {start,7} から {len,4} ティック連続でゼロ");
    }
}
Console.WriteLine($"  → 合計 {totalZeroRuns} 区間 / {seedCount} シード");

Console.WriteLine();
Console.WriteLine("=== 狼が 0 になった区間 ===");
foreach (var r in results)
{
    Console.WriteLine(r.WolfZeroRuns.Count == 0
        ? $"  seed {r.Seed,-6} なし（最小 {r.MinWolves}）"
        : $"  seed {r.Seed,-6} {r.WolfZeroRuns.Count} 区間、最小 {r.MinWolves}、最長 {Probe.MaxLen(r.WolfZeroRuns)} ティック");
}

Console.WriteLine();
Console.WriteLine("=== 草（植生場の総量）の最小値 ===");
foreach (var r in results)
{
    Console.WriteLine($"  seed {r.Seed,-6} 最小 {r.MinVegetation,7:F2}（適性セル {r.SuitableCells} あたり {r.MinVegetation / r.SuitableCells:F4}）");
}

// ---- 2. 死の場の立ち上がり ----
double deathSteady = 0;
{
    int from = ticks / 2;
    double sum = 0; int n = 0;
    foreach (var r in results)
        foreach (var (tick, value) in r.DeathTrace)
            if (tick >= from) { sum += value; n++; }
    deathSteady = n > 0 ? sum / n : 0;
}

double trampleSteady = Steady(r => r.TrampleTrace);
double vegSteady = Steady(r => r.VegTrace);

double Steady(Func<SeedProbe, List<(long tick, double value)>> pick)
{
    int from = ticks / 2;
    double sum = 0; int n = 0;
    foreach (var r in results)
        foreach (var (tick, value) in pick(r))
            if (tick >= from) { sum += value; n++; }
    return n > 0 ? sum / n : 0;
}

Console.WriteLine();
Console.WriteLine("=== 場の立ち上がり（5シードの平均、tick 0 から）===");
Console.WriteLine($"  {"tick",7}  {"死の場",10} {"定常比",7}  {"踏み荒らし",10} {"定常比",7}  {"草の総量",10} {"定常比",7}");
int[] marks = { 50, 100, 200, 300, 500, 750, 1000, 1500, 2000, 3000, 5000, 10000, 20000, 50000, 100000 };
foreach (int m in marks)
{
    if (m > ticks) break;
    double? d = Avg(r => r.DeathTrace, m);
    double? tr = Avg(r => r.TrampleTrace, m);
    double? v = Avg(r => r.VegTrace, m);
    if (!d.HasValue) continue;
    Console.WriteLine($"  {m,7}  {d.Value,10:F6} {d.Value / deathSteady,7:F3}  " +
                      $"{tr!.Value,10:F5} {tr.Value / trampleSteady,7:F3}  " +
                      $"{v!.Value,10:F1} {v.Value / vegSteady,7:F3}");
}
Console.WriteLine($"  定常値（後半 {ticks / 2} ティック以降）: 死={deathSteady:F6} 踏={trampleSteady:F5} 草={vegSteady:F1}");

double? Avg(Func<SeedProbe, List<(long tick, double value)>> pick, long m)
{
    double sum = 0; int n = 0;
    foreach (var r in results)
    {
        double? v = Probe.At(pick(r), m);
        if (v.HasValue) { sum += v.Value; n++; }
    }
    return n > 0 ? sum / n : null;
}

// ブロック平均でトレンドを見る。
//
// 【瞬時値の「±10%帯に入った時刻」を整定時間にしてはいけない】
// 定常であっても系列は揺らぎ続けるので、帯を出入りし続ける。
// 「最後に帯を出たティック」を取ると、定常な系でも必ず終端付近の値になり、
// 整定していないように見える（最初にこれをやって無意味な表を作った）。
// 揺らぎとトレンドを分けるには、区間平均を並べて比べる。
Console.WriteLine();
Console.WriteLine($"=== ブロック平均（{ticks / 10} ティックごと、5シードの平均）===");
Console.WriteLine($"  {"区間",-18} {"死の場",10} {"踏み荒らし",12} {"草の総量",10} {"草食獣",8} {"狼",8}");
int block = ticks / 10;
for (int b = 0; b < 10; b++)
{
    long lo = (long)b * block, hi = lo + block;
    Console.WriteLine($"  {lo,8}-{hi,-9} {BlockAvg(r => r.DeathTrace, lo, hi),10:F6} " +
                      $"{BlockAvg(r => r.TrampleTrace, lo, hi),12:F5} " +
                      $"{BlockAvg(r => r.VegTrace, lo, hi),10:F1} " +
                      $"{BlockAvg(r => r.HerbTrace, lo, hi),8:F2} " +
                      $"{BlockAvg(r => r.WolfTrace, lo, hi),8:F2}");
}

double BlockAvg(Func<SeedProbe, List<(long tick, double value)>> pick, long lo, long hi)
{
    double sum = 0; int n = 0;
    foreach (var r in results)
        foreach (var (tick, value) in pick(r))
            if (tick >= lo && tick < hi) { sum += value; n++; }
    return n > 0 ? sum / n : double.NaN;
}

// ---- 3. 長期ドリフト ----
Console.WriteLine();
Console.WriteLine("=== 長期ドリフト（前半 vs 後半、warmup 後）===");
Console.WriteLine($"  {"指標",-16} {"前半平均",12} {"後半平均",12} {"変化率",9}  {"シード間SD(後半)",16}");
DriftRow("草食獣", r => r.HerbFirst, r => r.HerbSecond);
DriftRow("狼", r => r.WolfFirst, r => r.WolfSecond);
DriftRow("植生場の総量", r => r.VegFirst, r => r.VegSecond);
DriftRow("死の場平均", r => r.DeathFirst, r => r.DeathSecond);
DriftRow("踏み荒らし平均", r => r.TrampleFirst, r => r.TrampleSecond);
DriftRow("恐怖の場平均", r => r.FearFirst, r => r.FearSecond);

void DriftRow(string name, Func<SeedProbe, double> a, Func<SeedProbe, double> b)
{
    double sa = 0, sb = 0;
    foreach (var r in results) { sa += a(r); sb += b(r); }
    sa /= results.Length; sb /= results.Length;
    double var2 = 0;
    foreach (var r in results) { double d = b(r) - sb; var2 += d * d; }
    double sd = Math.Sqrt(var2 / results.Length);
    double pct = sa != 0 ? (sb - sa) / sa * 100.0 : 0;
    Console.WriteLine($"  {name,-16} {sa,12:F4} {sb,12:F4} {pct,8:+0.0;-0.0;0.0}%  {sd,16:F4}");
}

static class Probe
{
    public const int Size = 50;

    /// <summary>Runner.WarmupTicks と揃える。</summary>
    public const int WarmupTicks = 300;

    /// <summary>場の平均を測る間隔。全セル走査なので毎ティックにはしない。</summary>
    public const int FieldSampleInterval = 50;

    public static SeedProbe RunOne(uint seed, int ticks)
    {
        var tp = TerrainParams.Default;
        tp.seed = seed;
        tp.width = Size;
        tp.depth = Size;
        tp.maxHeight = 16;
        var world = World.Create(tp);
        var p = SimParams.Default;

        var r = new SeedProbe
        {
            Seed = seed,
            SuitableCells = world.SuitableCellCount,
            MinHerbivores = int.MaxValue,
            MinWolves = int.MaxValue,
            MinVegetation = double.MaxValue,
        };

        var death = (ScalarField)world.Fields["death"];
        var trample = (ScalarField)world.Fields["trample"];
        var fear = (ScalarField)world.Fields["fear"];

        long zeroStart = -1; int zeroLen = 0;
        long wZeroStart = -1; int wZeroLen = 0;
        int half = ticks / 2;
        double h1 = 0, h2 = 0, w1 = 0, w2 = 0, v1 = 0, v2 = 0;
        double d1 = 0, d2 = 0, t1 = 0, t2 = 0, f1 = 0, f2 = 0;
        long n1 = 0, n2 = 0, m1 = 0, m2 = 0;

        for (int t = 0; t < ticks; t++)
        {
            Simulation.Tick(world, world.Rng, p);

            // 場のサンプリングは warmup の前から行う。立ち上がりそのものが
            // 見たい対象なので、ここを warmup で切ると 0 からの上昇が観測できない
            // （最初にこれをやって、tick 350 の値が既に定常の1.66倍という
            //   「立ち上がりを飛ばした表」を作ってしまった）
            bool fieldSample = world.TickCount % FieldSampleInterval == 0;
            double dm = 0, tm = 0, fm = 0;
            if (fieldSample)
            {
                dm = EcologyStats.FieldStats(death).mean;
                tm = EcologyStats.FieldStats(trample).mean;
                fm = EcologyStats.FieldStats(fear).mean;
                r.DeathTrace.Add((world.TickCount, dm));
                r.TrampleTrace.Add((world.TickCount, tm));
                r.VegTrace.Add((world.TickCount, world.VegetationTotal));
                r.HerbTrace.Add((world.TickCount, world.SheepCount + world.PigCount));
                r.WolfTrace.Add((world.TickCount, world.WolfCount));
            }

            if (t < WarmupTicks) continue;

            int herb = world.SheepCount + world.PigCount;
            int wolf = world.WolfCount;
            double veg = world.VegetationTotal;

            if (herb < r.MinHerbivores) r.MinHerbivores = herb;
            if (wolf < r.MinWolves) r.MinWolves = wolf;
            if (veg < r.MinVegetation) r.MinVegetation = veg;

            // ゼロの連続区間を記録する。一過性のゼロと本当の全滅を区別するため、
            // 「0だった」ではなく「何ティック連続で0だったか」を持つ
            if (herb == 0) { if (zeroStart < 0) { zeroStart = world.TickCount; zeroLen = 0; } zeroLen++; }
            else if (zeroStart >= 0) { r.ZeroRuns.Add((zeroStart, zeroLen)); zeroStart = -1; }

            if (wolf == 0) { if (wZeroStart < 0) { wZeroStart = world.TickCount; wZeroLen = 0; } wZeroLen++; }
            else if (wZeroStart >= 0) { r.WolfZeroRuns.Add((wZeroStart, wZeroLen)); wZeroStart = -1; }

            bool second = t >= half;
            if (second) { h2 += herb; w2 += wolf; v2 += veg; n2++; }
            else { h1 += herb; w1 += wolf; v1 += veg; n1++; }

            if (fieldSample)
            {
                if (second) { d2 += dm; t2 += tm; f2 += fm; m2++; }
                else { d1 += dm; t1 += tm; f1 += fm; m1++; }
            }
        }
        if (zeroStart >= 0) r.ZeroRuns.Add((zeroStart, zeroLen));
        if (wZeroStart >= 0) r.WolfZeroRuns.Add((wZeroStart, wZeroLen));

        if (n1 > 0) { r.HerbFirst = h1 / n1; r.WolfFirst = w1 / n1; r.VegFirst = v1 / n1; }
        if (n2 > 0) { r.HerbSecond = h2 / n2; r.WolfSecond = w2 / n2; r.VegSecond = v2 / n2; }
        if (m1 > 0) { r.DeathFirst = d1 / m1; r.TrampleFirst = t1 / m1; r.FearFirst = f1 / m1; }
        if (m2 > 0) { r.DeathSecond = d2 / m2; r.TrampleSecond = t2 / m2; r.FearSecond = f2 / m2; }
        return r;
    }

    public static int MaxLen(List<(long start, int len)> runs)
    {
        int m = 0;
        foreach (var (_, len) in runs) if (len > m) m = len;
        return m;
    }

    public static double? At(List<(long tick, double value)> trace, long tick)
    {
        foreach (var (t, v) in trace) if (t >= tick) return v;
        return null;
    }
}

sealed class SeedProbe
{
    public uint Seed;
    public int SuitableCells;
    public int MinHerbivores, MinWolves;
    public double MinVegetation;
    public List<(long start, int len)> ZeroRuns = new();
    public List<(long start, int len)> WolfZeroRuns = new();
    public List<(long tick, double value)> DeathTrace = new();
    public List<(long tick, double value)> TrampleTrace = new();
    public List<(long tick, double value)> VegTrace = new();
    public List<(long tick, double value)> HerbTrace = new();
    public List<(long tick, double value)> WolfTrace = new();
    public double HerbFirst, HerbSecond, WolfFirst, WolfSecond, VegFirst, VegSecond;
    public double DeathFirst, DeathSecond, TrampleFirst, TrampleSecond, FearFirst, FearSecond;
}
