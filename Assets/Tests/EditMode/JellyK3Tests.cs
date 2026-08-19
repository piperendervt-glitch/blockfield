using System;
using System.Collections.Generic;
using BlockField.SimCore.Fluid;
using BlockField.SimCore.Rng;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// jelly_2 K3: 境界からの侵害受容（prereg §3 + 追記13）。
    ///
    /// 【主張】環境が刺激の位置を決め、逃避の向きは依然として創発する。
    /// 「壁に最も近い1セル」を argmax で選ぶ形は**床に対して破綻する**ので
    /// （縁の距離差が 0.42 セルしかなく整数チャンファで読めない）、
    /// **体表の受容器が帯に入っていれば発火する**形に置き換えてある（追記13 A13.1）。
    /// </summary>
    public sealed class JellyK3Tests
    {
        const float k_Dt = 1f / 40f;

        /// <summary>
        /// 判定用の部屋。外周1セルを固体にして距離場を焼く。
        /// セルサイズは実機の既定と同じ 0.08 m。
        /// </summary>
        static FlowGrid Room(int w = 26, int h = 26, int d = 26, float cell = 0.08f)
        {
            var g = new FlowGrid(w, h, d, cell, 0f, 0f, 0f);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    for (int z = 0; z < d; z++)
                        if (x == 0 || y == 0 || z == 0 || x == w - 1 || y == h - 1 || z == d - 1)
                            g.SetSolid(x, y, z, true);
            FlowBoundaryBaker.BakeDistance(g);
            return g;
        }

        static JellyParams K3Params(bool nociception, float sinkRatio)
        {
            var p = JellyParams.Default;
            p.JetModel = true;
            p.Nociception = nociception;
            p.SinkRatio = sinkRatio;
            return p;
        }

        /// <summary>壁面からの距離 (m)。判定 M-K3a 用。</summary>
        static float WallDistance(FlowGrid g, float x, float y, float z)
        {
            int gx = (int)Math.Floor((x - g.OriginX) / g.CellSize);
            int gy = (int)Math.Floor((y - g.OriginY) / g.CellSize);
            int gz = (int)Math.Floor((z - g.OriginZ) / g.CellSize);
            if (!g.InRange(gx, gy, gz)) return 0f;
            return Math.Max(0f, g.DistanceInCells(g.Index(gx, gy, gz)) - 0.5f) * g.CellSize;
        }

        /// <summary>
        /// 1個体を走らせて判定量をまとめて返す。
        /// **実移動で測る**（推力からの積分ではない）— Phase C で「泳げている」と
        /// 報告する指標が「止まっている」個体に出ていたため（prereg §3.4）。
        /// </summary>
        struct Run
        {
            public float MeanWallDistance;   // m
            public float MeanActualSpeed;    // m/s
            public int Coverage;             // 訪れた格子セル数
            public float MeanHeightAboveFloor; // m
            public long NociceptionCount;
            public float FinalTilt;          // 度
        }

        static Run Simulate(FlowGrid g, uint seed, bool nociception, float sinkRatio,
            int steps = 4000, float startAboveFloor = -1f)
        {
            var rng = new Mulberry32(seed);
            var p = K3Params(nociception, sinkRatio);

            // 部屋の内側にランダムな初期位置。壁から傘半径ぶんは離す
            float margin = g.CellSize * 2f + p.BellDiameter;
            float sx = g.OriginX + margin + rng.NextFloat01() * (g.Width * g.CellSize - 2f * margin);
            float sy = startAboveFloor >= 0f
                ? g.OriginY + g.CellSize + startAboveFloor
                : g.OriginY + margin + rng.NextFloat01() * (g.Height * g.CellSize - 2f * margin);
            float sz = g.OriginZ + margin + rng.NextFloat01() * (g.Depth * g.CellSize - 2f * margin);

            var j = new Jellyfish(p, sx, sy, sz, g);

            // 初期姿勢もシードで振る。**積分を通して**傾ける（軸は代入されない）
            float ox = (rng.NextFloat01() - 0.5f) * 4f, oz = (rng.NextFloat01() - 0.5f) * 4f;
            for (int k = 0; k < 20; k++) j.NudgeForTest(ox, 0f, oz, k_Dt);

            double wall = 0, height = 0, speed = 0;
            var visited = new HashSet<int>();
            float px = j.X, py = j.Y, pz = j.Z;
            float floorY = g.OriginY + g.CellSize;   // 外周1セルが固体なので床面はここ

            // 【後半だけで測る】t=0 からの累積平均だと、途中で固まった個体でも
            // 序盤に動いたぶんが残って「動いている」ように見える。
            // 位相で標本化した傾き（追記8）と同じ族の誤りで、最初これで測っていた
            int from = steps / 2;
            int n = 0;

            for (int t = 0; t < steps; t++)
            {
                j.Step(k_Dt, 0f, 0f, 0f);

                float dx = j.X - px, dy = j.Y - py, dz = j.Z - pz;
                float step = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz) / k_Dt;
                px = j.X; py = j.Y; pz = j.Z;

                int gx = (int)Math.Floor((j.X - g.OriginX) / g.CellSize);
                int gy = (int)Math.Floor((j.Y - g.OriginY) / g.CellSize);
                int gz = (int)Math.Floor((j.Z - g.OriginZ) / g.CellSize);
                if (g.InRange(gx, gy, gz)) visited.Add(g.Index(gx, gy, gz));

                if (t < from) continue;
                wall += WallDistance(g, j.X, j.Y, j.Z);
                height += j.Y - floorY;
                speed += step;
                n++;
            }

            return new Run
            {
                MeanWallDistance = (float)(wall / n),
                MeanActualSpeed = (float)(speed / n),
                Coverage = visited.Count,
                MeanHeightAboveFloor = (float)(height / n),
                NociceptionCount = j.NociceptionCount,
                FinalTilt = j.TiltDegrees,
            };
        }

        // ================= 追記13 A13.1 の根拠を固定する =================

        /// <summary>
        /// **M-K3f 受容器の位置が刺激の位置を決める。**（追記13 A13.1）
        ///
        /// 床の真上・上向きなら**16セルが同時に**接触し、垂直な壁なら
        /// **一部だけ**が接触する。これが「argmax をやめた」ことの内容である。
        /// argmax なら床でも必ず1セルしか選ばれず、どれが選ばれるかは
        /// タイブレークの実装が決めていた。
        /// </summary>
        [Test]
        public void MK3f_TheBodySurfaceDecidesWhichCellsFire()
        {
            var g = Room();
            var p = K3Params(true, 1.10f);
            int n = p.RingCells;
            var contact = new bool[n];
            var posture = JellyPosture.Upright;

            var cos = new float[n];
            var sin = new float[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2.0 * Math.PI * i / n;
                cos[i] = (float)Math.Cos(a);
                sin[i] = (float)Math.Sin(a);
            }
            float r = p.BellDiameter * 0.5f;
            float mid = g.Width * g.CellSize * 0.5f;

            // 床のすぐ上、軸は真上。縁は全周が同じ高さにある
            int onFloor = JellyBoundary.SurfaceContact(g, mid, g.OriginY + g.CellSize * 1.4f, mid,
                r, posture, cos, sin, p.NociceptionBandCells, contact);
            Assert.AreEqual(n, onFloor,
                $"床の真上で {onFloor}/{n} セルしか発火していない。" +
                "縁は全周が同じ高さにあるので全部が同時に入るはず");

            // 垂直な壁のすぐ横、軸は真上。壁側の縁だけが入る
            int atWall = JellyBoundary.SurfaceContact(g, g.OriginX + g.CellSize * 1.4f, mid, mid,
                r, posture, cos, sin, p.NociceptionBandCells, contact);
            Assert.Greater(atWall, 0, "垂直な壁で1セルも発火していない");
            Assert.Less(atWall, n,
                $"垂直な壁で {atWall}/{n} セルが発火した。壁側だけが入るはず（非対称にならない）");

            // 部屋の中心では発火しない（空の検証の防止）
            int middle = JellyBoundary.SurfaceContact(g, mid, mid, mid,
                r, posture, cos, sin, p.NociceptionBandCells, contact);
            Assert.AreEqual(0, middle, "部屋の中心で発火した。帯の判定が効いていない");
        }

        /// <summary>
        /// **追記13 A13.1 の数値を固定する。** 傘の縁の「床までの距離差」が
        /// セルサイズを大きく下回ることが、argmax をやめた理由そのものである。
        /// 傾き 13°（実測の最大）でも 0.42 セルしかない。
        /// </summary>
        [Test]
        public void MK3f_TheFloorContrastIsBelowOneCell()
        {
            var p = JellyParams.Default;
            float diameter = p.BellDiameter;   // 0.15 m
            const float cell = 0.08f;

            foreach (var (tiltDeg, expectedCells) in new[]
                { (0f, 0.00f), (5.05f, 0.17f), (13.0f, 0.42f) })
            {
                float contrast = diameter * (float)Math.Sin(tiltDeg * Math.PI / 180.0);
                Assert.AreEqual(expectedCells, contrast / cell, 0.01f,
                    $"傾き {tiltDeg}° の縁の距離差が {contrast / cell:F2} セル");
                Assert.Less(contrast / cell, 1f,
                    "距離差が1セルを超えた。整数チャンファなら argmax でも読めることになる");
            }

            // 対照: 垂直な壁なら傘の直径ぶんの差があり、1セルを超える
            Assert.Greater(diameter / cell, 1f,
                "垂直な壁でも1セル未満なら、体表規則でも壁側を区別できない");
        }

        /// <summary>
        /// **M-K3g 接触マスクから面を分類できるか。**（prereg 追記17 A17.3）
        ///
        /// 実機ログが接触セル「数」しか出しておらず、隅と単一壁を分離できなかった。
        /// マスクを出すことにしたが、**マスク単独でも分離できない**ことを実測で固定する:
        /// 隅は「2弧」にはならず、**幅の広い1弧**になる。床・天井・隅+床はどれも FFFF。
        /// 分類には**マスク + 床上の高さ + 壁までの距離**の3つが要る。
        /// </summary>
        [Test]
        public void MK3g_TheMaskAloneCannotSeparateACornerFromAWall()
        {
            var g = Room();
            var p = K3Params(true, 1.10f);
            int n = p.RingCells;
            var contact = new bool[n];
            var cos = new float[n];
            var sin = new float[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2.0 * Math.PI * i / n;
                cos[i] = (float)Math.Cos(a);
                sin[i] = (float)Math.Sin(a);
            }
            float r = p.BellDiameter * 0.5f;
            float mid = g.Width * g.CellSize * 0.5f;
            float near = g.CellSize * 1.4f;
            float far = g.Height * g.CellSize - g.CellSize * 1.4f;

            int Mask(float x, float y, float z)
            {
                JellyBoundary.SurfaceContact(g, x, y, z, r, JellyPosture.Upright,
                    cos, sin, p.NociceptionBandCells, contact);
                int m = 0;
                for (int i = 0; i < n; i++) if (contact[i]) m |= 1 << i;
                return m;
            }
            // 円環上の連続した弧の数（全周は 1 とみなす）
            int Arcs(int mask)
            {
                if (mask == 0) return 0;
                if (mask == (1 << n) - 1) return 1;
                int a = 0;
                for (int i = 0; i < n; i++)
                {
                    bool cur = (mask & (1 << i)) != 0;
                    bool prev = (mask & (1 << ((i + n - 1) % n))) != 0;
                    if (cur && !prev) a++;
                }
                return a;
            }
            int Bits(int mask)
            {
                int c = 0;
                for (int i = 0; i < n; i++) if ((mask & (1 << i)) != 0) c++;
                return c;
            }

            int floor = Mask(mid, near, mid);
            int ceiling = Mask(mid, far, mid);
            int wall = Mask(near, mid, mid);
            int corner = Mask(near, mid, near);
            int cornerFloor = Mask(near, near, near);
            int centre = Mask(mid, mid, mid);

            Assert.AreEqual(0xFFFF, floor, "床の真上で全周が入らなかった");
            Assert.AreEqual(0xFFFF, ceiling, "天井の真下で全周が入らなかった");
            Assert.AreEqual(0xFFFF, cornerFloor, "隅+床が全周にならなかった");
            Assert.AreEqual(0, centre, "部屋の中心で接触した");

            // **隅は2弧ではない。** ここが取り違えやすい点なので明示的に落とす
            Assert.AreEqual(1, Arcs(wall), $"単一の壁が {Arcs(wall)} 弧になった");
            Assert.AreEqual(1, Arcs(corner),
                $"隅が {Arcs(corner)} 弧になった。**隅=2弧という読み方はできない**");

            // 分離できるのは弧の数ではなく**幅**
            Assert.Less(Bits(wall), Bits(corner),
                $"単一の壁 {Bits(wall)}bit と隅 {Bits(corner)}bit の幅が分離しない");
            Assert.AreEqual(11, Bits(wall), "単一の壁の幅が実測（11bit）と違う");
            Assert.AreEqual(15, Bits(corner), "隅の幅が実測（15bit）と違う");

            // FFFF は床・天井・隅+床のいずれでも出る。床上の高さで初めて分かれる
            float hFloor = JellyBoundary.HeightAboveFloor(g, mid, near, mid);
            float hCeiling = JellyBoundary.HeightAboveFloor(g, mid, far, mid);
            Assert.Less(hFloor, r,
                $"床の真上の床上高さが {hFloor:F3} m。傘半径 {r:F3} m を下回るはず");
            Assert.Greater(hCeiling, 1f,
                $"天井の真下の床上高さが {hCeiling:F3} m。マスクが同じなので高さで分ける");
        }

        /// <summary>
        /// **M-K3h 6配置は5つ組で一意に分かれるか。**（prereg 追記18 A18.2）
        ///
        /// 4つ組（mask/床上/壁1/壁2）では **5/6 しか分かれない** —
        /// `隅+床` と `壁+床` が `FFFF|0.03|0.03|0.03` で完全に同一だった。
        /// 3番目の面距離を足すと **6/6** になる。両方を固定する。
        ///
        /// **壁3 は記録専用である。** 判定の連鎖のリンク1 は 壁2 ≤ 0.155 m のままで、
        /// 壁3 では絞らない（追記18 A18.6）。
        /// </summary>
        [Test]
        public void MK3h_TheFiveTupleSeparatesAllSixPlacements()
        {
            var g = Room();
            var p = K3Params(true, 1.10f);
            int n = p.RingCells;
            var contact = new bool[n];
            var cos = new float[n];
            var sin = new float[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2.0 * Math.PI * i / n;
                cos[i] = (float)Math.Cos(a);
                sin[i] = (float)Math.Sin(a);
            }
            float r = p.BellDiameter * 0.5f;
            float mid = g.Width * g.CellSize * 0.5f;
            float near = g.CellSize * 1.4f;
            float far = g.Height * g.CellSize - g.CellSize * 1.4f;

            (string key, float second, float third) Probe(float x, float y, float z)
            {
                JellyBoundary.SurfaceContact(g, x, y, z, r, JellyPosture.Upright,
                    cos, sin, p.NociceptionBandCells, contact);
                int m = 0;
                for (int i = 0; i < n; i++) if (contact[i]) m |= 1 << i;
                float h = JellyBoundary.HeightAboveFloor(g, x, y, z);
                JellyBoundary.FaceDistances(g, x, y, z, out float d1, out float d2, out float d3);
                return ($"{m:X4}|{h:F2}|{d1:F2}|{d2:F2}", d2, d3);
            }
            string Five((string key, float second, float third) a) => $"{a.key}|{a.third:F2}";

            var floor = Probe(mid, near, mid);
            var ceiling = Probe(mid, far, mid);
            var wall = Probe(near, mid, mid);
            var corner = Probe(near, mid, near);
            var cornerFloor = Probe(near, near, near);
            var wallFloor = Probe(near, near, mid);

            var all = new[] { floor, ceiling, wall, corner, cornerFloor, wallFloor };

            // 4つ組では分かれない組があることを固定する（分かれる配置に選び直さない）
            var four = new HashSet<string>();
            foreach (var a in all) four.Add(a.key);
            Assert.AreEqual(5, four.Count,
                $"4つ組で分かれた配置が {four.Count}/6。実測は 5/6 のはず");
            Assert.AreEqual(cornerFloor.key, wallFloor.key,
                "隅+床 と 壁+床 が4つ組で分離した。実測では同一（FFFF|0.03|0.03|0.03）");

            // 壁3 を足すと 6/6 になる
            var five = new HashSet<string>();
            foreach (var a in all) five.Add(Five(a));
            Assert.AreEqual(6, five.Count,
                $"5つ組で分かれた配置が {five.Count}/6。壁3 を足しても分けきれていない");

            // A18.3 の閾値 0.155 m が実測を分けることの確認
            const float CornerThreshold = 0.155f;   // 傘半径 0.075 + 帯幅 0.08
            Assert.Less(corner.second, CornerThreshold, "隅（2面）が閾値の上に来た");
            Assert.Less(cornerFloor.second, CornerThreshold, "隅+床 が閾値の上に来た");
            Assert.Greater(wall.second, CornerThreshold, "単一の壁が閾値の下に来た");
            Assert.Greater(floor.second, CornerThreshold, "床が閾値の下に来た");
            Assert.Greater(ceiling.second, CornerThreshold, "天井が閾値の下に来た");
        }

        // ================= M-K3e: 対称発火の推力（対照と位相掃引つき） =================

        /// <summary>
        /// **M-K3e 対称16セル発火が推力に与える効果。**（追記13 A13.3）
        ///
        /// 「底近くを漂う」という予想には**対称発火が推力を増やす**という
        /// 未検証の前提が埋まっていた。M-K2a が測ったのは「旋回しない」だけである。
        ///
        /// **対照 = ペースメーカーのみ**（同じ周期・同じ時間）。これが無いと
        /// 「増やす／減らす」が言えない（K1 の教訓）。
        /// **位相は環境が決める**（帯に入った時刻で決まる）ので全点掃引し、
        /// 範囲を固定する。単一位相での標本化は前科3件と同じ族の誤りである。
        /// </summary>
        [Test]
        public void MK3e_SymmetricFiringChangesTheThrust_AllPhases()
        {
            var p = JellyParams.Default;
            p.JetModel = true;
            int T = p.PulsePeriodTicks;

            float Speed(int offset)
            {
                var j = new Jellyfish(p, 0f, 0f, 0f);
                void Inject(int t)
                {
                    if (offset < 0 || t % T != offset) return;
                    for (int i = 0; i < p.RingCells; i++) j.StimulateCell(i);
                }
                for (int t = 0; t < 800; t++) { Inject(t); j.Step(k_Dt, 0f, 0f, 0f); }

                float px = j.X, py = j.Y - j.SinkPathY, pz = j.Z;
                double path = 0;
                for (int t = 0; t < 1600; t++)
                {
                    Inject(800 + t);
                    j.Step(k_Dt, 0f, 0f, 0f);
                    float cy = j.Y - j.SinkPathY;
                    double dx = j.X - px, dy = cy - py, dz = j.Z - pz;
                    path += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    px = j.X; py = cy; pz = j.Z;
                }
                return (float)(path / (1600.0 / 40.0));
            }

            float control = Speed(-1);
            Assert.AreEqual(p.SwimSpeed, control, p.SwimSpeed * 0.05f,
                $"対照が目標速度で泳いでいない（{control:F5} m/s）。比較が成立しない");

            int dead = 0, gained = 0;
            float best = 0f;
            for (int off = 0; off < T; off++)
            {
                float ratio = Speed(off) / control - 1f;
                if (Math.Abs(ratio) < 0.10f) dead++;
                else if (ratio > 0f) gained++;
                if (ratio > best) best = ratio;
            }

            // 【結果は「増える」側】追記13 A13.3 の分岐のうち +10% 以上が多数を占める
            Assert.AreEqual(8, dead,
                $"不応期の影に落ちる位相が {dead}/{T} 個。登録時の測定は 8 個");
            Assert.AreEqual(T - 8, gained,
                $"推力が増える位相が {gained}/{T} 個。残りは減っていることになる");
            Assert.Greater(best, 10f,
                $"最大でも対照の {best:+0%} にしかならない。噴流は dV の2乗なので" +
                "全周同時収縮は桁で効くはず");
        }

        // ================= M-K3a〜d =================

        /// <summary>
        /// **M-K3a・M-K3c は取り下げ。**（追記14 A14.2）代わりに**取り下げの理由**を固定する。
        ///
        /// 【なぜ取り下げたか】§3.4 の M-K3a/c は噴流モデルと沈降が入る前の登録である。
        /// 噴流モデルで沈降を切ると全個体が上昇し続けて天井に達するので、
        /// M-K3a は「**天井に貼り付いた個体同士**」を比べていた。
        /// 実測 M-K3a 0/48（ON 0.0400 m = OFF 0.0400 m）、M-K3c 29/48（被覆 18.4 対 14.8）。
        ///
        /// 【判定を消さずに機構を残す】K3 の結論は
        /// **「対称発火は逃避ではなく軸方向のパワーストロークであり、
        /// 逃避は復元トルクとの合作としてしか成立しない」**である。
        /// 天井では推力が面へ押し込む向きになり、しかも対称発火は
        /// M-K2a より構造的にトルク 0 なので、**逃げるための非対称を永久に作れない**。
        /// これを塞がず、測定結果として残す（方針B）。
        /// </summary>
        [Test]
        public void MK3a_Withdrawn_SymmetricFiringIsAnAxialPowerStrokeNotAnEscape()
        {
            var g = Room();
            float ceiling = (g.Height - 1) * g.CellSize;   // 外周1セルが固体

            var on = Simulate(g, 40, nociception: true, sinkRatio: 0f);
            var off = Simulate(g, 40, nociception: false, sinkRatio: 0f);

            // 検定台が退化していること自体を固定する: 侵害受容の有無によらず天井に達する
            Assert.AreEqual(off.MeanWallDistance, on.MeanWallDistance, 1e-4f,
                $"沈降OFFで ON {on.MeanWallDistance:F4} m と OFF {off.MeanWallDistance:F4} m に差が出た。" +
                "取り下げの理由（両者とも天井に貼り付く）が成り立っていない");
            Assert.Less(on.MeanHeightAboveFloor, ceiling,
                "天井より上にいることになっている");
            Assert.Greater(on.MeanHeightAboveFloor, ceiling - 3f * g.CellSize,
                $"沈降OFFなのに天井へ達していない（床から {on.MeanHeightAboveFloor:F3} m）。" +
                "噴流は上向きなので必ず上がりきるはず");

            // 【証拠を訂正した】旧規則（周期Tごとに撃ち続ける）では傾きが 0.0° に
            // 固定され「侵害受容が事態を悪化させる」と読めたが、それは規則の性質だった。
            // 新規則（侵入時に1回）では接触が続くとロックアウトされるので、
            // **天井では侵害受容は不活性**になる（追記15 A15.5）。
            // 結論（対称発火は軸方向のパワーストローク、軸側の面からは逃げられない）は保持
            Assert.AreEqual(off.FinalTilt, on.FinalTilt, 0.01f,
                $"天井で ON {on.FinalTilt:F2}° と OFF {off.FinalTilt:F2}° に差が出た。" +
                "新規則では侵害受容はロックアウトされて不活性なはず");
            Assert.AreEqual(off.MeanActualSpeed, on.MeanActualSpeed, 1e-5f,
                $"天井で ON {on.MeanActualSpeed:F5} m/s と OFF {off.MeanActualSpeed:F5} m/s に差が出た");
            Assert.Greater(off.FinalTilt, 1f,
                $"対照の傾きが {off.FinalTilt:F2}°。振れていなければ比較の意味がない");
        }

        /// <summary>
        /// **M-K3b 静止しない。** これが要である。Phase C で「泳げている」と
        /// 報告する指標（推力からの積分）が「止まっている」個体に出ていたので、
        /// **実移動で測る**ことを判定に明記してある（prereg §3.4）。
        /// 沈降 OFF / ON の両方で見る。
        /// </summary>
        [Test]
        public void MK3b_ItDoesNotFreeze()
        {
            var g = Room();
            // 【沈降OFFは取り下げ】その世界は天井に貼り付く個体しか作らない（A14.2）
            foreach (float sink in new[] { 1.10f })
            {
                double worst = double.MaxValue;
                uint worstSeed = 0;
                for (uint s = 1; s <= 48; s++)
                {
                    var r = Simulate(g, s, nociception: true, sinkRatio: sink);
                    if (r.MeanActualSpeed < worst) { worst = r.MeanActualSpeed; worstSeed = s; }
                }
                Assert.GreaterOrEqual(worst, JellyParams.Default.SwimSpeed * 0.5f,
                    $"沈降 {sink:P0} でシード {worstSeed} の実移動が {worst:F5} m/s。" +
                    "目標の 50% を下回った（壁反発で一度同じ失敗をしている）");
            }
        }

        /// <summary>
        /// **M-K3d は取り下げ。**（追記21）合格を取り消し、**検定台として不成立**とする。
        /// M-K3a・M-K3c と同じ扱い。判定文と実測は残す。
        ///
        /// 【なぜ取り下げたか】**測定窓の産物だった。**
        /// この判定の窓は 8,000 tick = 200秒だが、**着底までの中央値は 350秒**である。
        /// 100,000 tick 走らせると **48/48 が着底する**（最短 48秒 / 最長 669秒）。
        /// 対照として回した「漂えた」シード 1・2 も着底した（295秒、222秒）。
        /// 42/48 が測っていたのは「着底しない」ではなく「**まだ着底していない**」。
        ///
        /// **緩めたのではなく取り消した。** 追記16 の判定文変更（全シード → 40/48）も
        /// これに伴って意味を失っている（追記16 自体は残す）。
        ///
        /// 【テストは何を固定しているか】合否ではなく**窓の産物であること**を固定する。
        /// 8,000 tick では 6/48 しか着底せず、対照（侵害受容OFF）では 48/48 着底する
        /// —— この差は所見「**侵害受容は着底を遅らせる**」の根拠として残る
        /// （追記21 A21.2。**判定ではなく所見**である）。
        /// </summary>
        [Test]
        public void MK3d_Withdrawn_TheWindowDecidedTheOutcome()
        {
            var g = Room();
            float radius = JellyParams.Default.BellDiameter * 0.5f;

            // 【床に届かなければ空の検証になる】沈降 1.10 でも正味は 0.0041 m/s しかなく、
            // 部屋の高さから始めると 4000 ティックでは 0.16 m しか降りない。
            // **床の近くから始め、対照が実際に着底することを先に確かめる**
            const float Start = 0.30f;
            const int Steps = 8000;

            int hovering = 0, settledOff = 0;
            double worstOn = double.MaxValue;
            uint worstSeed = 0;
            long noci = 0;
            for (uint s = 1; s <= 48; s++)
            {
                var on = Simulate(g, s, true, 1.10f, Steps, Start);
                var off = Simulate(g, s, false, 1.10f, Steps, Start);
                noci += on.NociceptionCount;
                if (on.MeanHeightAboveFloor >= radius) hovering++;
                if (off.MeanHeightAboveFloor < radius) settledOff++;
                if (on.MeanHeightAboveFloor < worstOn) { worstOn = on.MeanHeightAboveFloor; worstSeed = s; }
            }

            Assert.Greater(noci, 0, "48シードで侵害受容が一度も発火していない");

            TestContext.WriteLine(
                $"M-K3d（取り下げ済み）: 8,000 tick の窓で漂えた {hovering}/48、" +
                $"対照(侵害受容OFF)で着底 {settledOff}/48。" +
                $"最も低いシード {worstSeed} で {worstOn:F4} m（傘半径 {radius:F4} m）");

            // 【合否は固定しない】判定は取り下げた。固定するのは
            // 「窓の中では差が出る」という所見の側だけである
            Assert.AreEqual(48, settledOff,
                $"対照（侵害受容OFF）で着底したのは {settledOff}/48。48/48 のはず");
            Assert.LessOrEqual(48 - hovering, 12,
                $"侵害受容ONで着底したのが {48 - hovering}/48。窓 8,000 tick での実測は 6/48");
            Assert.Greater(settledOff, 48 - hovering,
                "侵害受容の有無で着底数に差が無い。所見「着底を遅らせる」が成立しない");
        }
    }
}
