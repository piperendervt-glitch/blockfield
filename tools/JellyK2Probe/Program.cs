using System;
using BlockField.SimCore.Fluid;
using System.Collections.Generic;
using BlockField.SimCore.Rng;

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

// ---- 沈降ONでの姿勢と高さの推移（M-K2k が落ちた理由）----
Console.WriteLine();
Console.WriteLine("既定パラメータ（噴流+沈降25%+復元0.5、ペースメーカーON）の推移");
Console.WriteLine("  拍動 | 傾き度 | 軸Y     | Y 位置  | 高さ変化/拍動");
{
    var q = JellyParams.Default;
    q.JetModel = true;
    var j = new Jellyfish(q, 0f, 0f, 0f);
    float prevY = 0f;
    for (int pulse = 0; pulse < 30; pulse++)
    {
        for (int t = 0; t < 40; t++) j.Step(1f / 40f, 0f, 0f, 0f);
        if (pulse % 3 == 0 || pulse == 29)
            Console.WriteLine($"  {pulse + 1,4} | {j.TiltDegrees,6:F1} | {j.Posture.AxisY,7:F3} | " +
                $"{j.Y,7:F3} | {j.Y - prevY,+8:F4}");
        prevY = j.Y;
    }
}

// ---- 沈降比の掃引: 「拍動＝沈まないための努力」が成立する比を探す ----
Console.WriteLine();
Console.WriteLine("沈降比の掃引（噴流+復元0.5、拍動ON。20拍動の正味の鉛直速度）");
Console.WriteLine("   比 | 沈降 m/s | 正味 m/s | 20拍動での高さ変化 | 判定");
foreach (float ratio in new[] { 0f, 0.25f, 0.5f, 0.75f, 0.9f, 1.0f, 1.1f, 1.25f, 1.5f })
{
    var q = JellyParams.Default;
    q.JetModel = true;
    q.SinkRatio = ratio;
    var j = new Jellyfish(q, 0f, 0f, 0f);
    for (int t = 0; t < 800; t++) j.Step(1f / 40f, 0f, 0f, 0f);   // 過渡
    float y0 = j.Y;
    for (int t = 0; t < 800; t++) j.Step(1f / 40f, 0f, 0f, 0f);
    float dy = j.Y - y0;
    float net = dy / 20f;
    string verdict = Math.Abs(net) < 0.005f ? "ほぼ静止（漂う）"
        : net > 0f ? "上昇" : "沈降";
    Console.WriteLine($"  {ratio,4:F2} | {q.SwimSpeed * ratio,8:F4} | {net,+8:F4} | " +
        $"{dy,+16:F3}m | {verdict}");
}

// ---- 刺激の強さと復元の差（実機で「復元の意味が分からない」件）----
Console.WriteLine();
Console.WriteLine("刺激セル数ごとの最大傾きと、復元の有無での戻り方");
Console.WriteLine("  セル数 | 復元0.0 最大 | 復元0.5 最大 | 復元2.0 最大 | 0.5 の10秒後 | 2.0 の10秒後");
foreach (int cells in new[] { 1, 3, 5 })
{
    var peaks = new float[3];
    var after = new float[3];
    float[] rg = { 0f, 0.5f, 2f };
    for (int r = 0; r < 3; r++)
    {
        var q = JellyParams.Default;
        q.JetModel = true;
        q.RightingGain = rg[r];
        var j = new Jellyfish(q, 0f, 0f, 0f);
        for (int t = 0; t < 400; t++) j.Step(1f / 40f, 0f, 0f, 0f);   // 定常へ
        // 側方に cells 個まとめて刺激
        int start = (q.PacemakerCell + q.RingCells / 4) % q.RingCells;
        for (int k = 0; k < cells; k++) j.StimulateCell((start + k) % q.RingCells);
        float peak = 0f;
        for (int t = 0; t < 400; t++) { j.Step(1f / 40f, 0f, 0f, 0f); peak = Math.Max(peak, j.TiltDegrees); }
        peaks[r] = peak;
        for (int t = 0; t < 400; t++) j.Step(1f / 40f, 0f, 0f, 0f);   // さらに10秒
        after[r] = j.TiltDegrees;
    }
    Console.WriteLine($"  {cells,6} | {peaks[0],12:F1} | {peaks[1],12:F1} | {peaks[2],12:F1} | " +
        $"{after[1],12:F1} | {after[2],12:F1}");
}

