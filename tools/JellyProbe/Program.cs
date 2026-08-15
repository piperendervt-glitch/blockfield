using System;
using System.Collections.Generic;
using System.Linq;
using BlockField.SimCore.Excitable;

// jelly_1 J2 の掃引・計測。
//
// 手順1: R₀=14 で g を掃引し、符号反転境界を測り直す（prereg 追記4 で解消済み）
// 手順3: M-J2c（48シード）と M-J2d（重ね合わせ）。新旧モデルの弁別を含む
//
// ここで測った値を期待値として EditMode テストへ固定する、という順序。

const int N = 16;

// ---------------------------------------------------------------------------
// 測定側のユーティリティ
// ---------------------------------------------------------------------------

// 到達方向の読み出しはここ（測定側）で行う。RingSwimmer には置かない
// ——「方向を計算するコードを持たない」を grep で確かめられる形に保つため。
static (double heading, double dist) HeadingOf(double x, double y)
{
    double deg = Math.Atan2(y, x) * 180.0 / Math.PI;
    if (deg < 0) deg += 360.0;
    return (deg, Math.Sqrt(x * x + y * y));
}

static double AngleError(double a, double b)
{
    double d = Math.Abs(a - b) % 360.0;
    return d > 180.0 ? 360.0 - d : d;
}

static string Verdict(double heading, double stimAngle)
{
    double toward = AngleError(heading, stimAngle);
    double away = AngleError(heading, (stimAngle + 180.0) % 360.0);
    if (toward < away - 1e-9) return "TOWARD";
    if (away < toward - 1e-9) return "AWAY";
    return "AMBIGUOUS";
}

// ---------------------------------------------------------------------------
// 本実装（波振幅モデル）: SimCore の RingSwimmer をそのまま使う
// ---------------------------------------------------------------------------

static (double heading, double dist) SwimWave(
    IReadOnlyList<(int cell, int tick)> stims, double g = 0.85, double drag = 0.1,
    int steps = 200, int r0 = 14, int? pace = null, int tPace = 40)
{
    var p = ExcitableParams.Default;
    p.RefractoryTicks = r0;
    p.Attenuation = g;

    var s = new RingSwimmer(N);
    for (int t = 0; t < steps; t++)
    {
        foreach (var (cell, tick) in stims)
        {
            if (tick == t) s.TryStimulate(cell, p);
        }
        if (pace.HasValue && t % tPace == 0) s.TryStimulate(pace.Value, p);
        s.Step(p, drag);
    }
    return HeadingOf(s.X, s.Y);
}

// ---------------------------------------------------------------------------
// 【比較専用】幾何距離モデル（旧版）
//
// **SimCore には入れない。** これは「波振幅モデルが正しい」ことを示すための
// 対照であって、本体で使う実装ではない。SimCore に置くと、後から誰かが
// 「こちらもある」と思って使ってしまう。prereg 修正4 のとおり、
// 振幅は波が運ぶ状態であって刺激からの幾何距離ではない。
//
// このモデルは刺激セルからのホップ数を毎回計算して g^hops を掛ける。
// (a) 距離を計算するコードが要る（創発の主張が無効になる）
// (b) 多重刺激で非物理な結果を出す（本ファイルの M-J2d が示す）
// 移植元は docs/prototypes/jelly/j2c_quantitative.py。
// ---------------------------------------------------------------------------

static (double heading, double dist) SwimGeometric(
    IReadOnlyList<(int cell, int tick)> stims, double g = 0.85, double drag = 0.1,
    int steps = 120, int r0 = 14)
{
    var p = ExcitableParams.Default;
    p.RefractoryTicks = r0;
    p.Attenuation = g;   // 場の側では使われるが、推力はこの下で上書きする

    var field = new ExcitableField(ExcitableGraphs.Ring(N));
    // 減衰の基準は「時刻が最も早い刺激」のセル（プロトタイプの origin = pend[0][0]）
    int origin = stims.OrderBy(s => s.tick).First().cell;

    double vx = 0, vy = 0, x = 0, y = 0;
    for (int t = 0; t < steps; t++)
    {
        foreach (var (cell, tick) in stims)
        {
            if (tick == t && field.Refractory(cell) == 0) field.Stimulate(cell, p);
        }
        field.Step(p);

        foreach (int i in field.LastFired)
        {
            // ここが問題の一行: 刺激セルからの幾何距離を計算している
            int hops = Math.Min(((i - origin) % N + N) % N, ((origin - i) % N + N) % N);
            double amp = Math.Pow(g, hops);
            double a = 2.0 * Math.PI * i / N;
            vx -= amp * Math.Cos(a);
            vy -= amp * Math.Sin(a);
        }
        vx *= (1.0 - drag); vy *= (1.0 - drag); x += vx; y += vy;
    }
    return HeadingOf(x, y);
}

