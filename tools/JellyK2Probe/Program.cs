using System;
using BlockField.SimCore.Fluid;

// jelly_2 K2: 復元トルクを上げたとき M-K2b（非対称で回頭する）がどこで落ちるか。
// 判定は追記7 A7.3 のとおり「片側の回頭が対照の 10 倍以上、かつ 1.0 度以上」。
// 落ちる係数が K4 の探索上限になるので、境界の位置そのものが成果物である。

static JellyParams P(float turn, float righting)
{
    var p = JellyParams.Default;
    p.JetModel = true;
    p.Pacemaker = false;      // 1セルしか叩かないので、それ自体が非対称になる
    p.TurnGain = turn;
    p.RightingGain = righting;
    return p;
}

// mode: 0 = 対称（0 と n/2）, 1 = 片側（0 のみ）
//
// 【位相に依らない統計量を返す】復元トルクと回転抗力は振動子を作るので、
// 「N ステップ時点の傾き」は振動の位相を拾うだけになる。実際に掃引すると
// 18.77 → 0.82 → 2.28 → 0.39 → 0.00 → 1.75 と非単調に振れた。
// 瞬時値を平均として読んだ件と同じ系統（3例目）。**後半の平均**で測る。
static float MeanTilt(JellyParams p, int mode, int steps)
{
    var j = new Jellyfish(p, 0f, 0f, 0f);
    int n = p.RingCells;
    int half = steps / 2;
    double sum = 0; int count = 0;
    for (int t = 0; t < steps; t++)
    {
        if (t % p.PulsePeriodTicks == 0)
        {
            j.StimulateCell(0);
            if (mode == 0) j.StimulateCell(n / 2);
        }
        j.Step(1f / 40f, 0f, 0f, 0f);
        if (t >= half) { sum += j.TiltDegrees; count++; }
    }
    return (float)(sum / count);
}

Console.WriteLine("復元トルクの掃引（旋回係数 = 1.0、800ステップ。後半400ステップの平均傾き）");
Console.WriteLine();
Console.WriteLine("  復元 | 対照(対称) | 片側     | 比      | M-K2b");
Console.WriteLine("  -----|-----------|----------|---------|------");

float[] rightings = { 0f, 0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f, 32f, 64f };
float lastPass = -1f, firstFail = -1f;
foreach (float r in rightings)
{
    float ctrl = MeanTilt(P(1.0f, r), 0, 800);
    float one = MeanTilt(P(1.0f, r), 1, 800);
    float ratio = ctrl > 1e-9f ? one / ctrl : float.PositiveInfinity;
    bool pass = one > Math.Max(ctrl * 10f, 1.0f);
    if (pass) lastPass = r; else if (firstFail < 0f) firstFail = r;
    Console.WriteLine($"  {r,5:F2} | {ctrl,9:F4} | {one,8:F4} | " +
        $"{(float.IsInfinity(ratio) ? "  ∞" : ratio.ToString("F1")),7} | {(pass ? "合格" : "**不合格**")}");
}
Console.WriteLine();
Console.WriteLine($"最後に合格した復元係数: {lastPass:F2}");
Console.WriteLine($"最初に落ちた復元係数  : {(firstFail < 0f ? "なし（全部合格）" : firstFail.ToString("F2"))}");

Console.WriteLine();
Console.WriteLine("旋回係数の掃引（復元 = 0.5 固定）");
Console.WriteLine("  旋回 | 対照(対称) | 片側     | M-K2b");
foreach (float g in new[] { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f })
{
    float ctrl = MeanTilt(P(g, 0.5f), 0, 800);
    float one = MeanTilt(P(g, 0.5f), 1, 800);
    bool pass = one > Math.Max(ctrl * 10f, 1.0f);
    Console.WriteLine($"  {g,5:F2} | {ctrl,9:F4} | {one,8:F4} | {(pass ? "合格" : "**不合格**")}");
}

// ---- 止水での実移動速度（実機で「動かない」と報告された件）----
Console.WriteLine();
Console.WriteLine("止水での実移動速度（過渡800を捨てて800ステップ、ペースメーカーON）");
Console.WriteLine("  モデル         | 実移動 m/s | 目標比");
foreach (bool jet in new[] { false, true })
{
    var q = JellyParams.Default;
    q.JetModel = jet;
    var j = new Jellyfish(q, 0f, 0f, 0f);
    for (int t = 0; t < 800; t++) j.Step(1f / 40f, 0f, 0f, 0f);
    float x0 = j.X, y0 = j.Y, z0 = j.Z;
    for (int t = 0; t < 800; t++) j.Step(1f / 40f, 0f, 0f, 0f);
    double d = Math.Sqrt((j.X - x0) * (j.X - x0) + (j.Y - y0) * (j.Y - y0) + (j.Z - z0) * (j.Z - z0));
    double v = d / (800.0 / 40.0);
    Console.WriteLine($"  {(jet ? "K2 噴流       " : "Phase C 2Dリム")} | {v,10:F6} | {v / q.SwimSpeed,6:F4}");
}
