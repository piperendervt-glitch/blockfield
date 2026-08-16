using System;
using System.Collections.Generic;
using System.Linq;
using BlockField.SimCore.Fluid;

// 系列2 Phase B の実機セッション（2026-08-16）で3症状が出たので、その診断。
//   1. 粒子が流れに乗って見えない（速すぎる / 移動距離が長い）
//   2. 家具の周りで回り込まない
//   3. 流れの中を移動する感覚が無い
//
// 実機ログには最大流速しか出ていない。分布・境界付近の挙動・粒子の運動学を測る。

// 実機と同じ格子（6.5cm、63x34x64）に、部屋らしい境界を入れる。
// 実機のメッシュは再現できないので、外周＋中央の塊＋床の段差で代用する
const float Cell = 0.065f;
var grid = new FlowGrid(63, 34, 64, Cell, 0f, 0f, 0f);
FlowBoundaryBaker.SealBorders(grid);

// 中央に家具相当の塊（およそ 0.8 x 0.5 x 0.6 m）
for (int z = 26; z < 36; z++)
    for (int y = 2; y < 10; y++)
        for (int x = 26; x < 38; x++)
            grid.SetSolid(x, y, z, true);
FlowBoundaryBaker.BakeDistance(grid);

var p = FlowParams.Default;
var field = new FlowField(grid, p);
field.RebuildAll();

// ---------------------------------------------------------------------------
// 0. ランプ幅の掃引（判定の閾値を決めるための実測。**先に測ってから決める**）
// ---------------------------------------------------------------------------

static (double normalMean, double normalP90, double nearFarRatio, double median)
    Diagnose(FlowGrid g, FlowParams pp)
{
    var f = new FlowField(g, pp);
    f.RebuildAll();

    var normals = new List<double>();
    var near = new List<double>();
    var far = new List<double>();
    var all = new List<double>();

    for (int z = 1; z < g.Depth - 1; z++)
        for (int y = 1; y < g.Height - 1; y++)
            for (int x = 1; x < g.Width - 1; x++)
            {
                int idx = g.Index(x, y, z);
                if (g.IsSolidAt(idx)) continue;
                f.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                double sp = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                all.Add(sp);

                int nx = 0, ny = 0, nz = 0, touching = 0;
                foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    if (!g.InRange(x + dx, y + dy, z + dz)) continue;
                    if (g.IsSolid(x + dx, y + dy, z + dz)) { nx -= dx; ny -= dy; nz -= dz; touching++; }
                }
                if (touching == 1 && sp > 1e-12)
                {
                    normals.Add(Math.Abs(vx * nx + vy * ny + vz * nz) / sp);
                    near.Add(sp);
                }
                if (g.DistanceInCells(idx) >= 6f) far.Add(sp);
            }

    normals.Sort(); all.Sort();
    return (normals.Average(), normals[(int)(normals.Count * 0.9)],
            near.Average() / far.Average(), all[all.Count / 2]);
}

// ---------------------------------------------------------------------------
// セルサイズ等価性の検証（2026-08-16 の3回目のセッション後）
//
// 実機で「セルサイズの差が分からない」となった。正規化でセルサイズと
// 流速・渦スケールの結合を切ったなら、**同じ流れ場を違う解像度で標本化して
// いるだけ**になっているはず。それを確かめる。
// ---------------------------------------------------------------------------

