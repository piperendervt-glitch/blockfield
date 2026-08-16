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

// ---------------------------------------------------------------------------
// 参考: J3a の区間平均速度（閾値の妥当性を見るための素の実測）
// ---------------------------------------------------------------------------

static double[] IntervalSpeeds(int pace, int tPace, int r0, params (int a, int b)[] windows)
{
    var p = ExcitableParams.Default;
    p.RefractoryTicks = r0;
    var s = new RingSwimmer(N);
    int last = windows.Max(w => w.b);
    var xs = new double[last + 1];
    var ys = new double[last + 1];
    for (int t = 0; t < last; t++)
    {
        if (t % tPace == 0) s.TryStimulate(pace, p);
        s.Step(p, 0.1);
        xs[t + 1] = s.X; ys[t + 1] = s.Y;
    }
    return windows.Select(w =>
    {
        double dx = xs[w.b] - xs[w.a], dy = ys[w.b] - ys[w.a];
        return Math.Sqrt(dx * dx + dy * dy) / (w.b - w.a);
    }).ToArray();
}

Console.WriteLine();
Console.WriteLine("=== 参考: J3a 区間平均速度（pace=cell8 / T=40 / R₀=14 / drag=0.1）===");
{
    var wins = new[] { (200, 400), (400, 800), (800, 1600) };
    var v = IntervalSpeeds(8, 40, 14, wins);
    for (int i = 0; i < wins.Length; i++)
    {
        Console.WriteLine($"  区間 {wins[i].Item1,4}-{wins[i].Item2,-5} 平均速度 {v[i]:F6}");
    }
    double mean = v.Average();
    Console.WriteLine($"  相互の最大乖離: {v.Max(x => Math.Abs(x - mean) / mean) * 100:F3}%（平均基準）");
    Console.WriteLine($"  最大/最小の比 : {v.Max() / v.Min():F6}  → 乖離 {(v.Max() / v.Min() - 1) * 100:F3}%");

    // 追記1 の旧基準（累積距離比）も併記
    var cum = IntervalSpeeds(8, 40, 14, (0, 100), (0, 200), (0, 400));
    double d100 = cum[0] * 100, d200 = cum[1] * 200, d400 = cum[2] * 400;
    Console.WriteLine($"  参考（追記1 の旧基準・累積距離）: {d100:F1} / {d200:F1} / {d400:F1}");
    Console.WriteLine($"      線形からの乖離: t=200 {(d200 / (2 * d100) - 1) * 100:+0.0;-0.0}% "
        + $"/ t=400 {(d400 / (4 * d100) - 1) * 100:+0.0;-0.0}%");
}

// 48シード版。シードから ペースメーカー位置 / g / drag を作る（M-J2c と同じ乱数列）
Console.WriteLine();
Console.WriteLine("=== 参考: J3a 48シード（pace位置 + g/drag の揺らぎ、T=40 / R₀=14）===");
{
    var wins = new[] { (200, 400), (400, 800), (800, 1600) };
    double worst = 0; int worstSeed = -1;
    for (uint seed = 1000; seed < 1048; seed++)
    {
        var r = RngStream(seed).GetEnumerator();
        r.MoveNext(); int pace = (int)(r.Current * 16);
        r.MoveNext(); double g = 0.75 + r.Current * 0.17;
        r.MoveNext(); double drag = 0.05 + r.Current * 0.15;

        var p = ExcitableParams.Default;
        p.Attenuation = g;
        var s = new RingSwimmer(N);
        var xs = new double[1601]; var ys = new double[1601];
        for (int t = 0; t < 1600; t++)
        {
            if (t % 40 == 0) s.TryStimulate(pace, p);
            s.Step(p, drag);
            xs[t + 1] = s.X; ys[t + 1] = s.Y;
        }
        var v = wins.Select(w =>
            Math.Sqrt(Math.Pow(xs[w.Item2] - xs[w.Item1], 2)
                    + Math.Pow(ys[w.Item2] - ys[w.Item1], 2)) / (w.Item2 - w.Item1)).ToArray();
        double dev = (v.Max() / v.Min() - 1) * 100;
        if (dev > worst) { worst = dev; worstSeed = (int)seed; }
    }
    Console.WriteLine($"  48シードの最大乖離: {worst:F4}%（最悪 seed={worstSeed}）");
}

Console.WriteLine();
Console.WriteLine("  4方位の t=1600 移動距離:");
foreach (int cell in new[] { 0, 4, 8, 12 })
{
    var (h, d) = SwimWave(Array.Empty<(int, int)>(), steps: 1600, pace: cell, tPace: 40);
    Console.WriteLine($"    pace=cell{cell,2} → dist {d,8:F1}  heading {h,7:F1}°");
}

Console.WriteLine();
Console.WriteLine("  4方位の進行方向（steps=400）:");
foreach (int cell in new[] { 0, 4, 8, 12 })
{
    var (h, d) = SwimWave(Array.Empty<(int, int)>(), steps: 400, pace: cell, tPace: 40);
    double exp = (360.0 * cell / N + 180.0) % 360.0;
    Console.WriteLine($"    pace=cell{cell,2} → {h,7:F1}°（期待 {exp,5:F1}°、誤差 {AngleError(h, exp),5:F2}°）dist {d,7:F1}");
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