// ---- 実機で選ばれた設定（沈降1.10 + 復元0.5）の実態 ----
Console.WriteLine();
Console.WriteLine("実機で選ばれた設定の実態（噴流+ペースメーカーON、120拍動）");
Console.WriteLine("  沈降 | 復元 | 平均傾き | 最大傾き | 正味の鉛直 m/s | 拍動停止時の沈降 m/s");
foreach (var cfg in new[] { (1.10f, 0.5f), (0.90f, 0.5f), (1.10f, 0.0f) })
{
    var q = JellyParams.Default;
    q.JetModel = true; q.SinkRatio = cfg.Item1; q.RightingGain = cfg.Item2;
    var j = new Jellyfish(q, 0f, 0f, 0f);
    for (int t = 0; t < 800; t++) j.Step(1f / 40f, 0f, 0f, 0f);
    float y0 = j.Y; double tsum = 0; float tmax = 0; int n = 0;
    for (int t = 0; t < 4000; t++)
    {
        j.Step(1f / 40f, 0f, 0f, 0f);
        tsum += j.TiltDegrees; tmax = Math.Max(tmax, j.TiltDegrees); n++;
    }
    float net = (j.Y - y0) / 100f;

    var k = new Jellyfish(q, 0f, 0f, 0f);
    k.PacemakerEnabled = false;
    for (int t = 0; t < 400; t++) k.Step(1f / 40f, 0f, 0f, 0f);
    float sy = k.Y;
    for (int t = 0; t < 400; t++) k.Step(1f / 40f, 0f, 0f, 0f);
    float stopped = (sy - k.Y) / 10f;

    Console.WriteLine($"  {cfg.Item1,4:F2} | {cfg.Item2,4:F2} | {tsum / n,8:F2} | {tmax,8:F1} | " +
        $"{net,+14:F4} | {stopped,20:F4}");
}

// ---- 追記12 A12.3 の閾値の根拠: 比 1.5 が 1/3 の境界か ----
Console.WriteLine();
Console.WriteLine("閾値 1/3 の根拠（拍動中の沈降 ÷ 停止時の沈降）");
foreach (float r in new[] { 0.90f, 1.10f, 1.25f, 1.50f, 1.75f })
{
    var q = JellyParams.Default; q.JetModel = true; q.SinkRatio = r;
    float Rate(bool pulse)
    {
        var j = new Jellyfish(q, 0f, 0f, 0f); j.PacemakerEnabled = pulse;
        for (int t = 0; t < 800; t++) j.Step(1f / 40f, 0f, 0f, 0f);
        float y0 = j.Y;
        for (int t = 0; t < 800; t++) j.Step(1f / 40f, 0f, 0f, 0f);
        return Math.Max(0f, (y0 - j.Y) / 20f);
    }
    float st = Rate(false), pu = Rate(true);
    Console.WriteLine($"  比 {r:F2}: 停止 {st:F4} 拍動 {pu:F4} → {pu / st:P1}" +
        (pu <= st / 3f ? "  合格" : "  不合格"));
}

