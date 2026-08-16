using System;
using System.Collections.Generic;
using System.Linq;
using BlockField.SimCore.Fluid;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// 系列2 Phase B: ψ から作る流れ場。
    ///
    /// Phase B は判定のない表現作業だが、**決定論と UnityEngine 非依存の規約は維持する**
    /// （後で判定を乗せる土台になるため）。ここで固定するのはその2点と、
    /// ψ を使う理由そのもの（非圧縮性が構造的に保証される）である。
    /// </summary>
    public class FlowFieldTests
    {
        /// <summary>実測の部屋バウンズ 3.19 × 2.07 × 2.60 m を模した格子。</summary>
        static FlowGrid MakeRoomGrid(float cellSize = 0.13f)
        {
            var grid = FlowGrid.FromBounds(0f, 0f, 0f, 3.19f, 2.07f, 2.60f, cellSize);
            FlowBoundaryBaker.SealBorders(grid);
            FlowBoundaryBaker.BakeDistance(grid);
            return grid;
        }

        static readonly (int dx, int dy, int dz)[] k_Faces =
        {
            (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
        };

        /// <summary>部屋 + 中央に家具相当の塊。境界の判定に使う。</summary>
        static (FlowGrid grid, FlowField field) MakeRoomWithObstacle()
        {
            var grid = FlowGrid.FromBounds(0f, 0f, 0f, 3.19f, 2.07f, 2.60f, 0.065f);
            FlowBoundaryBaker.SealBorders(grid);
            for (int z = 16; z <= 25; z++)
                for (int y = 3; y <= 11; y++)
                    for (int x = 20; x <= 31; x++)
                        grid.SetSolid(x, y, z, true);
            FlowBoundaryBaker.BakeDistance(grid);

            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();
            return (grid, field);
        }

        static float MedianSpeed(FlowGrid grid, FlowField field)
        {
            var speeds = new List<float>();
            for (int z = 1; z < grid.Depth - 1; z++)
                for (int y = 1; y < grid.Height - 1; y++)
                    for (int x = 1; x < grid.Width - 1; x++)
                    {
                        if (grid.IsSolid(x, y, z)) continue;
                        field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                        speeds.Add((float)Math.Sqrt(vx * vx + vy * vy + vz * vz));
                    }
            speeds.Sort();
            return speeds[speeds.Count / 2];
        }

        // ================= 格子の形 =================

        /// <summary>
        /// **立方体にしない。** セルサイズを一様に固定し、軸ごとのセル数を
        /// 部屋の実測から決める。N³ を被せると高さ方向のセルが大量に余る。
        /// </summary>
        [Test]
        public void Grid_UsesUniformCellSizeAndPerAxisCounts()
        {
            var grid = FlowGrid.FromBounds(0f, 0f, 0f, 3.19f, 2.07f, 2.60f, 0.065f);

            Assert.AreEqual(50, grid.Width, "3.19m / 6.5cm を切り上げ");
            Assert.AreEqual(32, grid.Height, "2.07m / 6.5cm を切り上げ");
            Assert.AreEqual(40, grid.Depth, "2.60m / 6.5cm を切り上げ");
            Assert.AreEqual(0.065f, grid.CellSize, 1e-6f);

            // 立方体を被せた場合との比較（余りがどれだけ出るか）。
            // 最長辺 3.19m の立方体なら 50³ = 125,000 セル。部屋なりなら 64,000 で、
            // **約 51%**。高さ 2.07m に 50 セル分の高さを与えるのが無駄になる
            Assert.AreEqual(64000, grid.CellCount);
            int cubic = 50 * 50 * 50;
            Assert.Less(grid.CellCount / (double)cubic, 0.55,
                "立方体を被せるより大幅に少ないセル数で済むはず");
        }

        [Test]
        public void Grid_RejectsDegenerateShapes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new FlowGrid(0, 4, 4, 0.1f, 0f, 0f, 0f));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new FlowGrid(4, 4, 4, 0f, 0f, 0f, 0f));
        }

        // ================= 境界の焼き込み =================

        /// <summary>
        /// **距離場は整数チャンファーで作る。** 固体セルは 0、離れるほど大きくなり、
        /// 255（= 7.97 セル）で飽和する。
        ///
        /// 量子化単位は 1/32 セル。ψ のランプ（d₀ = 2〜3 セル）に使うだけなので
        /// この粒度で足りる。整数で持つのは、リプレイ経路から浮動小数点の
        /// 幾何演算を構造的に排除するため。
        /// </summary>
        [Test]
        public void Distance_IsZeroOnSolidAndGrowsAwayFromIt()
        {
            var grid = new FlowGrid(16, 16, 16, 0.1f, 0f, 0f, 0f);
            grid.SetSolid(8, 8, 8, true);
            FlowBoundaryBaker.BakeDistance(grid);

            Assert.AreEqual(0, grid.Distance(8, 8, 8), "固体セルの距離は 0");
            Assert.AreEqual(FlowGrid.DistanceUnitsPerCell, grid.Distance(9, 8, 8),
                "軸方向に1セル離れたら 1 セル ぶん");
            Assert.AreEqual(45, grid.Distance(9, 9, 8), "斜め（辺）は √2 ≈ 1.414 セル");
            Assert.AreEqual(55, grid.Distance(9, 9, 9), "斜め（角）は √3 ≈ 1.732 セル");

            // 遠くは飽和する
            Assert.AreEqual(FlowGrid.MaxQuantizedDistance, grid.Distance(0, 0, 0));

            // 実数読み出しはセル単位
            Assert.AreEqual(1.0f, grid.DistanceInCells(grid.Index(9, 8, 8)), 1e-6f);
        }

        /// <summary>
        /// 距離場が対称であること。掃引の順序（前方→後方）に依存しない。
        /// 依存していると、格子の切り方で流れが変わってしまう。
        /// </summary>
        [Test]
        public void Distance_IsSymmetricAroundAnIsolatedSolidCell()
        {
            var grid = new FlowGrid(15, 15, 15, 0.1f, 0f, 0f, 0f);
            grid.SetSolid(7, 7, 7, true);
            FlowBoundaryBaker.BakeDistance(grid);

            for (int d = 1; d <= 4; d++)
            {
                byte plus = grid.Distance(7 + d, 7, 7);
                Assert.AreEqual(plus, grid.Distance(7 - d, 7, 7), $"x 方向 ±{d}");
                Assert.AreEqual(plus, grid.Distance(7, 7 + d, 7), $"y 方向 +{d}");
                Assert.AreEqual(plus, grid.Distance(7, 7, 7 - d), $"z 方向 −{d}");
            }
        }

        /// <summary>
        /// 三角形からの焼き込み。床を張ると、その高さのセルだけが固体になる。
        /// 頂点はアンカーローカル座標 (m)。
        /// </summary>
        [Test]
        public void BakeSolid_FillsCellsTouchedByTriangles()
        {
            var grid = new FlowGrid(10, 10, 10, 0.1f, 0f, 0f, 0f);
            // y = 0.35 の高さに 1m 四方の床（2三角形）
            var verts = new[]
            {
                0.05f, 0.35f, 0.05f,
                0.95f, 0.35f, 0.05f,
                0.05f, 0.35f, 0.95f,
                0.95f, 0.35f, 0.95f,
            };
            var tris = new[] { 0, 1, 2, 1, 3, 2 };

            int filled = FlowBoundaryBaker.BakeSolid(grid, verts, tris);
            Assert.Greater(filled, 0, "1セルも埋まっていない");

            Assert.IsTrue(grid.IsSolid(5, 3, 5), "床の高さ（y=0.35 → セル3）は固体");
            Assert.IsFalse(grid.IsSolid(5, 5, 5), "その上は水");
            Assert.IsFalse(grid.IsSolid(5, 1, 5), "その下も水");

            // 面に穴が開いていないこと（刻みがセルの半分以下なので開かないはず）
            for (int x = 1; x <= 8; x++)
            {
                for (int z = 1; z <= 8; z++)
                {
                    Assert.IsTrue(grid.IsSolid(x, 3, z), $"({x},3,{z}) に穴が開いている");
                }
            }
        }

        [Test]
        public void BakeSolid_IgnoresTrianglesOutsideTheGrid()
        {
            var grid = new FlowGrid(8, 8, 8, 0.1f, 0f, 0f, 0f);
            var verts = new[] { 5f, 5f, 5f, 6f, 5f, 5f, 5f, 5f, 6f };
            Assert.AreEqual(0, FlowBoundaryBaker.BakeSolid(grid, verts, new[] { 0, 1, 2 }));
        }

        // ================= ψ を使う理由: 非圧縮性 =================

        /// <summary>
        /// **u = ∇×ψ なので発散は構造的にゼロ。**
        ///
        /// これが ψ を使う理由そのものである。圧力ポアソン方程式を解かずに
        /// 非圧縮性が保証されるので、コストは格子1枚分の差分だけで済む。
        /// 実装が回転の形になっていなければここが落ちる。
        /// </summary>
        [Test]
        public void Curl_ProducesADivergenceFreeField()
        {
            var grid = MakeRoomGrid();
            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();

            double worst = 0;
            int sampled = 0;
            for (int z = 2; z < grid.Depth - 2; z++)
            {
                for (int y = 2; y < grid.Height - 2; y++)
                {
                    for (int x = 2; x < grid.Width - 2; x++)
                    {
                        if (grid.IsSolid(x, y, z)) continue;
                        worst = Math.Max(worst, Math.Abs(field.DivergenceAt(x, y, z)));
                        sampled++;
                    }
                }
            }

            Assert.Greater(sampled, 100, "評価したセルが少なすぎる");
            // 中心差分の回転を中心差分で発散させると、丸め誤差だけが残る
            Assert.Less(worst, 1e-3, $"発散の最大値 {worst:E3} — 回転になっていない疑い");
        }

        /// <summary>
        /// **流れが壁を貫かないこと。** 境界のランプで ψ が壁際で 0 になるので、
        /// 固体セルの流速は 0 になり、その隣も壁に沿う向きになる。
        /// 家具のポリゴンが水中の岩として働くのはこの性質による。
        /// </summary>
        [Test]
        public void Boundary_FlowVanishesInsideSolidCells()
        {
            var grid = MakeRoomGrid();
            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();

            for (int z = 0; z < grid.Depth; z++)
            {
                for (int y = 0; y < grid.Height; y++)
                {
                    for (int x = 0; x < grid.Width; x++)
                    {
                        if (!grid.IsSolid(x, y, z)) continue;
                        field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                        Assert.AreEqual(0f, vx, 1e-9f);
                        Assert.AreEqual(0f, vy, 1e-9f);
                        Assert.AreEqual(0f, vz, 1e-9f);
                    }
                }
            }
        }

        /// <summary>
        /// 塊の中は静止し、外側には流れがあること。
        ///
        /// 【名前を実態に合わせた】以前は `Boundary_FlowGoesAroundAnObstacleInTheMiddle`
        /// という名前だったが、**測っているのは「外に流れがある」だけ**で
        /// 「回り込んでいる」は一切見ていなかった。名前が主張と一致しないテストは、
        /// 通ることで安心を生むぶん有害である（2026-08-16 の実機で回り込みが見えず、
        /// |u·n|/|u| = 0.227 だったのに本テストは通っていた）。
        ///
        /// 回り込みそのものは <see cref="Boundary_FlowIsTangentialAtObstacleFaces"/> が見る。
        /// **本テストも必要である** — 流れが全部ゼロなら接線性の判定は自明に通るので、
        /// 「流れが存在する」を別に固定しておかなければならない。
        /// </summary>
        [Test]
        public void Boundary_FlowExistsOutsideTheObstacleAndIsZeroInside()
        {
            var grid = FlowGrid.FromBounds(0f, 0f, 0f, 2f, 2f, 2f, 0.1f);
            FlowBoundaryBaker.SealBorders(grid);
            for (int z = 8; z <= 11; z++)
                for (int y = 8; y <= 11; y++)
                    for (int x = 8; x <= 11; x++)
                        grid.SetSolid(x, y, z, true);
            FlowBoundaryBaker.BakeDistance(grid);

            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();

            field.VelocityAt(9, 9, 9, out float ix, out float iy, out float iz);
            Assert.AreEqual(0f, ix * ix + iy * iy + iz * iz, 1e-12f, "塊の中は静止");

            double outside = 0;
            for (int y = 8; y <= 11; y++)
            {
                for (int z = 8; z <= 11; z++)
                {
                    field.VelocityAt(13, y, z, out float vx, out float vy, out float vz);
                    outside = Math.Max(outside, Math.Sqrt(vx * vx + vy * vy + vz * vz));
                }
            }
            Assert.Greater(outside, 0f, "塊の外側に流れが無い");
        }

        /// <summary>
        /// **流れが障害物の面に沿っていること（回り込み）。**
        ///
        /// 面に接する水セルで、速度の法線成分の割合 |u·n|/|u| を測る。
        /// 0 なら完全に接線、1 なら壁を向いている。**向きが無相関なら 0.5**。
        ///
        /// 【なぜ 0 にならないか】障害物はボクセル化された階段状の面なので、
        /// ここで使う軸平行の面法線と、ランプが見ている滑らかな面の法線が一致しない。
        /// 角の近くのセルほどずれる。
        ///
        /// 実測（2026-08-16 の修正後）: 平均 0.14 前後。修正前は 0.227 で、
        /// ランプが**セル中心基準**だったため壁に接する最初の水セルに
        /// ψ が 0.35 も残っていた（smoothstep(1.0/2.5)）。
        /// </summary>
        [Test]
        public void Boundary_FlowIsTangentialAtObstacleFaces()
        {
            var (grid, field) = MakeRoomWithObstacle();

            var ratios = new List<double>();
            for (int z = 1; z < grid.Depth - 1; z++)
            {
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    for (int x = 1; x < grid.Width - 1; x++)
                    {
                        if (grid.IsSolid(x, y, z)) continue;

                        // 固体に接している面が1つだけのセルを拾う（角は法線が定まらない）
                        int nx = 0, ny = 0, nz = 0, touching = 0;
                        foreach (var (dx, dy, dz) in k_Faces)
                        {
                            if (!grid.InRange(x + dx, y + dy, z + dz)) continue;
                            if (!grid.IsSolid(x + dx, y + dy, z + dz)) continue;
                            nx -= dx; ny -= dy; nz -= dz; touching++;
                        }
                        if (touching != 1) continue;

                        field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                        double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                        if (speed < 1e-12) continue;
                        ratios.Add(Math.Abs(vx * nx + vy * ny + vz * nz) / speed);
                    }
                }
            }

            Assert.Greater(ratios.Count, 200, "面に接する水セルの標本が少なすぎる");
            double mean = ratios.Average();
            Assert.Less(mean, 0.20,
                $"|u·n|/|u| の平均が {mean:F4}。無相関なら 0.5、接線なら 0。" +
                "境界ランプが壁面基準になっていない疑い");
        }

        /// <summary>
        /// **境界の近くでは流れが弱まること。**
        ///
        /// ランプが ψ を落としているなら、壁際の流速は開けた所より小さくなる。
        /// 修正前は比 0.988 で**まったく落ちていなかった**。修正後は 0.67 前後。
        ///
        /// ただし落としすぎると壁際の流れが死んで「回り込み」が見えなくなる。
        /// ランプ幅 2.5 セルはその折り合いで選んだ（幅 5 にすると比 0.21 まで落ちる）。
        /// </summary>
        [Test]
        public void Boundary_FlowIsSlowerNearWallsThanInTheOpen()
        {
            var (grid, field) = MakeRoomWithObstacle();

            var near = new List<double>();
            var far = new List<double>();
            for (int z = 1; z < grid.Depth - 1; z++)
            {
                for (int y = 1; y < grid.Height - 1; y++)
                {
                    for (int x = 1; x < grid.Width - 1; x++)
                    {
                        int idx = grid.Index(x, y, z);
                        if (grid.IsSolidAt(idx)) continue;
                        field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                        double speed = Math.Sqrt(vx * vx + vy * vy + vz * vz);
                        double d = grid.DistanceInCells(idx);
                        if (d <= 1.01) near.Add(speed);
                        else if (d >= 6.0) far.Add(speed);
                    }
                }
            }

            Assert.Greater(near.Count, 100, "壁際の標本が少なすぎる");
            Assert.Greater(far.Count, 100, "開けた所の標本が少なすぎる");
            double ratio = near.Average() / far.Average();
            Assert.Less(ratio, 0.85,
                $"壁際 / 開けた所 の流速比が {ratio:F3}。ランプが効いていない疑い");
            Assert.Greater(ratio, 0.05,
                $"流速比が {ratio:F3}。落としすぎると壁際の流れが死に、回り込みが見えなくなる");
        }

        // ================= 目標流速への正規化 =================

        /// <summary>
        /// **水セルの流速の中央値が目標流速に一致すること。**
        ///
        /// 2026-08-16 の実機セッションで、粒子が毎フレーム 20cm 飛んで
        /// 流れに見えなかった。ψ の振幅に物理スケールを与えておらず、
        /// 流速が ∇×ψ の生の値（中央 2.4、単位は ψ/m）のままだったため。
        /// </summary>
        [Test]
        public void Speed_MedianMatchesTheTargetSpeed()
        {
            foreach (float target in new[] { 0.02f, 0.08f, 0.3f })
            {
                var p = FlowParams.Default;
                p.TargetSpeed = target;
                var grid = MakeRoomGrid();
                var field = new FlowField(grid, p);
                field.RebuildAll();

                Assert.AreEqual(target, MedianSpeed(grid, field), target * 1e-3f,
                    $"目標 {target} m/s に対して中央値が合っていない");
            }
        }

        /// <summary>
        /// **セルサイズを変えても流速が変わらないこと。**
        ///
        /// 修正前は ∇×ψ を 1/(2×セルサイズ) で割った生の値をそのまま使っていたので、
        /// **流速がセルサイズに反比例していた**（実機ログ: 6.5cm で 7.72、
        /// 5.5cm で 9.39、比 1.22 ≈ セルサイズ比 1.18）。
        /// セルサイズの比較が「解像度の比較」になっていなかった。
        /// </summary>
        [Test]
        public void Speed_IsIndependentOfCellSize()
        {
            var p = FlowParams.Default;
            foreach (float cell in new[] { 0.08f, 0.065f, 0.055f })
            {
                var grid = FlowGrid.FromBounds(0f, 0f, 0f, 3.19f, 2.07f, 2.60f, cell);
                FlowBoundaryBaker.SealBorders(grid);
                FlowBoundaryBaker.BakeDistance(grid);
                var field = new FlowField(grid, p);
                field.RebuildAll();

                Assert.AreEqual(p.TargetSpeed, MedianSpeed(grid, field), p.TargetSpeed * 1e-3f,
                    $"セル {cell * 100f:F1}cm で流速が目標から外れた");
            }
        }

        /// <summary>
        /// **渦の大きさはメートルで決まること。**
        /// ノイズ座標をセル添字ではなくワールド座標で取っているので、
        /// セルサイズを変えても流れの空間構造が変わらない。
        /// 渦を大きくすると生の ∇×ψ は小さくなり、目標へ合わせる係数は大きくなる。
        /// </summary>
        [Test]
        public void Flow_EddyScaleIsSetInMetresNotCells()
        {
            var grid = MakeRoomGrid();

            float ScaleFor(float eddyMeters)
            {
                var p = FlowParams.Default;
                p.EddySizeMeters = eddyMeters;
                var f = new FlowField(grid, p);
                f.RebuildAll();
                return f.SpeedScale;
            }

            Assert.Greater(ScaleFor(2.0f), ScaleFor(0.25f),
                "渦の大きさが ∇×ψ の大きさに効いていない");
        }

        // ================= 決定論 =================

        /// <summary>
        /// **同一シード・同一ティック数から同一の場に到達すること。**
        /// Phase B は判定が無いが、この規約だけは後で判定を乗せる土台として維持する。
        /// </summary>
        [Test]
        public void Determinism_SameSeedAndTicksReachTheSameField()
        {
            ulong Run(uint seed, int ticks)
            {
                var grid = MakeRoomGrid();
                var p = FlowParams.Default;
                p.Seed = seed;
                var field = new FlowField(grid, p);
                field.RebuildAll();
                for (int i = 0; i < ticks; i++) field.Tick();
                return field.ComputeContentHash();
            }

            Assert.AreEqual(Run(1u, 20), Run(1u, 20), "同一条件の2回が違う場になった");
            Assert.AreNotEqual(Run(1u, 20), Run(2u, 20), "シードが違うのに同じ場");
            Assert.AreNotEqual(Run(1u, 20), Run(1u, 21), "ティック数が違うのに同じ場");
        }

        /// <summary>
        /// **ノイズは評価順に依存しないこと。**
        ///
        /// ψ は縞に分けて更新するので、「どの順で評価しても同じ値」でなければ
        /// 分割の仕方で結果が変わってしまう。ハッシュから作っているので
        /// テーブルも状態も持たない。
        /// </summary>
        [Test]
        public void Noise_IsPurelyPositionalAndOrderIndependent()
        {
            var p = FlowParams.Default;
            float a = CurlNoise3.Fbm(1.5f, 2.5f, 3.5f, p.Seed, p.Octaves);
            float b = CurlNoise3.Fbm(9.5f, 8.5f, 7.5f, p.Seed, p.Octaves);
            float a2 = CurlNoise3.Fbm(1.5f, 2.5f, 3.5f, p.Seed, p.Octaves);

            Assert.AreEqual(a, a2, "同じ座標から違う値が出た（状態を持っている疑い）");
            Assert.AreNotEqual(a, b, "違う座標なのに同じ値");
            Assert.GreaterOrEqual(a, -1f); Assert.LessOrEqual(a, 1f);
        }

        /// <summary>
        /// 縞の分割数を変えても、同じティック数を回せば同じ場に落ち着くこと……ではなく、
        /// **縞の分割はティック駆動であってフレーム駆動ではない**ことの確認。
        /// 同じティック数なら常に同じ縞が更新される。
        /// </summary>
        [Test]
        public void Determinism_StripeUpdateFollowsTicksNotFrames()
        {
            var grid = MakeRoomGrid();
            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();

            var hashes = new ulong[FlowField.NoiseStripes * 2];
            for (int i = 0; i < hashes.Length; i++)
            {
                field.Tick();
                hashes[i] = field.ComputeContentHash();
            }

            var grid2 = MakeRoomGrid();
            var field2 = new FlowField(grid2, FlowParams.Default);
            field2.RebuildAll();
            for (int i = 0; i < hashes.Length; i++)
            {
                field2.Tick();
                Assert.AreEqual(hashes[i], field2.ComputeContentHash(),
                    $"tick {i + 1}: 同じティック数で違う場になった");
            }
            Assert.AreEqual(hashes.Length, field2.TickCount);
        }

        // ================= 読み出し =================

        /// <summary>
        /// 三線形補間の読み出しがセル中心で格子値と一致すること。
        /// 粒子の移流はこの読み出しを使う。
        /// </summary>
        [Test]
        public void Sample_MatchesTheGridValueAtCellCentres()
        {
            var grid = MakeRoomGrid();
            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();

            foreach (var (x, y, z) in new[] { (6, 6, 6), (10, 8, 12), (14, 10, 16) })
            {
                field.VelocityAt(x, y, z, out float ex, out float ey, out float ez);
                float wx = grid.OriginX + (x + 0.5f) * grid.CellSize;
                float wy = grid.OriginY + (y + 0.5f) * grid.CellSize;
                float wz = grid.OriginZ + (z + 0.5f) * grid.CellSize;
                field.SampleVelocity(wx, wy, wz, out float sx, out float sy, out float sz);

                Assert.AreEqual(ex, sx, 1e-5f, $"({x},{y},{z}) の x 成分");
                Assert.AreEqual(ey, sy, 1e-5f, $"({x},{y},{z}) の y 成分");
                Assert.AreEqual(ez, sz, 1e-5f, $"({x},{y},{z}) の z 成分");
            }
        }

        /// <summary>格子の外を読んでも落ちないこと（粒子が外へ出たときの保険）。</summary>
        [Test]
        public void Sample_OutsideTheGridReturnsZeroWithoutThrowing()
        {
            var grid = MakeRoomGrid();
            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();

            field.SampleVelocity(-10f, -10f, -10f, out float vx, out float vy, out float vz);
            Assert.AreEqual(0f, vx + vy + vz, 1e-9f);
        }

        // ================= 流れが実際に立っているか =================

        /// <summary>
        /// **流れがゼロでないこと。** 発散ゼロも壁で止まるのも、
        /// 「どこにも流れが無い」なら自明に満たされてしまう。
        /// </summary>
        [Test]
        public void Flow_IsActuallyMovingSomewhere()
        {
            var grid = MakeRoomGrid();
            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();

            double maxSpeed = 0;
            for (int z = 2; z < grid.Depth - 2; z++)
                for (int y = 2; y < grid.Height - 2; y++)
                    for (int x = 2; x < grid.Width - 2; x++)
                    {
                        field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                        maxSpeed = Math.Max(maxSpeed, Math.Sqrt(vx * vx + vy * vy + vz * vz));
                    }

            Assert.Greater(maxSpeed, 1e-4, "格子のどこにも流れが無い");
        }

        /// <summary>
        /// 流れが時間とともに変わること（止まった絵にならない）。
        /// ノイズ座標が毎ティック進むので、同じセルの流速が変化する。
        /// </summary>
        [Test]
        public void Flow_ChangesOverTime()
        {
            var grid = MakeRoomGrid();
            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();
            field.VelocityAt(10, 8, 12, out float ax, out float ay, out float az);

            // 全縞が1周する分だけ進める
            for (int i = 0; i < FlowField.NoiseStripes * 4; i++) field.Tick();
            field.VelocityAt(10, 8, 12, out float bx, out float by, out float bz);

            double delta = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
            Assert.Greater(delta, 1e-5, "時間が経っても流れが変わらない");
        }
    
        /// <summary>
        /// **目標流速 0 で止水になること（NaN を出さないこと）。**
        ///
        /// 0.00 は実機の判定用モード。流れがある状態ではクラゲが自力で進んでいるのか
        /// 流されているのかを目で見分けられないので、止水にして「動く分はすべて自力」
        /// という一意な状況を作る。正規化は中央値で割るので、0 を入れる経路を
        /// 実際に通しておく。
        /// </summary>
        [Test]
        public void TargetSpeedZero_ProducesStillWaterWithoutNaN()
        {
            var grid = FlowGrid.FromBounds(0f, 0f, 0f, 2f, 2f, 2f, 0.1f);
            FlowBoundaryBaker.SealBorders(grid);
            FlowBoundaryBaker.BakeDistance(grid);

            var p = FlowParams.Default;
            p.TargetSpeed = 0f;
            var field = new FlowField(grid, p);
            field.RebuildAll();
            for (int t = 0; t < 20; t++) field.Tick();

            double max = 0;
            for (int z = 0; z < grid.Depth; z++)
                for (int y = 0; y < grid.Height; y++)
                    for (int x = 0; x < grid.Width; x++)
                    {
                        field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                        Assert.IsFalse(float.IsNaN(vx) || float.IsNaN(vy) || float.IsNaN(vz),
                            $"NaN が出た ({x},{y},{z})");
                        max = Math.Max(max, Math.Sqrt(vx * vx + vy * vy + vz * vz));
                    }

            Assert.AreEqual(0.0, max, 1e-9, $"止水にならなかった（最大 {max}）");
        }

        /// <summary>止水でも粒子や描画が壊れないこと（ハッシュが取れる = 状態が健全）。</summary>
        [Test]
        public void TargetSpeedZero_StateStaysWellFormed()
        {
            var grid = FlowGrid.FromBounds(0f, 0f, 0f, 2f, 2f, 2f, 0.1f);
            FlowBoundaryBaker.SealBorders(grid);
            FlowBoundaryBaker.BakeDistance(grid);

            var p = FlowParams.Default;
            p.TargetSpeed = 0f;
            var a = new FlowField(grid, p); a.RebuildAll();
            var b = new FlowField(grid, p); b.RebuildAll();
            Assert.AreEqual(a.ComputeContentHash(), b.ComputeContentHash());
        }
}
}
