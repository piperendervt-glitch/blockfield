using System;
using BlockField.Aquarium;
using BlockField.SimCore.Fluid;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// 系列2 Phase C: 水槽のクラゲ1体。
    ///
    /// 【この段で固定すること】jelly_1 の神経（`jelly-1.1`）をそのまま使い、
    /// **無次元だったモデルに物理単位を与えた**ことを確かめる。
    /// Phase B で ψ の振幅に物理スケールが無く実機まで気づかなかったので、
    /// 同じ形にはしない。
    /// </summary>
    public class JellyfishTests
    {
        static FlowGrid MakeTank(float cell = 0.08f)
        {
            var grid = FlowGrid.FromBounds(0f, 0f, 0f, 3.2f, 2.1f, 2.6f, cell);
            FlowBoundaryBaker.SealBorders(grid);
            FlowBoundaryBaker.BakeDistance(grid);
            return grid;
        }

        static Jellyfish Spawn(JellyParams p) => new Jellyfish(p, 1.6f, 1.1f, 1.3f);

        /// <summary>
        /// **沈降ぶんを除いた高さ**。沈降は世界法則であって自力遊泳ではないので
        /// （追記10）、「自力で鉛直に動いたか」を見る判定はこちらで測る。
        /// `SinkPathY` は負に積むので、引くと沈降が消える。
        /// </summary>
        static float HeightWithoutSinking(Jellyfish j) => j.Y - j.SinkPathY;

        /// <summary>自力遊泳ぶんの経路長 (m)。沈降を除いて測る。</summary>
        static float SelfPropelledPath(Jellyfish j, int steps)
        {
            float px = j.X, py = HeightWithoutSinking(j), pz = j.Z;
            double path = 0;
            for (int t = 0; t < steps; t++)
            {
                j.Step(1f / 40f, 0f, 0f, 0f);
                float cy = HeightWithoutSinking(j);
                double dx = j.X - px, dy = cy - py, dz = j.Z - pz;
                path += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                px = j.X; py = cy; pz = j.Z;
            }
            return (float)path;
        }

        /// <summary>
        /// 止水（流れゼロ）で N ステップ進めたときの**自力ぶんの**移動距離。
        /// 沈降は世界法則であって自力遊泳ではないので除く（追記10）。
        /// </summary>
        static float SwimDistanceInStillWater(Jellyfish jelly, int steps)
        {
            float x0 = jelly.X, y0 = HeightWithoutSinking(jelly), z0 = jelly.Z;
            for (int i = 0; i < steps; i++) jelly.Step(1f / 40f, 0f, 0f, 0f);
            float dx = jelly.X - x0, dy = HeightWithoutSinking(jelly) - y0, dz = jelly.Z - z0;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // ================= 物理単位を与えたこと =================

        /// <summary>
        /// **持続遊泳速度が目標値に一致すること。**
        ///
        /// jelly_1 のモデルは無次元（J3a の 0.298032 は「推力単位/ティック」）で、
        /// 傘径からは速度を導出できない。正規化して単位を与えている。
        /// </summary>
        [Test]
        public void Swim_SustainedSpeedMatchesTheTarget()
        {
            foreach (float target in new[] { 0.02f, 0.04f, 0.1f })
            {
                var p = JellyParams.Default;
                p.SwimSpeed = target;
                var jelly = Spawn(p);

                // 過渡（立ち上がり）を外してから測る。jelly_1 J3a と同じ考え方
                SwimDistanceInStillWater(jelly, 800);
                float distance = SwimDistanceInStillWater(jelly, 800);
                float speed = distance / (800f / 40f);

                Assert.AreEqual(target, speed, target * 0.05f,
                    $"目標 {target} m/s に対し実測 {speed:F4} m/s");
            }
        }

        /// <summary>
        /// **傘径を変えても遊泳速度は変わらないこと。**
        /// モデルに長さの単位が無い以上、傘径と速度は独立に与える。
        /// （実物では相関するが、それは抗力係数の逆算後に入る話。prereg jelly_1 §9）
        /// </summary>
        [Test]
        public void Swim_SpeedIsIndependentOfBellDiameter()
        {
            foreach (float bell in new[] { 0.10f, 0.15f, 0.25f })
            {
                var p = JellyParams.Default;
                p.BellDiameter = bell;
                var jelly = Spawn(p);

                SwimDistanceInStillWater(jelly, 800);
                float speed = SwimDistanceInStillWater(jelly, 800) / (800f / 40f);

                Assert.AreEqual(p.SwimSpeed, speed, p.SwimSpeed * 0.05f,
                    $"傘 {bell * 100f:F0}cm で速度が変わった");
            }
        }

        /// <summary>
        /// **1拍動が 1.0 秒であること。** 神経 40Hz × 周期 40 ステップ。
        /// 流れ場は 20Hz なので、整数比 2:1 で入れ子にできる。
        /// </summary>
        [Test]
        public void Pulse_PeriodIsOneSecondAtFortyHertz()
        {
            var jelly = Spawn(JellyParams.Default);
            for (int i = 0; i < 400; i++) jelly.Step(1f / 40f, 0f, 0f, 0f);

            // 400 ステップ = 10 秒 → 10 拍動（初回が t=0 なので 11 回目に入る手前）
            Assert.AreEqual(10, jelly.PulseCount, 1,
                $"10秒で {jelly.PulseCount} 拍動。1拍動 = 1.0 秒のはず");
        }

        // ================= 流れとの結合 =================

        /// <summary>
        /// **流れはクラゲを運ぶ。** 一様な流れの中では、その分だけ流される。
        /// </summary>
        [Test]
        public void Flow_CarriesTheJellyfish()
        {
            var still = Spawn(JellyParams.Default);
            var carried = Spawn(JellyParams.Default);

            const int steps = 400;   // 10 秒
            const float dt = 1f / 40f;
            for (int i = 0; i < steps; i++)
            {
                still.Step(dt, 0f, 0f, 0f);
                carried.Step(dt, 0f, 0.05f, 0f);   // 上向きに 0.05 m/s
            }

            // 自力遊泳は水平のみなので、鉛直の差はそのまま流れの寄与。
            // 沈降は世界法則なので除いて見る（追記10）
            Assert.AreEqual(0f, HeightWithoutSinking(still) - 1.1f, 1e-5f,
                "止水では自力で鉛直に動かないはず");
            Assert.AreEqual(0.05f * steps * dt, HeightWithoutSinking(carried) - 1.1f, 1e-3f,
                "流れに運ばれた距離が合わない");
        }

        /// <summary>
        /// **クラゲは流れに書き戻さない。** 航跡場は後の段。
        /// クラゲを進めても流れ場のハッシュが変わらないことで確かめる。
        /// </summary>
        [Test]
        public void Flow_IsNotModifiedByTheJellyfish()
        {
            var grid = MakeTank();
            var field = new FlowField(grid, FlowParams.Default);
            field.RebuildAll();
            ulong before = field.ComputeContentHash();

            var jelly = Spawn(JellyParams.Default);
            for (int i = 0; i < 400; i++)
            {
                field.SampleVelocity(jelly.X, jelly.Y, jelly.Z,
                    out float vx, out float vy, out float vz);
                jelly.Step(1f / 40f, vx, vy, vz);
            }

            Assert.AreEqual(before, field.ComputeContentHash(),
                "クラゲが流れ場を書き換えている（航跡場はこの段では入れない）");
        }

        // ================= この段の既知の制約 =================

        /// <summary>
        /// **自力で泳ぐのは水平方向だけ**であること（この段の制約）。
        ///
        /// リム収縮の推力はリング平面内にしか出ず、リング平面は水平に固定している。
        /// 鉛直方向は流れに運ばれる分しかない。
        /// 次段の dV/dt モデル（推力の大きさは体積変化、向きは傘の軸、
        /// 旋回は収縮の非対称から）でこの制約が外れる。
        /// </summary>
        [Test]
        public void Limitation_SelfPropulsionIsHorizontalOnly()
        {
            var jelly = Spawn(JellyParams.Default);
            for (int i = 0; i < 800; i++) jelly.Step(1f / 40f, 0f, 0f, 0f);

            // 沈降は世界法則なので除いて見る（追記10）。除いた高さが動かないことが
            // 「自力で泳ぐのは水平だけ」の内容である
            // 許容差 1e-4: 沈降を引き算で除いているので、800ステップぶんの
            // 浮動小数の累積誤差（実測 1.3e-5）が乗る。主張は変わらない
            Assert.AreEqual(1.1f, HeightWithoutSinking(jelly), 1e-4f,
                "止水で自力で鉛直に動いている");
            Assert.Greater(Math.Abs(jelly.X - 1.6f) + Math.Abs(jelly.Z - 1.3f), 0.01f,
                "水平にも動いていない（そもそも泳げていない）");
        }

        // ================= 決定論 =================

        /// <summary>
        /// **同一パラメータ・同一入力から同一の状態に到達すること。**
        /// クラゲは View でなく真実側なので、決定論の対象に入る。
        /// </summary>
        [Test]
        public void Determinism_SameInputReachesTheSameState()
        {
            ulong Run(float swimSpeed, int steps)
            {
                var p = JellyParams.Default;
                p.SwimSpeed = swimSpeed;
                var jelly = Spawn(p);
                for (int i = 0; i < steps; i++) jelly.Step(1f / 40f, 0.01f, 0f, 0.02f);
                return jelly.ComputeContentHash();
            }

            Assert.AreEqual(Run(0.04f, 300), Run(0.04f, 300), "2回の実行が違う状態になった");
            Assert.AreNotEqual(Run(0.04f, 300), Run(0.04f, 301), "ステップ数が違うのに同じ状態");
            Assert.AreNotEqual(Run(0.04f, 300), Run(0.08f, 300), "速度が違うのに同じ状態");
        }

        /// <summary>
        /// 換算係数がパラメータから決まること（同じ設定なら同じ値）。
        /// 実測で決めているので、g や抗力を変えても追従する。
        /// </summary>
        [Test]
        public void Calibration_IsDeterministicAndScalesWithTheTarget()
        {
            var p = JellyParams.Default;
            Assert.AreEqual(Spawn(p).SpeedScale, Spawn(p).SpeedScale);

            var faster = p; faster.SwimSpeed = p.SwimSpeed * 2f;
            Assert.AreEqual(Spawn(p).SpeedScale * 2f, Spawn(faster).SpeedScale, 1e-6f,
                "目標を倍にしたら係数も倍になるはず");
        }

        // ================= 描画のための状態 =================

        /// <summary>
        /// 収縮の度合いが 0〜1 に収まり、拍動で実際に動くこと。
        /// 傘の描画がこれを読むので、範囲が壊れると形が破綻する。
        /// </summary>
        [Test]
        public void Contraction_StaysInRangeAndActuallyMoves()
        {
            var jelly = Spawn(JellyParams.Default);
            float maxSeen = 0f, minSeen = 1f;

            for (int i = 0; i < 200; i++)
            {
                jelly.Step(1f / 40f, 0f, 0f, 0f);
                for (int c = 0; c < jelly.Ring.CellCount; c++)
                {
                    float v = jelly.Contraction(c);
                    Assert.GreaterOrEqual(v, 0f);
                    Assert.LessOrEqual(v, 1f);
                    if (v > maxSeen) maxSeen = v;
                    if (v < minSeen) minSeen = v;
                }
            }

            Assert.Greater(maxSeen, 0.5f, "収縮が浅すぎて拍動が見えない");
            Assert.Less(minSeen, 0.1f, "弛緩しきる瞬間が無い");
        }

        // ================= 傘の姿勢（2026-08-16 の実機で崩れていた） =================

        /// <summary>
        /// **リムはいつでも水平な平面に載る。**
        ///
        /// 収縮をセルごとにリムの高さへ写していたとき、興奮波がリングを巡るせいで
        /// リムが平面から外れ、実測**最大 13.6°**、40ステップ周期の 21ステップに
        /// わたって傘が傾いていた（実機報告「傘の底面が傾いている」）。
        /// 高さは対称量（リング平均）だけに使い、進行波は半径に出す。
        /// </summary>
        [Test]
        public void Bell_RimStaysOnAHorizontalPlane()
        {
            var jelly = Spawn(JellyParams.Default);
            int n = jelly.Ring.CellCount;
            var verts = new UnityEngine.Vector3[n + 1];

            for (int t = 0; t < 200; t++)
            {
                jelly.Step(1f / 40f, 0f, 0f, 0f);
                BlockField.Aquarium.JellyfishView.BuildBellVertices(jelly, verts);

                float y0 = verts[1].y;
                for (int i = 1; i <= n; i++)
                {
                    Assert.AreEqual(y0, verts[i].y, 1e-6f,
                        $"t={t} でリムが平面から外れた（セル {i - 1}）");
                }
            }
        }

        /// <summary>
        /// **拍動でリムの半径と傘の高さが実際に動く。**
        /// リムを平面に固定した副作用で「動かない傘」になっていないことを見る。
        /// </summary>
        [Test]
        public void Bell_PulseChangesRadiusAndHeight()
        {
            var jelly = Spawn(JellyParams.Default);
            int n = jelly.Ring.CellCount;
            var verts = new UnityEngine.Vector3[n + 1];
            float minApex = float.MaxValue, maxApex = float.MinValue;
            float minR = float.MaxValue, maxR = float.MinValue;

            for (int t = 0; t < 200; t++)
            {
                jelly.Step(1f / 40f, 0f, 0f, 0f);
                BlockField.Aquarium.JellyfishView.BuildBellVertices(jelly, verts);

                float apex = verts[0].y - verts[1].y;
                minApex = Math.Min(minApex, apex); maxApex = Math.Max(maxApex, apex);
                for (int i = 1; i <= n; i++)
                {
                    float r = (float)Math.Sqrt(verts[i].x * verts[i].x + verts[i].z * verts[i].z);
                    minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                }
            }

            Assert.Greater(maxApex / minApex, 1.1f, "拍動で傘の高さが変わっていない");
            Assert.Greater(maxR / minR, 1.1f, "拍動でリムの半径が変わっていない");
        }

        /// <summary>
        /// **扇の法線が外向き・上向きであること。**
        ///
        /// Unity は法線 = Cross(B-A, C-A) を使う（組み込み Quad で確認済み）。
        /// 巻き方向が逆だと法線が内向き・下向きになり、既定の裏面カリングで
        /// **外から傘が消える**（実機報告「法線が逆では」）。
        /// </summary>
        [Test]
        public void Bell_FanWindingFacesOutward()
        {
            var jelly = Spawn(JellyParams.Default);
            int n = jelly.Ring.CellCount;
            var verts = new UnityEngine.Vector3[n + 1];
            BlockField.Aquarium.JellyfishView.BuildBellVertices(jelly, verts);

            var mesh = BlockField.Aquarium.JellyfishView.BuildBellTriangles(n);
            for (int f = 0; f < n; f++)
            {
                var a = verts[mesh[f * 3]];
                var b = verts[mesh[f * 3 + 1]];
                var c = verts[mesh[f * 3 + 2]];
                var normal = UnityEngine.Vector3.Cross(b - a, c - a).normalized;

                var centroid = (a + b + c) / 3f;
                var radial = new UnityEngine.Vector3(centroid.x, 0f, centroid.z).normalized;

                Assert.Greater(UnityEngine.Vector3.Dot(normal, radial), 0f,
                    $"面 {f} の法線が内向き（巻き方向が逆）");
                Assert.Greater(normal.y, 0f, $"面 {f} の法線が下向き（巻き方向が逆）");
            }
        }
    
        // ================= 水槽の境界（2026-08-16 の脱走） =================

        /// <summary>
        /// **固体セルの中へ入らないこと。**
        ///
        /// 実機でクラゲが壁の向こうへ移動した。位置更新に境界の項が無く、
        /// 唯一あった処理は「固体セルに入ったら1セルぶん上へ押す」だった。
        /// 壁の中では上へ押しても壁の中のままなので毎ステップ登り、
        /// 天井に貼り付く（72秒間の記録が残っている）。入らせない形に直した。
        /// </summary>
        [Test]
        public void Tank_JellyfishNeverEntersASolidCell()
        {
            var grid = MakeTank();
            var jelly = new Jellyfish(JellyParams.Default, 1.6f, 1.1f, 1.3f, grid);

            // 壁へ押し付ける向きの強い流れを当て続ける
            for (int i = 0; i < 4000; i++)
            {
                jelly.Step(1f / 40f, 0.5f, -0.5f, 0.5f);
                Assert.IsTrue(JellyBoundary.IsFluid(grid, jelly.X, jelly.Y, jelly.Z),
                    $"t={i} で固体セルに入った 位置=({jelly.X:F2}, {jelly.Y:F2}, {jelly.Z:F2})");
            }
        }

        /// <summary>
        /// **壁に当たっても止まりきらないこと。** 軸ごとに拒否しているので
        /// 壁に沿って滑る。3成分まとめて拒否すると完全停止して「死んで見える」。
        /// </summary>
        [Test]
        public void Tank_JellyfishSlidesAlongTheWallInsteadOfStopping()
        {
            var grid = MakeTank();
            var jelly = new Jellyfish(JellyParams.Default, 1.6f, 1.1f, 1.3f, grid);

            // 下向きの流れで床へ押し付ける
            for (int i = 0; i < 1200; i++) jelly.Step(1f / 40f, 0f, -0.5f, 0f);
            float x0 = jelly.X, z0 = jelly.Z;
            for (int i = 0; i < 800; i++) jelly.Step(1f / 40f, 0f, -0.5f, 0f);

            Assert.Greater(Math.Abs(jelly.X - x0) + Math.Abs(jelly.Z - z0), 0.01f,
                "床に押し付けられた後、水平にも動かなくなっている");
        }

        /// <summary>
        /// 境界を渡さなければ従来どおり素通しであること（単体テストの前提）。
        /// </summary>
        [Test]
        public void Tank_WithoutAGridThereIsNoBoundary()
        {
            var jelly = Spawn(JellyParams.Default);
            for (int i = 0; i < 400; i++) jelly.Step(1f / 40f, 0f, -1f, 0f);
            Assert.Less(jelly.Y, 0f, "境界なしなら下へ抜けるはず");
        }

        /// <summary>
        /// 水の領域の内面までの距離 (m)。**連続値で測る**。
        /// 格子の量子化された距離場だとセル単位に丸まり、数 cm の差が消える。
        /// MakeTank は外周1セルだけが固体なので、内面の座標は解析的に分かる。
        /// </summary>
        static float WallDistance(FlowGrid g, Jellyfish j)
        {
            float c = g.CellSize;
            float loX = g.OriginX + c, hiX = g.OriginX + (g.Width - 1) * c;
            float loY = g.OriginY + c, hiY = g.OriginY + (g.Height - 1) * c;
            float loZ = g.OriginZ + c, hiZ = g.OriginZ + (g.Depth - 1) * c;
            return Math.Min(
                Math.Min(Math.Min(j.X - loX, hiX - j.X), Math.Min(j.Y - loY, hiY - j.Y)),
                Math.Min(j.Z - loZ, hiZ - j.Z));
        }

        static Jellyfish SpawnWithRepel(FlowGrid g, float repel)
        {
            var p = JellyParams.Default;
            p.WallRepelSpeed = repel;
            return new Jellyfish(p, 1.6f, 1.1f, 1.3f, g);
        }

        /// <summary>
        /// **反発があるとクラゲが壁面に貼り付かない。**
        ///
        /// 軸ごとの拒否は壁を貫通しないことしか保証しない。推力の向きは
        /// ペースメーカーの位置で決まっていて一定なので、壁へ向かった個体は
        /// 押し続けて壁際に張り付く。止水モードを入れて初めてむき出しになった
        /// （2026-08-16 の実機）。
        /// </summary>
        [Test]
        public void Wall_RepulsionKeepsTheJellyfishOffTheSurface()
        {
            var grid = MakeTank();

            // 止水で長く泳がせる。推力の向きは一定なので必ず壁へ達する
            var without = SpawnWithRepel(grid, 0f);
            var with = SpawnWithRepel(grid, 0.10f);
            for (int i = 0; i < 6000; i++)
            {
                without.Step(1f / 40f, 0f, 0f, 0f);
                with.Step(1f / 40f, 0f, 0f, 0f);
            }

            float dWithout = WallDistance(grid, without);
            float dWith = WallDistance(grid, with);

            // 既定の強さは「押し続ける推力（遊泳 0.04 m/s）に勝てること」で決めている。
            // 弱すぎると釣り合う位置が壁のすぐ際になり、張り付きと見分けがつかない
            Assert.Greater(dWith, dWithout + 0.03f,
                $"反発が効いていない（反発なし {dWithout:F3}m / 反発あり {dWith:F3}m）");
        }

        /// <summary>
        /// **既定では反発は無効。** 旋回が無い以上、押し戻しても釣り合うだけで
        /// 壁から離れられない（実機で 0.05 / 0.10 / 0.20 のいずれでも止まった）。
        /// 有効にすると「対処済み」に見えてしまうので、既定は 0 にしてある。
        /// 旋回（jelly_2 段2）が入ったらここを見直す。
        /// </summary>
        [Test]
        public void Wall_RepulsionIsDisabledByDefaultUntilTurningExists()
        {
            Assert.AreEqual(0f, JellyParams.Default.WallRepelSpeed,
                "旋回が無い状態で反発を既定有効にすると、対処済みに見えてしまう");
            Assert.AreEqual(0f, AquariumJellyfish.WallRepelChoices[0],
                "実機の初期状態も無効であること");
        }

        /// <summary>**反発があっても固体セルへは入らない。** 拒否と両立すること。</summary>
        [Test]
        public void Wall_RepulsionNeverPushesIntoASolid()
        {
            var grid = MakeTank();
            var jelly = SpawnWithRepel(grid, 0.12f);
            for (int i = 0; i < 4000; i++)
            {
                jelly.Step(1f / 40f, 0.5f, -0.5f, 0.5f);
                Assert.IsTrue(JellyBoundary.IsFluid(grid, jelly.X, jelly.Y, jelly.Z),
                    $"t={i} で固体セルに入った");
            }
        }

        /// <summary>
        /// **反発は水の中ほどでは効かない。** 帯の外では力学を変えないこと
        /// （効きっぱなしだと遊泳速度の較正が狂う）。
        /// </summary>
        [Test]
        public void Wall_RepulsionDoesNotActFarFromWalls()
        {
            var grid = MakeTank();
            var plain = SpawnWithRepel(grid, 0f);
            var repel = SpawnWithRepel(grid, 0.12f);

            // 中央付近にいる 400 ステップのあいだは同じ軌跡のはず
            for (int i = 0; i < 400; i++)
            {
                plain.Step(1f / 40f, 0f, 0f, 0f);
                repel.Step(1f / 40f, 0f, 0f, 0f);
            }
            Assert.AreEqual(plain.X, repel.X, 1e-6f, "壁から遠いのに軌跡が変わった");
            Assert.AreEqual(plain.Z, repel.Z, 1e-6f, "壁から遠いのに軌跡が変わった");
        }

        // ================= K2: dV/dt 噴流モデル（jelly_2 追記7） =================

        static JellyParams JetParams(float turn = 1.0f, float righting = 0.5f)
        {
            var p = JellyParams.Default;
            p.JetModel = true;
            p.TurnGain = turn;
            p.RightingGain = righting;
            // 内蔵ペースメーカーは 1 セルしか叩かないので、それ自体が非対称である。
            // 発火のさせ方は判定側が完全に決める（JellyParams.Pacemaker のコメント）
            p.Pacemaker = false;
            return p;
        }

        /// <summary>
        /// 発火のさせ方を差し替えられる走行。対称／片側／鏡像を同じ位相で比べる。
        /// mode: 0 = 対称（両端）, 1 = セル0 のみ, 2 = 鏡像（セル n/2 のみ）
        /// </summary>
        static Jellyfish RunPattern(JellyParams p, int mode, int steps)
        {
            var jelly = new Jellyfish(p, 0f, 0f, 0f);
            int n = p.RingCells;
            for (int t = 0; t < steps; t++)
            {
                if (t % p.PulsePeriodTicks == 0)
                {
                    if (mode == 0) { jelly.StimulateCell(0); jelly.StimulateCell(n / 2); }
                    else if (mode == 1) jelly.StimulateCell(0);
                    else jelly.StimulateCell(n / 2);
                }
                jelly.Step(1f / 40f, 0f, 0f, 0f);
            }
            return jelly;
        }

        /// <summary>
        /// **後半の平均傾き**（度）。位相に依らない統計量。
        ///
        /// 【スナップショットで測ってはいけない】復元トルクと回転抗力は振動子を
        /// 作るので、「N ステップ時点の傾き」は振動の位相を拾うだけになる。
        /// 復元係数を掃引すると 18.77 → 0.82 → 2.28 → 0.39 → 0.00 → 1.75 と
        /// **非単調に振れた**。瞬時の遊泳速度を平均として読んでいた件と同じ系統
        /// （3例目）。後半 steps/2 の平均で測る（prereg jelly_2 追記8）。
        /// </summary>
        static float MeanTilt(JellyParams p, int mode, int steps)
        {
            var jelly = new Jellyfish(p, 0f, 0f, 0f);
            int n = p.RingCells;
            int half = steps / 2;
            double sum = 0; int count = 0;
            for (int t = 0; t < steps; t++)
            {
                if (t % p.PulsePeriodTicks == 0)
                {
                    if (mode == 0) { jelly.StimulateCell(0); jelly.StimulateCell(n / 2); }
                    else if (mode == 1) jelly.StimulateCell(0);
                    else jelly.StimulateCell(n / 2);
                }
                jelly.Step(1f / 40f, 0f, 0f, 0f);
                if (t >= half) { sum += jelly.TiltDegrees; count++; }
            }
            return (float)(sum / count);
        }

        /// <summary>
        /// **M-K2a 対照: 対称に発火すれば軸は動かない。**
        ///
        /// トルクは Σ(amp·r̂) × 軸 なので、対称な発火では Σ(amp·r̂) = 0 になり
        /// **構造的に**トルクが出ない。復元も軸が真上なら 0。
        /// </summary>
        [Test]
        public void MK2a_SymmetricFiringDoesNotTurn()
        {
            float tilt = MeanTilt(JetParams(), 0, 800);
            Assert.Less(tilt, 0.5f,
                $"対称発火で軸が平均 {tilt:F4} 度傾いた（構造的に 0 のはず）");
        }

        /// <summary>**M-K2b 非対称で回頭する。** 絶対閾値ではなく対照との比で判定する。</summary>
        [Test]
        public void MK2b_AsymmetricFiringTurnsFarMoreThanTheControl()
        {
            float control = MeanTilt(JetParams(), 0, 800);
            float oneSided = MeanTilt(JetParams(), 1, 800);

            // K1 で「片側 0.963 度 対 両側 0.612 度」という区別のつかない結果に
            // なった経験から、絶対閾値ではなく対照との比で置く（追記7 A7.3）
            Assert.Greater(oneSided, Math.Max(control * 10f, 1.0f),
                $"片側 {oneSided:F3} 度 / 対照 {control:F4} 度。対照の10倍に届かない");
        }

        /// <summary>**M-K2c 鏡像対称。** 反対側のセルを叩けば逆向きに同じだけ回る。</summary>
        [Test]
        public void MK2c_MirroredFiringTurnsTheOppositeWay()
        {
            float ma = MeanTilt(JetParams(), 1, 800);
            float mb = MeanTilt(JetParams(), 2, 800);
            var a = RunPattern(JetParams(), 1, 800);
            var b = RunPattern(JetParams(), 2, 800);

            // 傾きの大きさは一致し、傾く向き（軸の水平成分）は反転する
            Assert.AreEqual(ma, mb, ma * 0.10f,
                $"鏡像で傾きの大きさが違う（{ma:F3} 対 {mb:F3}）");
            Assert.Less(a.Posture.AxisX * b.Posture.AxisX + a.Posture.AxisZ * b.Posture.AxisZ, 0f,
                "鏡像で傾く向きが反転していない");
        }

        /// <summary>
        /// **M-K2d 推力が軸に沿う。** 止水での変位の向きが軸の向きと一致すること。
        /// 「鉛直に何 m 進むか」にしないのは、magic number を置かずに
        /// 「推力の向きは傘の軸」をそのまま検証できるから（追記7 A7.3）。
        /// </summary>
        [Test]
        public void MK2d_DisplacementFollowsTheAxis()
        {
            // 復元を切って軸を傾けたまま保つ。片側発火で傾けてから直進させる
            var p = JetParams(turn: 1.0f, righting: 0f);
            var jelly = new Jellyfish(p, 0f, 0f, 0f);
            int n = p.RingCells;

            for (int t = 0; t < 400; t++)   // 片側で傾ける
            {
                if (t % p.PulsePeriodTicks == 0) jelly.StimulateCell(0);
                jelly.Step(1f / 40f, 0f, 0f, 0f);
            }
            Assert.Greater(jelly.TiltDegrees, 5f, "傾きが足りず判定にならない");

            float x0 = jelly.X, y0 = jelly.Y, z0 = jelly.Z;
            float ax = jelly.Posture.AxisX, ay = jelly.Posture.AxisY, az = jelly.Posture.AxisZ;
            for (int t = 0; t < 800; t++)   // 対称発火で直進
            {
                if (t % p.PulsePeriodTicks == 0) { jelly.StimulateCell(0); jelly.StimulateCell(n / 2); }
                jelly.Step(1f / 40f, 0f, 0f, 0f);
            }

            float dx = jelly.X - x0, dy = jelly.Y - y0, dz = jelly.Z - z0;
            float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            Assert.Greater(len, 1e-4f, "動いていないので向きが判定できない");

            float cos = (dx * ax + dy * ay + dz * az) / len;
            float angle = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, cos))) * 180.0 / Math.PI);
            Assert.Less(angle, 20f, $"変位が軸から {angle:F1} 度ずれている");
        }

        /// <summary>
        /// **M-K2e 神経は軸を読まない。** 線引きの核。
        ///
        /// 初期姿勢を変えてもリングの状態が一字一句同じなら、神経は姿勢を
        /// 参照していない。参照できないなら**何かへ向けて操舵することは
        /// 原理的に不可能**であり、grep より強い保証になる（追記7 A7.3）。
        /// </summary>
        [Test]
        public void MK2e_TheNerveNeverReadsTheAxis()
        {
            // 復元を切る。入れると両者が同じ平衡姿勢へ収束してしまい、
            // 「姿勢が違う状態で神経を回した」ことにならない（空の検証になる）
            var p = JetParams(righting: 0f);
            var upright = new Jellyfish(p, 0f, 0f, 0f);
            var tilted = new Jellyfish(p, 0f, 0f, 0f);

            // 軸を大きく倒す。**積分を通して**倒すので「軸は代入されない」が保たれる
            for (int k = 0; k < 20; k++) tilted.NudgeForTest(0f, 0f, 2f, 1f / 40f);

            // 走らせる前に、姿勢が実際に違うことを確かめる（空の検証の防止）
            Assert.Greater(Math.Abs(tilted.TiltDegrees - upright.TiltDegrees), 10f,
                $"姿勢の差が小さすぎる（{upright.TiltDegrees:F2} 対 {tilted.TiltDegrees:F2}）");

            for (int t = 0; t < 600; t++)
            {
                if (t % p.PulsePeriodTicks == 0) { upright.StimulateCell(0); tilted.StimulateCell(0); }
                upright.Step(1f / 40f, 0f, 0f, 0f);
                tilted.Step(1f / 40f, 0f, 0f, 0f);
            }

            // 走らせたあとも姿勢は違うまま（神経が違う姿勢を「見る機会」があった）
            Assert.Greater(Math.Abs(tilted.TiltDegrees - upright.TiltDegrees), 1f,
                "走行後に姿勢の差が消えている。神経が姿勢を見る機会がなかった");

            Assert.AreEqual(upright.Ring.ComputeContentHash(), tilted.Ring.ComputeContentHash(),
                "初期姿勢を変えたらリングの状態が変わった = 神経が軸を読んでいる");
        }

        /// <summary>**M-K2f 決定論。** 噴流モデルでも同一入力から同一状態へ。</summary>
        [Test]
        public void MK2f_JetModelIsDeterministic()
        {
            ulong Run(float turn, int steps) => RunPattern(JetParams(turn), 1, steps).ComputeContentHash();

            Assert.AreEqual(Run(1.0f, 400), Run(1.0f, 400), "2回の実行が違う状態になった");
            Assert.AreNotEqual(Run(1.0f, 400), Run(1.0f, 401), "ステップ数が違うのに同じ状態");
            Assert.AreNotEqual(Run(1.0f, 400), Run(2.0f, 400), "旋回係数が違うのに同じ状態");
        }

        /// <summary>
        /// **M-K2g 復元の有無で M-K2b が変わらない。**
        ///
        /// 復元が強すぎると回頭を打ち消し、判定が復元係数依存になる。
        /// K1 で既定 0.06 が「反発ありと無しで差が出ない」となったのと同じ罠の
        /// 再発防止（追記7 A7.3）。落ちる係数は探索範囲の上限として記録する。
        /// </summary>
        [Test]
        public void MK2g_TheVerdictDoesNotDependOnTheRightingGain()
        {
            // 掃引で M-K2b が合格する範囲は復元 0〜32（追記8）。既定 0.5 の
            // 上下に十分な幅がある。境界（32〜64）は K4 の探索上限として登録済み
            foreach (float righting in new[] { 0f, 0.5f, 8f, 32f })
            {
                float control = MeanTilt(JetParams(1.0f, righting), 0, 800);
                float oneSided = MeanTilt(JetParams(1.0f, righting), 1, 800);
                Assert.Greater(oneSided, Math.Max(control * 10f, 1.0f),
                    $"復元 {righting:F2} で M-K2b が落ちた（片側 {oneSided:F3} / 対照 {control:F4}）");
            }
        }

        /// <summary>
        /// **傘の回転が局所軸を姿勢へ写すこと。**
        ///
        /// メッシュは局所 +Y が頂点、局所 +X が解剖学的な 0°。
        /// `Quaternion.LookRotation` の引数の向きは導出だけで決めず、ここで固定する
        /// （Unity は right = Cross(up, forward) なので forward = -(軸 × 基準)）。
        /// 間違えると傘が姿勢と別の向きを向き、実機でしか気づけない。
        /// </summary>
        [Test]
        public void MK2h_BellRotationMapsLocalAxesOntoThePosture()
        {
            var p = JetParams(righting: 0f);
            var jelly = new Jellyfish(p, 0f, 0f, 0f);

            // 姿勢を傾ける（積分を通す）。真上のままだと恒等写像で空の検証になる
            for (int k = 0; k < 15; k++) jelly.NudgeForTest(0.7f, 0.3f, 1.5f, 1f / 40f);
            Assert.Greater(jelly.TiltDegrees, 10f, "姿勢が傾いていないと判定にならない");

            var rot = BlockField.Aquarium.JellyfishView.PostureRotation(jelly);
            var post = jelly.Posture;

            var mappedUp = rot * UnityEngine.Vector3.up;
            var mappedRight = rot * UnityEngine.Vector3.right;

            Assert.AreEqual(post.AxisX, mappedUp.x, 1e-4f, "局所 +Y が軸に写っていない");
            Assert.AreEqual(post.AxisY, mappedUp.y, 1e-4f, "局所 +Y が軸に写っていない");
            Assert.AreEqual(post.AxisZ, mappedUp.z, 1e-4f, "局所 +Y が軸に写っていない");

            Assert.AreEqual(post.RefX, mappedRight.x, 1e-4f, "局所 +X が基準に写っていない");
            Assert.AreEqual(post.RefY, mappedRight.y, 1e-4f, "局所 +X が基準に写っていない");
            Assert.AreEqual(post.RefZ, mappedRight.z, 1e-4f, "局所 +X が基準に写っていない");
        }

        /// <summary>
        /// **M-K2i 噴流モデルでも目標速度で泳ぐこと。**
        ///
        /// 【なぜ後から足したか】K2 の判定7件は全部通ったのに、実機では
        /// 「止水でクラゲが移動しない」となった。実測は **0.001067 m/s
        /// （目標 0.04 の 2.7%、1.07mm/s）** で、目には止まって見える。
        /// 換算係数を 2D リム収縮のモデルで較正したまま噴流の速度へ掛けており、
        /// **桁が違うのに正規化していなかった**。
        ///
        /// M-K2d は変位の**向き**しか見ておらず、大きさの下限は「0 でないこと」を
        /// 保証する 1e-4 m だけだった。**変位が出ることと、視認できる速度で
        /// 出ることは別**である（2026-08-16 の指摘）。大きさを見る判定を置く。
        /// </summary>
        [Test]
        public void MK2i_JetModelSwimsAtTheTargetSpeed()
        {
            foreach (float target in new[] { 0.02f, 0.04f, 0.1f })
            {
                var p = JellyParams.Default;
                p.JetModel = true;
                p.SwimSpeed = target;
                var jelly = new Jellyfish(p, 0f, 0f, 0f);

                for (int t = 0; t < 800; t++) jelly.Step(1f / 40f, 0f, 0f, 0f);   // 過渡
                float speed = SelfPropelledPath(jelly, 800) / (800f / 40f);

                Assert.AreEqual(target, speed, target * 0.05f,
                    $"噴流モデルの遊泳速度が目標 {target} m/s に対し {speed:F6} m/s");
            }
        }

        /// <summary>
        /// **2つのモデルの遊泳速度が一致すること。** 実機で切り替えたときに
        /// 「速さが変わった」と見えないための担保。
        /// </summary>
        [Test]
        public void MK2i_BothModelsSwimAtTheSameSpeed()
        {
            float Measure(bool jet)
            {
                var p = JellyParams.Default;
                p.JetModel = jet;
                var j = new Jellyfish(p, 0f, 0f, 0f);
                for (int t = 0; t < 800; t++) j.Step(1f / 40f, 0f, 0f, 0f);
                return SelfPropelledPath(j, 800) / (800f / 40f);
            }

            float a = Measure(false), b = Measure(true);
            Assert.AreEqual(a, b, a * 0.05f,
                $"2Dリム {a:F5} m/s に対し噴流 {b:F5} m/s。実機で速さが変わって見える");
        }

        // ================= 沈降（jelly_2 追記10） =================

        static JellyParams SinkParams(float ratio)
        {
            var p = JellyParams.Default;
            p.JetModel = true;
            p.SinkRatio = ratio;
            return p;
        }

        /// <summary>
        /// **M-K2j 拍動を止めると沈む。** 沈降速度と一致すること。
        /// 「拍動＝沈まないための努力」の前提。
        /// </summary>
        [Test]
        public void MK2j_StoppingThePulseMakesItSink()
        {
            var p = SinkParams(0.90f);
            var jelly = new Jellyfish(p, 0f, 0f, 0f);
            jelly.PacemakerEnabled = false;

            // 残っている推力を抜く
            for (int t = 0; t < 400; t++) jelly.Step(1f / 40f, 0f, 0f, 0f);

            float y0 = jelly.Y;
            for (int t = 0; t < 400; t++) jelly.Step(1f / 40f, 0f, 0f, 0f);
            float rate = (y0 - jelly.Y) / (400f / 40f);

            float expected = p.SwimSpeed * p.SinkRatio;
            Assert.AreEqual(expected, rate, expected * 0.10f,
                $"沈降 {rate:F5} m/s が想定 {expected:F5} m/s と違う");
        }

        /// <summary>
        /// **M-K2k 拍動していれば沈まない。** これが「拍動＝努力」の内容である。
        /// 拍動中の正味の高さ変化が、同じ時間の沈降だけの場合の 20% 未満。
        /// </summary>
        [Test]
        public void MK2k_PulsingKeepsItFromSinking()
        {
            var p = SinkParams(0.90f);

            var pulsing = new Jellyfish(p, 0f, 0f, 0f);
            for (int t = 0; t < 800; t++) pulsing.Step(1f / 40f, 0f, 0f, 0f);   // 姿勢の過渡
            float y0 = pulsing.Y;
            for (int t = 0; t < 800; t++) pulsing.Step(1f / 40f, 0f, 0f, 0f);

            // 【上昇と沈降を区別する】最初は Math.Abs で「変化＝沈んだ」と
            // 扱っており、**上昇 0.599m を「沈んだ」と報告して落ちた**。
            // 主張は「沈まない」であって「動かない」ではない
            float rise = pulsing.Y - y0;
            float sinkOnly = p.SwimSpeed * p.SinkRatio * (800f / 40f);

            Assert.Greater(rise, -sinkOnly * 0.20f,
                $"拍動しても {-rise:F4}m 沈んだ（沈降だけなら {sinkOnly:F4}m）。" +
                "拍動が沈降に勝てていない");
        }

        /// <summary>
        /// **M-K2l 沈降が K2 の判定を壊さない。**
        /// 新しい世界法則を足したときに既存の判定が静かに壊れていないかを、
        /// M-K2g と同じ掃引の形で確かめる。
        /// </summary>
        [Test]
        public void MK2l_SinkingDoesNotBreakTheOtherVerdicts()
        {
            foreach (float ratio in new[] { 0f, 0.5f, 0.9f, 1.1f })
            {
                // M-K2i: 目標速度で泳ぐ（沈降を除いた経路長で）
                var p = SinkParams(ratio);
                var jelly = new Jellyfish(p, 0f, 0f, 0f);
                for (int t = 0; t < 800; t++) jelly.Step(1f / 40f, 0f, 0f, 0f);
                float speed = SelfPropelledPath(jelly, 800) / (800f / 40f);
                Assert.AreEqual(p.SwimSpeed, speed, p.SwimSpeed * 0.05f,
                    $"沈降 {ratio:P0} で遊泳速度が {speed:F5} m/s になった");

                // M-K2d: 変位の向きが軸と一致（沈降を除いて見る）
                var q = SinkParams(ratio);
                q.Pacemaker = false;
                q.RightingGain = 0f;
                var tilted = new Jellyfish(q, 0f, 0f, 0f);
                for (int t = 0; t < 400; t++)
                {
                    if (t % q.PulsePeriodTicks == 0) tilted.StimulateCell(0);
                    tilted.Step(1f / 40f, 0f, 0f, 0f);
                }
                float x0 = tilted.X, h0 = HeightWithoutSinking(tilted), z0 = tilted.Z;
                float ax = tilted.Posture.AxisX, ay = tilted.Posture.AxisY, az = tilted.Posture.AxisZ;
                int n = q.RingCells;
                for (int t = 0; t < 800; t++)
                {
                    if (t % q.PulsePeriodTicks == 0)
                    { tilted.StimulateCell(0); tilted.StimulateCell(n / 2); }
                    tilted.Step(1f / 40f, 0f, 0f, 0f);
                }
                float dx = tilted.X - x0, dy = HeightWithoutSinking(tilted) - h0, dz = tilted.Z - z0;
                float len = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                Assert.Greater(len, 1e-3f, $"沈降 {ratio:P0} で動いていない");

                float cos = (dx * ax + dy * ay + dz * az) / len;
                float angle = (float)(Math.Acos(Math.Max(-1f, Math.Min(1f, cos))) * 180.0 / Math.PI);
                Assert.Less(angle, 20f, $"沈降 {ratio:P0} で変位が軸から {angle:F1} 度ずれた");
            }
        }
}
}