// ================= M-K3e: 対称16セル発火は推力を増やすか =================
// 対照 = ペースメーカーのみ。処理 = ペースメーカー + 周期Tごとに全16セル。
// 位相は環境が決めるのでこちらでは選べない。4点で掃引して範囲を報告する。
Console.WriteLine();
Console.WriteLine("M-K3e 対称16セル発火の推力への効果（対照 = ペースメーカーのみ）");
{
    var p = JellyParams.Default; p.JetModel = true;   // 噴流・ペースメーカーON・沈降1.10
    int T = p.PulsePeriodTicks;
    const int Warm = 800, Meas = 1600;

    (float speed, float net, long pulses) Run(int offset)
    {
        var j = new Jellyfish(p, 0f, 0f, 0f);
        for (int t = 0; t < Warm; t++)
        {
            if (offset >= 0 && t % T == offset)
                for (int i = 0; i < p.RingCells; i++) j.StimulateCell(i);
            j.Step(1f / 40f, 0f, 0f, 0f);
        }
        float px = j.X, py = j.Y - j.SinkPathY, pz = j.Z, y0 = j.Y;
        long p0 = j.PulseCount;
        double path = 0;
        for (int t = 0; t < Meas; t++)
        {
            if (offset >= 0 && (Warm + t) % T == offset)
                for (int i = 0; i < p.RingCells; i++) j.StimulateCell(i);
            j.Step(1f / 40f, 0f, 0f, 0f);
            float cy = j.Y - j.SinkPathY;
            double dx = j.X - px, dy = cy - py, dz = j.Z - pz;
            path += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            px = j.X; py = cy; pz = j.Z;
        }
        return ((float)(path / (Meas / 40.0)), (j.Y - y0) / (Meas / 40f), j.PulseCount - p0);
    }

    var ctrl = Run(-1);
    Console.WriteLine($"  対照（刺激なし）      : 遊泳 {ctrl.speed:F5} m/s  正味鉛直 {ctrl.net:+0.0000;-0.0000} m/s  拍動 {ctrl.pulses}");
    Console.WriteLine($"  周期 T = {T} ティック。位相オフセット 0, T/4, T/2, 3T/4 = 0, {T / 4}, {T / 2}, {3 * T / 4}");
    foreach (int off in new[] { 0, T / 4, T / 2, 3 * T / 4 })
    {
        var r = Run(off);
        float ratio = r.speed / ctrl.speed - 1f;
        Console.WriteLine($"  位相 {off,2}            : 遊泳 {r.speed:F5} m/s  正味鉛直 {r.net:+0.0000;-0.0000} m/s  拍動 {r.pulses}" +
            $"   対照比 {ratio:+0.0%;-0.0%}");
    }
}

// 位相を全点掃く（4点では「効かない位相」がどれだけあるか分からない）
Console.WriteLine();
Console.WriteLine("M-K3e 位相の全掃引（T=40 の全オフセット）");
{
    var p = JellyParams.Default; p.JetModel = true;
    int T = p.PulsePeriodTicks;
    float Speed(int offset)
    {
        var j = new Jellyfish(p, 0f, 0f, 0f);
        for (int t = 0; t < 800; t++)
        {
            if (offset >= 0 && t % T == offset)
                for (int i = 0; i < p.RingCells; i++) j.StimulateCell(i);
            j.Step(1f / 40f, 0f, 0f, 0f);
        }
        float px = j.X, py = j.Y - j.SinkPathY, pz = j.Z; double path = 0;
        for (int t = 0; t < 1600; t++)
        {
            if (offset >= 0 && (800 + t) % T == offset)
                for (int i = 0; i < p.RingCells; i++) j.StimulateCell(i);
            j.Step(1f / 40f, 0f, 0f, 0f);
            float cy = j.Y - j.SinkPathY;
            double dx = j.X - px, dy = cy - py, dz = j.Z - pz;
            path += Math.Sqrt(dx * dx + dy * dy + dz * dz);
            px = j.X; py = cy; pz = j.Z;
        }
        return (float)(path / 40.0);
    }
    float c = Speed(-1);
    int none = 0, gain = 0;
    var sb = new System.Text.StringBuilder();
    for (int off = 0; off < T; off++)
    {
        float r = Speed(off) / c - 1f;
        if (Math.Abs(r) < 0.10f) none++; else if (r > 0) gain++;
        sb.Append($"{off}:{r:+0%;-0%} ");
        if (off % 10 == 9) { Console.WriteLine("  " + sb); sb.Clear(); }
    }
    Console.WriteLine($"  → 効果なし(±10%内) {none}/{T} 位相、増加 {gain}/{T} 位相、対照 {c:F5} m/s");
}