// ---------------------------------------------------------------------------
// 手順1: g の符号反転境界（記録として残す）
// ---------------------------------------------------------------------------

Console.WriteLine("=== 手順1: R₀=14 での g の掃引（刺激 セル0 = 0°、drag=0.1、steps=200）===");
Console.WriteLine($"  {"g",7} {"heading",9} {"dist",9}  判定");
foreach (double g in new[] { 1.00, 0.98, 0.96, 0.95, 0.90, 0.85, 0.70 })
{
    var (h, d) = SwimWave(new[] { (0, 0) }, g);
    Console.WriteLine($"  {g,7:F2} {h,9:F1} {d,9:F2}  {Verdict(h, 0.0)}");
}
{
    string prevV = ""; double prev = 0;
    for (double g = 1.000; g >= 0.949; g -= 0.001)
    {
        double gg = Math.Round(g, 3);
        string v = Verdict(SwimWave(new[] { (0, 0) }, gg).heading, 0.0);
        if (prevV != "" && v != prevV)
        {
            Console.WriteLine($"  ★ 境界: g={prev:F3} が {prevV} / g={gg:F3} が {v}");
        }
        prevV = v; prev = gg;
    }
}

// ---------------------------------------------------------------------------
// 手順3-1: M-J2c — 48シードの定量判定
// ---------------------------------------------------------------------------

// プロトタイプ j2c_waveamp.py と同一の乱数列（LCG）。
// シードから 刺激セル / g / drag を作る。移植の照合対象なので式を変えない
static IEnumerable<double> RngStream(uint seed)
{
    uint s = seed;
    while (true)
    {
        unchecked { s = s * 1664525u + 1013904223u; }
        yield return s / 4294967296.0;
    }
}

Console.WriteLine();
Console.WriteLine("=== 手順3-1: M-J2c（48シード、g∈[0.75,0.92] / drag∈[0.05,0.2]、steps=120）===");
foreach (var (label, geometric) in new[] { ("波振幅モデル", false), ("幾何距離モデル", true) })
{
    var errs = new List<double>();
    double worstG = 0, worstDrag = 0; int worstCell = -1; double worst = -1;
    for (uint seed = 1000; seed < 1048; seed++)
    {
        var r = RngStream(seed).GetEnumerator();
        r.MoveNext(); int cell = (int)(r.Current * 16);
        r.MoveNext(); double g = 0.75 + r.Current * 0.17;
        r.MoveNext(); double drag = 0.05 + r.Current * 0.15;

        var stim = new[] { (cell, 0) };
        var (h, _) = geometric
            ? SwimGeometric(stim, g, drag, steps: 120)
            : SwimWave(stim, g, drag, steps: 120);
        double exp = (360.0 * cell / N + 180.0) % 360.0;
        double err = AngleError(h, exp);
        errs.Add(err);
        if (err > worst) { worst = err; worstCell = cell; worstG = g; worstDrag = drag; }
    }
    Console.WriteLine($"  {label,-14} 平均 {errs.Average():F3}° / 最大 {errs.Max():F3}°"
        + $"  → {(errs.Max() < 5 ? "合格" : "不合格")}");
    Console.WriteLine($"      最悪シード: cell={worstCell} g={worstG:F4} drag={worstDrag:F4} 誤差 {worst:F3}°");
}

// ---------------------------------------------------------------------------
// 手順3-2: M-J2d — 重ね合わせ。**steps を揃えて**新旧を弁別する
// ---------------------------------------------------------------------------

// ベクトル平均の予測は**計算で出す**（手で書くと、測定値をそのまま
// 予測値として書き写す事故が起きる。実際にやりかけた）。
// 単一刺激の逃避は「刺激角 + 180°」なので、同時刺激の予測は
// その単位ベクトルの和の向きになる。
static double VectorAveragePrediction(IEnumerable<int> stimCells)
{
    double sx = 0, sy = 0;
    foreach (int c in stimCells)
    {
        double escape = (360.0 * c / N + 180.0) * Math.PI / 180.0;
        sx += Math.Cos(escape);
        sy += Math.Sin(escape);
    }
    return HeadingOf(sx, sy).heading;
}