static (double p10, double p50, double p90, double reversal, int cells)
    Sample(float cellSize)
{
    // 同じ物理サイズの部屋・同じ位置の障害物にする（セル数ではなく m で決める）
    var g = FlowGrid.FromBounds(0f, 0f, 0f, 4.10f, 2.21f, 4.16f, cellSize);
    FlowBoundaryBaker.SealBorders(g);
    int Cx(float m) => (int)(m / cellSize);
    for (int z = Cx(1.6f); z <= Cx(2.2f); z++)
        for (int y = Cx(0.2f); y <= Cx(0.7f); y++)
            for (int x = Cx(1.3f); x <= Cx(2.1f); x++)
                if (g.InRange(x, y, z)) g.SetSolid(x, y, z, true);
    FlowBoundaryBaker.BakeDistance(g);

    var f = new FlowField(g, FlowParams.Default);
    f.RebuildAll();

    var speeds = new List<double>();
    for (int z = 1; z < g.Depth - 1; z++)
        for (int y = 1; y < g.Height - 1; y++)
            for (int x = 1; x < g.Width - 1; x++)
            {
                if (g.IsSolid(x, y, z)) continue;
                f.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                speeds.Add(Math.Sqrt(vx * vx + vy * vy + vz * vz));
            }
    speeds.Sort();

    // 渦のスケール: 速度の向きが反転するまでの距離（m）。セル数ではなく m で測る
    var lengths = new List<double>();
    for (float wz = 0.4f; wz < 3.8f; wz += 0.35f)
        for (float wy = 1.0f; wy < 2.0f; wy += 0.3f)
        {
            float bx = 0, by = 0, bz = 0; double run = 0;
            for (float wx = 0.2f; wx < 3.9f; wx += cellSize)
            {
                f.SampleVelocity(wx, wy, wz, out float vx, out float vy, out float vz);
                double n = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (n < 1e-12) { run = 0; continue; }
                vx = (float)(vx / n); vy = (float)(vy / n); vz = (float)(vz / n);
                if (run > 0 && vx * bx + vy * by + vz * bz < 0) { lengths.Add(run); run = 0; continue; }
                bx = vx; by = vy; bz = vz; run += cellSize;
            }
        }
    lengths.Sort();
    return (speeds[(int)(speeds.Count * 0.1)], speeds[speeds.Count / 2],
            speeds[(int)(speeds.Count * 0.9)],
            lengths.Count > 0 ? lengths[lengths.Count / 2] : double.NaN, g.CellCount);
}

Console.WriteLine("=== セルサイズ等価性（同じ部屋・同じ障害物・同じ目標流速 0.08 m/s）===");
Console.WriteLine($"  {"セル",7} {"セル数",9} {"p10",9} {"p50",9} {"p90",9} {"渦の反転距離",13}");
foreach (float c in new[] { 0.08f, 0.065f, 0.055f })
{
    var r = Sample(c);
    Console.WriteLine($"  {c * 100,5:F1}cm {r.cells,9} {r.p10,9:F4} {r.p50,9:F4} {r.p90,9:F4} {r.reversal * 100,10:F0} cm");
}
Console.WriteLine("  → 一致するなら「同じ流れ場を違う解像度で標本化しているだけ」");
Console.WriteLine();

Console.WriteLine("=== 0. 境界ランプ幅の掃引（壁面基準に修正後）===");
Console.WriteLine($"  {"ramp",6} {"|u·n|/|u| 平均",14} {"p90",8} {"境界/開け",10} {"中央流速",10}");
foreach (float ramp in new[] { 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 4.0f, 5.0f })
{
    var q = p; q.BoundaryRampCells = ramp;
    var r = Diagnose(grid, q);
    Console.WriteLine($"  {ramp,6:F1} {r.normalMean,14:F4} {r.normalP90,8:F4} {r.nearFarRatio,10:F3} {r.median,10:F4}");
}
Console.WriteLine("  （無相関なら 0.5、完全に接線なら 0）");
Console.WriteLine();

int solid = 0;
for (int i = 0; i < grid.CellCount; i++) if (grid.IsSolidAt(i)) solid++;

Console.WriteLine($"格子 {grid.Width}x{grid.Height}x{grid.Depth} = {grid.CellCount} セル / セル {Cell * 100:F1}cm");
Console.WriteLine($"固体 {solid} ({100.0 * solid / grid.CellCount:F1}%)");
Console.WriteLine();

// ---------------------------------------------------------------------------
// 1. 流速の分布（実機ログは最大値しか出していない）
// ---------------------------------------------------------------------------

var speeds = new List<double>();
var rampCells = 0;
var fluidCells = 0;
for (int z = 1; z < grid.Depth - 1; z++)
    for (int y = 1; y < grid.Height - 1; y++)
        for (int x = 1; x < grid.Width - 1; x++)
        {
            int idx = grid.Index(x, y, z);
            if (grid.IsSolidAt(idx)) continue;
            fluidCells++;
            if (grid.DistanceInCells(idx) < p.BoundaryRampCells) rampCells++;
            field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
            speeds.Add(Math.Sqrt(vx * vx + vy * vy + vz * vz));
        }
