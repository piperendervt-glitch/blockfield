using System;
using System.Collections.Generic;
using System.Globalization;
using BlockField.SimCore.Excitable;

// jelly_1 J2 の掃引・計測。
//
// 手順1（本ファイルの主目的）: R₀=14 で g を掃引し、逃避方向が
// 刺激方向(TOWARD)から反対方向(AWAY)へ反転する境界を測り直す。
// プロトタイプの実測（g=1.00 で TOWARD、g=0.95 で AWAY）は r0=4 の
// 下で得られたもので、R₀=14 で同じ位置に来る保証がない（prereg §7.3-3）。

const int N = 16;
const int Steps = 200;
const double Drag = 0.1;

// 角度の読み出しはここ（測定側）で行う。RingSwimmer には置かない
// ——「方向を計算するコードを持たない」を grep で確かめられる形に保つため。
static (double heading, double dist) Measure(RingSwimmer s)
{
    double deg = Math.Atan2(s.Y, s.X) * 180.0 / Math.PI;
    if (deg < 0) deg += 360.0;
    return (deg, Math.Sqrt(s.X * s.X + s.Y * s.Y));
}

static (double heading, double dist) Swim(
    IEnumerable<(int cell, int tick)> stims, double g, double drag = Drag,
    int steps = Steps, int r0 = 14, int? pace = null, int tPace = 40)
{
    var p = ExcitableParams.Default;
    p.RefractoryTicks = r0;
    p.Attenuation = g;

    var s = new RingSwimmer(N);
    var list = new List<(int cell, int tick)>(stims);

    for (int t = 0; t < steps; t++)
    {
        foreach (var (cell, tick) in list)
        {
            if (tick == t) s.TryStimulate(cell, p);
        }
        if (pace.HasValue && t % tPace == 0) s.TryStimulate(pace.Value, p);
        s.Step(p, drag);
    }
    return Measure(s);
}

static double AngleError(double a, double b)
{
    double d = Math.Abs(a - b) % 360.0;
    return d > 180.0 ? 360.0 - d : d;
}

static string Verdict(double heading, double stimAngle)
{
    // 刺激の向きに近いか、その正反対に近いかを分類するだけ。
    // 模型はこの判定を見ていない（測定側の言葉づかい）
    double toward = AngleError(heading, stimAngle);
    double away = AngleError(heading, (stimAngle + 180.0) % 360.0);
    if (toward < away - 1e-9) return "TOWARD";
    if (away < toward - 1e-9) return "AWAY";
    return "AMBIGUOUS";
}

Console.WriteLine("=== 手順1: R₀=14 での g の掃引（刺激 セル0 = 0°、drag=0.1、200ステップ）===");
Console.WriteLine($"  {"g",6} {"heading",9} {"dist",9}  判定");
double[] coarse = { 1.00, 0.99, 0.98, 0.97, 0.96, 0.95, 0.94, 0.92, 0.90, 0.88, 0.85, 0.80, 0.75, 0.70 };
foreach (double g in coarse)
{
    var (h, d) = Swim(new[] { (0, 0) }, g);
    Console.WriteLine($"  {g,6:F2} {h,9:F1} {d,9:F2}  {Verdict(h, 0.0)}");
}

Console.WriteLine();
Console.WriteLine("=== 境界の細分（0.001 刻み）===");
double prev = double.NaN; string prevV = "";
for (double g = 1.000; g >= 0.949; g -= 0.001)
{
    double gg = Math.Round(g, 3);
    var (h, d) = Swim(new[] { (0, 0) }, gg);
    string v = Verdict(h, 0.0);
    if (prevV != "" && v != prevV)
    {
        Console.WriteLine($"  ★ 境界: g={prev:F3} が {prevV} / g={gg:F3} が {v}");
    }
    prevV = v; prev = gg;
}
Console.WriteLine("  （上で境界が出なければ、掃引範囲に反転が無い）");

// プロトタイプ j2_attenuation.py は steps=80 / r0=4 で走っている。
// 移植の一致はまず**同条件**で確かめる（条件を揃えずに数字が違うのは当たり前）。
Console.WriteLine();
Console.WriteLine("=== プロトタイプとの照合（j2_attenuation.py と同条件: r0=4 / steps=80）===");
Console.WriteLine("  プロトタイプ実測: g=1.00 → 0.0/8.99 / 0.95 → 180.0/0.93 / 0.92 → 180.0/5.29");
Console.WriteLine("                    0.90 → 180.0/7.64 / 0.88 → 180.0/9.61 / 0.85 → 180.0/11.92");
Console.WriteLine($"  {"g",6} {"heading",9} {"dist",9}  判定");
foreach (double g in new[] { 1.00, 0.95, 0.92, 0.90, 0.88, 0.85, 0.70 })
{
    var (h, d) = Swim(new[] { (0, 0) }, g, r0: 4, steps: 80);
    Console.WriteLine($"  {g,6:F2} {h,9:F1} {d,9:F2}  {Verdict(h, 0.0)}");
}

Console.WriteLine();
Console.WriteLine("=== R₀ を 4 → 14 に変えると何が動くか（steps=80 で固定）===");
Console.WriteLine($"  {"g",6} {"r0=4 head/dist",18} {"R₀=14 head/dist",18}");
foreach (double g in new[] { 1.00, 0.96, 0.955, 0.95, 0.85 })
{
    var a = Swim(new[] { (0, 0) }, g, r0: 4, steps: 80);
    var b = Swim(new[] { (0, 0) }, g, r0: 14, steps: 80);
    Console.WriteLine($"  {g,6:F3} {a.heading,8:F1}/{a.dist,-9:F3} {b.heading,8:F1}/{b.dist,-9:F3}");
}

// 8方位で刺激して、逃避が常に正反対を向くか（M-J2b の本体）
Console.WriteLine();
Console.WriteLine("=== M-J2b: 16方位の刺激に対する逃避方向（R₀=14 / g=0.85 / steps=200）===");
Console.WriteLine($"  {"stim",6} {"heading",9} {"expected",9} {"err",7} {"dist",8}");
double maxErr = 0;
for (int c = 0; c < N; c++)
{
    double sa = 360.0 * c / N;
    var (h, dd) = Swim(new[] { (c, 0) }, 0.85);
    double exp = (sa + 180.0) % 360.0;
    double err = AngleError(h, exp);
    if (err > maxErr) maxErr = err;
    Console.WriteLine($"  {sa,6:F1} {h,9:F1} {exp,9:F1} {err,7:F2} {dd,8:F2}");
}
Console.WriteLine($"  → 最大誤差 {maxErr:F2}°");