// ================= K3 の不合格の切り分け =================
Console.WriteLine();
Console.WriteLine("K3 診断: 部屋の中での軌跡（シード40、沈降0）");
{
    FlowGrid MakeRoom()
    {
        const int W = 26; const float C = 0.08f;
        var g = new FlowGrid(W, W, W, C, 0f, 0f, 0f);
        for (int x = 0; x < W; x++) for (int y = 0; y < W; y++) for (int z = 0; z < W; z++)
            if (x == 0 || y == 0 || z == 0 || x == W - 1 || y == W - 1 || z == W - 1)
                g.SetSolid(x, y, z, true);
        FlowBoundaryBaker.BakeDistance(g);
        return g;
    }
    float WallDist(FlowGrid g, float x, float y, float z)
    {
        int gx = (int)Math.Floor((x - g.OriginX) / g.CellSize);
        int gy = (int)Math.Floor((y - g.OriginY) / g.CellSize);
        int gz = (int)Math.Floor((z - g.OriginZ) / g.CellSize);
        if (!g.InRange(gx, gy, gz)) return 0f;
        return Math.Max(0f, g.DistanceInCells(g.Index(gx, gy, gz)) - 0.5f) * g.CellSize;
    }

    var g = MakeRoom();
    foreach (bool noci in new[] { true, false })
    {
        var p = JellyParams.Default; p.JetModel = true; p.Nociception = noci; p.SinkRatio = 0f;
        var rng = new Mulberry32(40);
        float margin = g.CellSize * 2f + p.BellDiameter;
        float span = 26 * g.CellSize - 2f * margin;
        float sx = margin + rng.NextFloat01() * span;
        float sy = margin + rng.NextFloat01() * span;
        float sz = margin + rng.NextFloat01() * span;
        var j = new Jellyfish(p, sx, sy, sz, g);
        float ox = (rng.NextFloat01() - 0.5f) * 4f, oz = (rng.NextFloat01() - 0.5f) * 4f;
        for (int k = 0; k < 20; k++) j.NudgeForTest(ox, 0f, oz, 1f / 40f);

        Console.WriteLine($"  --- 侵害受容 {(noci ? "ON " : "OFF")} 初期 ({sx:F2},{sy:F2},{sz:F2}) ---");
        float px = j.X, py = j.Y, pz = j.Z; double sp = 0;
        for (int t = 0; t < 4000; t++)
        {
            j.Step(1f / 40f, 0f, 0f, 0f);
            float dx = j.X - px, dy = j.Y - py, dz = j.Z - pz;
            sp += Math.Sqrt(dx * dx + dy * dy + dz * dz) * 40.0;
            px = j.X; py = j.Y; pz = j.Z;
            if (t % 500 == 499)
                Console.WriteLine($"    t={t + 1,4} 位置({j.X:F2},{j.Y:F2},{j.Z:F2}) 壁距離{WallDist(g, j.X, j.Y, j.Z):F3}m " +
                    $"接触{j.NociceptedCells,2}/16 侵害{j.NociceptionCount,4}回 傾き{j.TiltDegrees,5:F1}° 実移動{sp / (t + 1):F4}m/s");
        }
    }
}

Console.WriteLine();
Console.WriteLine("K3 診断2: 沈降1.10（床へ向かう世界）で侵害受容が効くか");
{
    const int W = 26; const float C = 0.08f;
    var g = new FlowGrid(W, W, W, C, 0f, 0f, 0f);
    for (int x = 0; x < W; x++) for (int y = 0; y < W; y++) for (int z = 0; z < W; z++)
        if (x == 0 || y == 0 || z == 0 || x == W - 1 || y == W - 1 || z == W - 1)
            g.SetSolid(x, y, z, true);
    FlowBoundaryBaker.BakeDistance(g);

    foreach (bool noci in new[] { true, false })
    {
        var p = JellyParams.Default; p.JetModel = true; p.Nociception = noci; p.SinkRatio = 1.10f;
        var j = new Jellyfish(p, 1.04f, 1.20f, 1.04f, g);
        Console.WriteLine($"  --- 侵害受容 {(noci ? "ON " : "OFF")} ---");
        float px = j.X, py = j.Y, pz = j.Z; double sp = 0; int n = 0;
        for (int t = 0; t < 6000; t++)
        {
            j.Step(1f / 40f, 0f, 0f, 0f);
            if (t >= 3000)
            {
                float dx = j.X - px, dy = j.Y - py, dz = j.Z - pz;
                sp += Math.Sqrt(dx * dx + dy * dy + dz * dz) * 40.0; n++;
            }
            px = j.X; py = j.Y; pz = j.Z;
            if (t % 1000 == 999)
                Console.WriteLine($"    t={t + 1,4} 高さ{j.Y - 0.08f:F3}m 接触{j.NociceptedCells,2}/16 " +
                    $"侵害{j.NociceptionCount,4}回 傾き{j.TiltDegrees,5:F1}°" +
                    (n > 0 ? $" 後半の実移動{sp / n:F4}m/s" : ""));
        }
    }
}