speeds.Sort();

double Pct(double q) => speeds[(int)Math.Clamp(q * (speeds.Count - 1), 0, speeds.Count - 1)];

Console.WriteLine("=== 1. 流速の分布（単位は ψ/m。**m/s ではない**）===");
Console.WriteLine($"  水セル {fluidCells}（うちランプ内 {rampCells} = {100.0 * rampCells / fluidCells:F1}%）");
Console.WriteLine($"  平均 {speeds.Average():F3}  中央 {Pct(0.5):F3}");
Console.WriteLine($"  p10 {Pct(0.1):F3}  p50 {Pct(0.5):F3}  p90 {Pct(0.9):F3}  p99 {Pct(0.99):F3}  最大 {speeds[^1]:F3}");

// ---------------------------------------------------------------------------
// 2. 粒子の運動学 — 実機で「流れて見えない」の正体
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== 2. 粒子の運動学（FlowParticleView の実装値で換算）===");
double roomSpan = grid.Width * Cell;
foreach (var (name, gain) in new[] { ("微粒子/粗い粒", 6.0), ("流線強調", 9.0) })
{
    double mean = speeds.Average() * gain;
    double max = speeds[^1] * gain;
    double p50 = Pct(0.5) * gain;
    Console.WriteLine($"  --- SpeedGain = {gain} ({name}) ---");
    Console.WriteLine($"    粒子速度  中央 {p50:F2} m/s  平均 {mean:F2} m/s  最大 {max:F2} m/s");
    Console.WriteLine($"    1フレーム移動 (72FPS)  中央 {p50 / 72 * 100:F1} cm  最大 {max / 72 * 100:F1} cm");
    Console.WriteLine($"    格子({roomSpan:F2}m)を横断する時間  中央 {roomSpan / p50:F2} 秒 = {roomSpan / p50 * 72:F1} フレーム");
}

Console.WriteLine();
Console.WriteLine("  参考: 水中の微粒子が漂う速さは 0.01〜0.05 m/s 程度");

// ---------------------------------------------------------------------------
// 3. 寿命で消えているのか、格子外へ出ているのか（瞬間移動の切り分け）
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== 3. 粒子が入れ替わる原因（寿命 1〜5秒 対 格子外への離脱）===");
foreach (double gain in new[] { 6.0, 9.0 })
{
    var rng = new Random(12345);
    int outCount = 0, lifeCount = 0;
    const int particles = 500;
    const int frames = 720;   // 10秒相当
    double dt = 1.0 / 72.0;
    for (int i = 0; i < particles; i++)
    {
        // 水セルから開始
        float px, py, pz;
        while (true)
        {
            int cx = rng.Next(grid.Width), cy = rng.Next(grid.Height), cz = rng.Next(grid.Depth);
            if (grid.IsSolid(cx, cy, cz)) continue;
            px = (float)((cx + rng.NextDouble()) * Cell);
            py = (float)((cy + rng.NextDouble()) * Cell);
            pz = (float)((cz + rng.NextDouble()) * Cell);
            break;
        }
        double life = 1.0 + rng.NextDouble() * 4.0;
        for (int f = 0; f < frames; f++)
        {
            field.SampleVelocity(px, py, pz, out float vx, out float vy, out float vz);
            px += (float)(vx * gain * dt); py += (float)(vy * gain * dt); pz += (float)(vz * gain * dt);
            life -= dt;
            bool outside = px < 0 || py < 0 || pz < 0
                || px > grid.Width * Cell || py > grid.Height * Cell || pz > grid.Depth * Cell;
            if (outside) { outCount++; break; }
            if (life <= 0) { lifeCount++; break; }
        }
    }
    Console.WriteLine($"  SpeedGain={gain}: 10秒以内に 格子外へ {outCount}/{particles} / 寿命切れ {lifeCount}/{particles}");
}

