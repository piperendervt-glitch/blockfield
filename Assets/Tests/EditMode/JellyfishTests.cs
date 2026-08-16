using System;
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

        /// <summary>止水（流れゼロ）で N ステップ進めたときの移動距離。</summary>
        static float SwimDistanceInStillWater(Jellyfish jelly, int steps)
        {
            float x0 = jelly.X, y0 = jelly.Y, z0 = jelly.Z;
            for (int i = 0; i < steps; i++) jelly.Step(1f / 40f, 0f, 0f, 0f);
            float dx = jelly.X - x0, dy = jelly.Y - y0, dz = jelly.Z - z0;
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

            // 自力遊泳は水平のみなので、鉛直の差はそのまま流れの寄与
            Assert.AreEqual(0f, still.Y - 1.1f, 1e-5f, "止水では鉛直に動かないはず");
            Assert.AreEqual(0.05f * steps * dt, carried.Y - 1.1f, 1e-3f,
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

            Assert.AreEqual(1.1f, jelly.Y, 1e-6f, "止水で鉛直に動いている");
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
            var with = SpawnWithRepel(grid, JellyParams.Default.WallRepelSpeed);
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
}
}