Console.WriteLine();
Console.WriteLine("K3 診断3: 床の近くから始める（沈降1.10）");
{
    const int W = 26; const float C = 0.08f;
    var g = new FlowGrid(W, W, W, C, 0f, 0f, 0f);
    for (int x = 0; x < W; x++) for (int y = 0; y < W; y++) for (int z = 0; z < W; z++)
        if (x == 0 || y == 0 || z == 0 || x == W - 1 || y == W - 1 || z == W - 1)
            g.SetSolid(x, y, z, true);
    FlowBoundaryBaker.BakeDistance(g);

    foreach (bool noci in new[] { true, false })
    {
        var p = JellyParams.Default; p.JetModel = true; p.Nociception = noci; p.SinkRatio = 1.10f;
        var j = new Jellyfish(p, 1.04f, 0.30f, 1.04f, g);
        Console.WriteLine($"  --- 侵害受容 {(noci ? "ON " : "OFF")} ---");
        for (int t = 0; t < 8000; t++)
        {
            j.Step(1f / 40f, 0f, 0f, 0f);
            if (t % 500 == 499)
                Console.WriteLine($"    t={t + 1,4} 高さ{j.Y - 0.08f:F3}m 接触{j.NociceptedCells,2}/16 " +
                    $"侵害{j.NociceptionCount,4}回 傾き{j.TiltDegrees,5:F1}°");
        }
    }
}

Console.WriteLine();
Console.WriteLine("K3 診断4: 48シードのうち何個が着底するか（沈降1.10、床から0.30m、8000ティック）");
{
    const int W = 26; const float C = 0.08f;
    var g = new FlowGrid(W, W, W, C, 0f, 0f, 0f);
    for (int x = 0; x < W; x++) for (int y = 0; y < W; y++) for (int z = 0; z < W; z++)
        if (x == 0 || y == 0 || z == 0 || x == W - 1 || y == W - 1 || z == W - 1)
            g.SetSolid(x, y, z, true);
    FlowBoundaryBaker.BakeDistance(g);

    int settled = 0; var bad = new List<string>();
    for (uint s = 1; s <= 48; s++)
    {
        var p = JellyParams.Default; p.JetModel = true; p.Nociception = true; p.SinkRatio = 1.10f;
        var rng = new Mulberry32(s);
        float margin = C * 2f + p.BellDiameter;
        float span = W * C - 2f * margin;
        float sx = margin + rng.NextFloat01() * span;
        rng.NextFloat01();
        float sz = margin + rng.NextFloat01() * span;
        var j = new Jellyfish(p, sx, C + 0.30f, sz, g);
        float ox = (rng.NextFloat01() - 0.5f) * 4f, oz = (rng.NextFloat01() - 0.5f) * 4f;
        for (int k = 0; k < 20; k++) j.NudgeForTest(ox, 0f, oz, 1f / 40f);

        long entryStep = -1; double h = 0; int n = 0;
        for (int t = 0; t < 8000; t++)
        {
            j.Step(1f / 40f, 0f, 0f, 0f);
            if (entryStep < 0 && j.NociceptedCells > 0) entryStep = t;
            if (t >= 4000) { h += j.Y - C; n++; }
        }
        float mean = (float)(h / n);
        int phase = entryStep < 0 ? -1 : (int)(entryStep % p.PulsePeriodTicks);
        bool dead = phase >= 7 && phase <= 14;
        if (mean < p.BellDiameter * 0.5f)
        {
            settled++;
            bad.Add($"    シード{s,2}: 高さ{mean:F4}m 侵入位相{phase,3}{(dead ? " ★死角" : "")} 侵害{j.NociceptionCount}回 傾き{j.TiltDegrees:F1}°");
        }
    }
    Console.WriteLine($"  着底したシード: {settled}/48");
    foreach (var b in bad) Console.WriteLine(b);
}
