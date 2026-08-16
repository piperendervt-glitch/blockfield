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
    }
}