// ---------------------------------------------------------------------------
// 4. 境界のランプは効いているか（流れが壁に沿っているか）
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== 4. 境界のランプ（流れが壁に沿っているか）===");
{
    // 中央の塊の表面に隣接する水セルで、法線成分と接線成分を比べる
    var normalRatios = new List<double>();
    var nearSpeeds = new List<double>();
    var farSpeeds = new List<double>();

    for (int z = 20; z < 42; z++)
        for (int y = 1; y < 16; y++)
            for (int x = 20; x < 44; x++)
            {
                int idx = grid.Index(x, y, z);
                if (grid.IsSolidAt(idx)) continue;
                // 塊の面に接している水セルを拾い、その面法線を出す
                int nx = 0, ny = 0, nz = 0, touching = 0;
                foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                {
                    if (!grid.InRange(x + dx, y + dy, z + dz)) continue;
                    if (grid.IsSolid(x + dx, y + dy, z + dz)) { nx -= dx; ny -= dy; nz -= dz; touching++; }
                }
                if (touching != 1) continue;   // 角は法線が定まらないので除く

                field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (speed < 1e-9) continue;
                double normal = Math.Abs(vx * nx + vy * ny + vz * nz);
                normalRatios.Add(normal / speed);
                nearSpeeds.Add(speed);
            }

    // 開けた場所（塊からも壁からも離れた所）と比べる
    for (int z = 8; z < 56; z++)
        for (int y = 14; y < 30; y++)
            for (int x = 8; x < 55; x++)
            {
                int idx = grid.Index(x, y, z);
                if (grid.IsSolidAt(idx)) continue;
                if (grid.DistanceInCells(idx) < 6f) continue;
                field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                farSpeeds.Add(Math.Sqrt(vx * vx + vy * vy + vz * vz));
            }

    normalRatios.Sort();
    Console.WriteLine($"  塊の面に接する水セル {normalRatios.Count} 個");
    Console.WriteLine($"  |u·n| / |u|  平均 {normalRatios.Average():F4}  中央 {normalRatios[normalRatios.Count / 2]:F4}"
        + $"  p90 {normalRatios[(int)(normalRatios.Count * 0.9)]:F4}");
    Console.WriteLine("    → 0 に近いほど「壁に沿っている」。1 なら壁を向いている");
    Console.WriteLine($"  流速  境界付近 平均 {nearSpeeds.Average():F3} / 開けた所 平均 {farSpeeds.Average():F3}"
        + $"  （比 {nearSpeeds.Average() / farSpeeds.Average():F3}）");
}

// ---------------------------------------------------------------------------
// 5. 渦の大きさ — 「回り込み」が見える空間スケールか
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== 5. 流れの空間スケール（速度の向きが変わるまでの距離）===");
{
    // 中央の水平線に沿って、速度ベクトルの向きが 90 度変わるまでの距離を測る
    var lengths = new List<double>();
    for (int z = 10; z < 55; z += 7)
    {
        for (int y = 16; y < 30; y += 5)
        {
            float bx = 0, by = 0, bz = 0;
            int run = 0;
            for (int x = 2; x < grid.Width - 2; x++)
            {
                if (grid.IsSolid(x, y, z)) { run = 0; continue; }
                field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                double n = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                if (n < 1e-9) continue;
                vx = (float)(vx / n); vy = (float)(vy / n); vz = (float)(vz / n);
                if (run > 0)
                {
                    double dot = vx * bx + vy * by + vz * bz;
                    if (dot < 0) { lengths.Add(run * Cell); run = 0; continue; }
                }
                bx = vx; by = vy; bz = vz; run++;
            }
        }
    }
    lengths.Sort();
    if (lengths.Count > 0)
    {
        Console.WriteLine($"  向きが反転するまでの距離 中央 {lengths[lengths.Count / 2] * 100:F0} cm"
            + $" / p10 {lengths[(int)(lengths.Count * 0.1)] * 100:F0} cm"
            + $" / p90 {lengths[(int)(lengths.Count * 0.9)] * 100:F0} cm （{lengths.Count} 標本）");
        Console.WriteLine($"  指定した渦の直径 {p.EddySizeMeters * 100:F0} cm");
    }
}