Console.WriteLine();
Console.WriteLine("=== 手順3-2: M-J2d（重ね合わせ）— steps を揃えた新旧比較 ===");
var cases = new (string label, (int, int)[] stims)[]
{
    ("同時 0°&90°",      new[] { (0, 0), (4, 0) }),
    ("同時 0°&90°&180°", new[] { (0, 0), (4, 0), (8, 0) }),
    ("0°@t0, 90°@t6",    new[] { (0, 0), (4, 6) }),
};
foreach (int steps in new[] { 120, 200, 400 })
{
    Console.WriteLine($"  --- steps={steps} ---");
    Console.WriteLine($"  {"ケース",-20} {"波振幅",16} {"幾何距離",16} {"予測",10} {"波振幅の誤差",8}");
    foreach (var (label, stims) in cases)
    {
        var w = SwimWave(stims, steps: steps);
        var geo = SwimGeometric(stims, steps: steps);
        bool simultaneous = stims.All(s => s.Item2 == 0);
        string pred = "        —", err = "      —";
        if (simultaneous)
        {
            double pv = VectorAveragePrediction(stims.Select(s => s.Item1));
            pred = $"{pv,8:F1}°";
            err = $"{AngleError(w.heading, pv),6:F1}°";
        }
        Console.WriteLine($"  {label,-20} {w.heading,8:F1}°/{w.dist,-6:F1} {geo.heading,8:F1}°/{geo.dist,-6:F1} {pred} {err}");
    }
}

// 大きさの重ね合わせも見る。方向が合っても大きさは線形に足されない
Console.WriteLine();
Console.WriteLine("  重ね合わせの線形性（steps=200、g=0.85）:");
{
    double one = SwimWave(new[] { (0, 0) }, steps: 200).dist;
    double two = SwimWave(new[] { (0, 0), (4, 0) }, steps: 200).dist;
    double three = SwimWave(new[] { (0, 0), (4, 0), (8, 0) }, steps: 200).dist;
    Console.WriteLine($"    刺激1つ {one,7:F3}");
    Console.WriteLine($"    刺激2つ {two,7:F3}  （線形なら {one * Math.Sqrt(2),7:F3}）");
    Console.WriteLine($"    刺激3つ {three,7:F3}  （線形なら {one,7:F3}）");
}

// ---------------------------------------------------------------------------
// 手順3-3: 時間差刺激における R₀ の効き
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== 手順3-3: 時間差刺激で R₀ は効くか（波振幅モデル、steps=200）===");
Console.WriteLine("  単一刺激では効かなかった（追記4-2）。時間差では対象セルが不応期中か否かを左右する");
Console.WriteLine($"  {"ケース",-20} {"R₀=4",18} {"R₀=14",18} 一致?");
foreach (var (label, stims) in cases)
{
    var a = SwimWave(stims, steps: 200, r0: 4);
    var b = SwimWave(stims, steps: 200, r0: 14);
    bool same = Math.Abs(a.heading - b.heading) < 1e-9 && Math.Abs(a.dist - b.dist) < 1e-9;
    Console.WriteLine($"  {label,-20} {a.heading,8:F1}°/{a.dist,-8:F3} {b.heading,8:F1}°/{b.dist,-8:F3} {(same ? "同一" : "**違う**")}");
}

// 2つ目の刺激が実際に入ったのかを直接見る。
// 「入らない」なら結果が R₀ に依らないのは当然であって、
// R₀ が無関係だという主張にはならない
Console.WriteLine();
Console.WriteLine("  2つ目の刺激（セル4）が入るか — 投入時刻を掃引して境界を出す:");
Console.WriteLine($"  {"t2",4} {"R₀=4 の R/入る?",22} {"R₀=14 の R/入る?",22} {"逃避方向 R₀=4 / R₀=14",24}");
foreach (int t2 in new[] { 4, 6, 8, 9, 10, 12, 15, 18, 19, 20 })
{
    var row = new List<string>();
    foreach (int r0 in new[] { 4, 14 })
    {
        var p = ExcitableParams.Default;
        p.RefractoryTicks = r0;
        var s = new RingSwimmer(N);
        bool landed = false; int refr = -1;
        for (int t = 0; t < 30; t++)
        {
            if (t == 0) s.TryStimulate(0, p);
            if (t == t2) { refr = s.Field.Refractory(4); landed = s.TryStimulate(4, p); }
            s.Step(p, 0.1);
        }
        row.Add($"R={refr,2} {(landed ? "入る  " : "入らない")}");
    }
    var a = SwimWave(new[] { (0, 0), (4, t2) }, steps: 200, r0: 4);
    var b = SwimWave(new[] { (0, 0), (4, t2) }, steps: 200, r0: 14);
    string mark = (Math.Abs(a.heading - b.heading) < 1e-9 && Math.Abs(a.dist - b.dist) < 1e-9)
        ? "" : "  ← R₀ で変わる";
    Console.WriteLine($"  {t2,4} {row[0],22} {row[1],22} {a.heading,7:F1}° / {b.heading,7:F1}°{mark}");
}
