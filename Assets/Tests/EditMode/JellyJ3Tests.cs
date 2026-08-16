using System;
using System.Collections.Generic;
using System.Linq;
using BlockField.SimCore.Excitable;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// jelly_1 J3: ペースメーカー（自発拍動）+ 外部刺激の割り込み。
    /// M-J3a / M-J3b / M-J3c。
    ///
    /// 【実行条件は必ず明示する】(prereg 追記6 の教訓)
    /// プロトタイプの `swim` は `stims` の既定値が `((0,0),)` で、
    /// `pace` だけ指定した呼び出しに**セル0 への単発刺激が混ざっていた**。
    /// そのせいで「ペースメーカー位置によって遊泳距離が変わる」という、
    /// モデルの性質に見える現象（誤差 1.8°）が prereg の再現目標に
    /// 記録されていた。純粋版ではリングの回転対称性どおり誤差 0.00° になる。
    /// **本ファイルの走行は刺激を毎回明示する。**
    /// </summary>
    public class JellyJ3Tests
    {
        const int N = 16;
        const double Drag = 0.1;
        const int TPace = 40;

        /// <summary>
        /// ペースメーカーと外部刺激を与えて走らせ、各ステップ後の位置を返す。
        /// `stims` は既定値を持たせない——呼び出し側に必ず書かせるため。
        /// </summary>
        static (double[] xs, double[] ys) Trajectory(
            int pace, int tPace, (int cell, int tick)[] stims, int steps,
            double g = 0.85, double drag = Drag, int r0 = 14)
        {
            var p = ExcitableParams.Default;
            p.RefractoryTicks = r0;
            p.Attenuation = g;

            var s = new RingSwimmer(N);
            var xs = new double[steps];
            var ys = new double[steps];
            for (int t = 0; t < steps; t++)
            {
                foreach (var (cell, tick) in stims)
                {
                    if (tick == t) s.TryStimulate(cell, p);
                }
                if (t % tPace == 0) s.TryStimulate(pace, p);
                s.Step(p, drag);
                xs[t] = s.X; ys[t] = s.Y;
            }
            return (xs, ys);
        }

        static double Heading(double x, double y)
        {
            double deg = Math.Atan2(y, x) * 180.0 / Math.PI;
            return deg < 0 ? deg + 360.0 : deg;
        }

        static double AngleError(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        /// <summary>区間 [a, b) の平均速度。累積でなく区間で測る（追記6-4）。</summary>
        static double IntervalSpeed(double[] xs, double[] ys, int a, int b)
        {
            double dx = xs[b - 1] - xs[a - 1];
            double dy = ys[b - 1] - ys[a - 1];
            return Math.Sqrt(dx * dx + dy * dy) / (b - a);
        }

        // ================= M-J3a: 持続遊泳 =================

        /// <summary>
        /// **単一ペースメーカーから持続遊泳が生じ、速度が一定であること。**
        ///
        /// 判定（追記6-4）: 区間 200-400 / 400-800 / 800-1600 の平均速度が
        /// 互いに **±5% 以内**。実測は3区間とも **0.298032**（乖離 0.0000%）。
        ///
        /// **累積距離でなく区間で測る。** 立ち上がり（t&lt;200）は速度が抗力と
        /// つり合うまで加速するので、累積の比は必ず線形からずれる
        /// （純粋値では −16.2%）。追記1 の累積 ±15% 基準は破棄した。
        /// </summary>
        [Test]
        public void MJ3a_IntervalSpeedIsConstantAfterTheTransient()
        {
            var (xs, ys) = Trajectory(8, TPace, Array.Empty<(int, int)>(), 1600);

            double[] v =
            {
                IntervalSpeed(xs, ys, 200, 400),
                IntervalSpeed(xs, ys, 400, 800),
                IntervalSpeed(xs, ys, 800, 1600),
            };

            foreach (double s in v)
            {
                Assert.AreEqual(0.298032, s, 1e-6, "プロトタイプ（純粋版）の実測と一致するはず");
            }
            double deviation = v.Max() / v.Min() - 1.0;
            Assert.Less(deviation, 0.05, $"区間平均速度の乖離 {deviation * 100:F4}%（許容 5%）");
            Assert.Less(deviation, 1e-9, "実測は 0.0000% なので、丸め以上のずれが出たら何かが変わった");
        }

        /// <summary>
        /// **4方位すべてで、期待方向へまっすぐ進むこと。**
        ///
        /// リングは回転対称なので、ペースメーカー位置を変えても
        /// 距離は同じで方向だけが回るのが正しい。
        /// **誤差 0.00° / 距離は全方位 476.8（t=1600）。**
        /// 旧記載の「1.8° 以内」は既定引数の汚染によるもので、物理ではない（追記6-2）。
        /// </summary>
        [Test]
        public void MJ3a_AllFourPacemakerPositionsSwimStraightWithIdenticalSpeed()
        {
            foreach (int pace in new[] { 0, 4, 8, 12 })
            {
                var (xs, ys) = Trajectory(pace, TPace, Array.Empty<(int, int)>(), 1600);
                double heading = Heading(xs[1599], ys[1599]);
                double expected = (360.0 * pace / N + 180.0) % 360.0;
                double dist = Math.Sqrt(xs[1599] * xs[1599] + ys[1599] * ys[1599]);

                Assert.Less(AngleError(heading, expected), 5.0,
                    $"pace={pace}: 進行方向 {heading:F1}°（期待 {expected:F1}°）");
                Assert.Less(AngleError(heading, expected), 1e-9,
                    $"pace={pace}: 回転対称なので厳密に一致するはず");
                Assert.AreEqual(476.8, dist, 0.05,
                    $"pace={pace}: 距離は位置に依らないはず");
            }
        }

        /// <summary>
        /// **48シードで速度が一定であること。**（追記6-5 の定義）
        ///
        /// ペースメーカー位置 0〜15 一様 / g ∈ [0.75, 0.92] / drag ∈ [0.05, 0.2]。
        /// 乱数列は M-J2c と同一の LCG。実測は最大乖離 **0.0000%**。
        /// </summary>
        [Test]
        public void MJ3a_IntervalSpeedIsConstantAcrossFortyEightSeeds()
        {
            double worst = 0;
            for (uint seed = 1000; seed < 1048; seed++)
            {
                var r = JellyJ2Tests.RngStreamFor(seed).GetEnumerator();
                r.MoveNext(); int pace = (int)(r.Current * 16);
                r.MoveNext(); double g = 0.75 + r.Current * 0.17;
                r.MoveNext(); double drag = 0.05 + r.Current * 0.15;

                var (xs, ys) = Trajectory(
                    pace, TPace, Array.Empty<(int, int)>(), 1600, g: g, drag: drag);
                double[] v =
                {
                    IntervalSpeed(xs, ys, 200, 400),
                    IntervalSpeed(xs, ys, 400, 800),
                    IntervalSpeed(xs, ys, 800, 1600),
                };
                double dev = v.Max() / v.Min() - 1.0;
                worst = Math.Max(worst, dev);
                Assert.Less(dev, 0.05,
                    $"seed={seed} pace={pace} g={g:F4} drag={drag:F4}: 乖離 {dev * 100:F4}%");
            }
            // 実測の最大乖離は 4.32e-7（= 0.0000432%）。**厳密な 0 ではない。**
            // 掃引ハーネスが小数4桁の百分率で表示していたため「0.0000%」に
            // 丸められて見えていた（prereg 追記7）。判定の 5% に対しては
            // 5桁の余裕があるが、「ゼロ」と書くと次に誰かが 1e-9 で固定して落ちる
            Assert.Less(worst, 1e-5,
                $"48シードの最大乖離 {worst:E3}（実測 4.32e-7）");
        }

        // ================= M-J3b: 偏向と復帰 =================

        /// <summary>
        /// **遊泳中の側方刺激で進路が偏向し、刺激が消えると元の進路へ復帰すること。**
        ///
        /// ペースメーカー cell8（進行方向 360°）で泳いでいるところへ、
        /// t=100 に 90° 側（cell4）から刺激を入れる。
        /// プロトタイプ実測 **360.0° → 315.1° → 360.0°**（汚染されていない値）。
        ///
        /// heading 変数を持たないのに元の進路へ戻るのは、
        /// **進路がペースメーカー位置だけで決まっている**からである。
        /// 刺激は一時的に推力の向きを変えるが、記憶されない。
        /// </summary>
        [Test]
        public void MJ3b_LateralPokeDeflectsThenTheCourseResumes()
        {
            var (xs, ys) = Trajectory(8, TPace, new[] { (4, 100) }, 400);

            double HeadAt(int a, int b) => Heading(xs[b] - xs[a], ys[b] - ys[a]);

            double before = HeadAt(79, 99);
            double during = HeadAt(99, 139);
            double after = HeadAt(299, 399);

            Assert.Less(AngleError(before, 360.0), 0.05, $"刺激前は 360° のはず（{before:F1}°）");
            Assert.Less(AngleError(during, 315.1), 0.05,
                $"刺激中は 90° 側から逸れて 315.1° のはず（{during:F1}°）");
            Assert.Less(AngleError(after, 360.0), 0.05,
                $"刺激が消えたら 360° へ復帰するはず（{after:F1}°）");

            // 偏向が「90°の反対側へ」であることを符号で確かめる
            Assert.Less(during, before, "偏向は 90° から離れる向き（角度が小さくなる側）のはず");
        }

        /// <summary>
        /// 刺激を入れなければ進路は終始変わらないこと（M-J3b の対照）。
        /// 偏向が刺激によるものだと言うには、刺激なしで曲がらないことが要る。
        /// </summary>
        [Test]
        public void MJ3b_WithoutThePokeTheCourseNeverChanges()
        {
            var (xs, ys) = Trajectory(8, TPace, Array.Empty<(int, int)>(), 400);
            double HeadAt(int a, int b) => Heading(xs[b] - xs[a], ys[b] - ys[a]);

            foreach (var (a, b) in new[] { (79, 99), (99, 139), (299, 399) })
            {
                Assert.Less(AngleError(HeadAt(a, b), 360.0), 1e-9,
                    $"t={a}-{b}: 刺激がないのに進路が変わった");
            }
        }

        // ================= M-J3c: 決定論 =================

        /// <summary>
        /// **同一入力から2回走らせて同じ状態に到達すること。**
        /// ペースメーカーは毎拍動で状態を書き換えるので、J1 より長い履歴を通した検証になる。
        /// </summary>
        [Test]
        public void MJ3c_PacemakerSwimIsDeterministic()
        {
            // 【805 ステップにする理由】T=40 なので t=800 が拍動、波は t=801〜808 に走る。
            // 800 ちょうどで止めると**場が静止している**ので、ペースメーカー位置が
            // 違っても同じ全ゼロ状態になり、「ハッシュが状態を見ていない」のと
            // 区別がつかない。J1 でも同じ罠を踏んだ（追記3 の末尾）。
            const int k_Steps = 805;

            (ulong hash, double x, double y) Run(int pace, (int cell, int tick)[] stims)
            {
                var p = ExcitableParams.Default;
                var s = new RingSwimmer(N);
                for (int t = 0; t < k_Steps; t++)
                {
                    foreach (var (cell, tick) in stims)
                    {
                        if (tick == t) s.TryStimulate(cell, p);
                    }
                    if (t % TPace == 0) s.TryStimulate(pace, p);
                    s.Step(p, Drag);
                }
                return (s.Field.ComputeContentHash(), s.X, s.Y);
            }

            var poke = new[] { (4, 100) };
            foreach (int pace in new[] { 0, 8 })
            {
                Assert.AreEqual(Run(pace, Array.Empty<(int, int)>()).hash,
                                Run(pace, Array.Empty<(int, int)>()).hash,
                                $"pace={pace}: 2回の実行が違う状態になった");
                Assert.AreEqual(Run(pace, poke).hash, Run(pace, poke).hash,
                                $"pace={pace}（側方刺激あり）: 2回の実行が違う状態になった");
            }
            Assert.AreNotEqual(Run(0, Array.Empty<(int, int)>()).hash,
                               Run(8, Array.Empty<(int, int)>()).hash,
                "ペースメーカー位置が違うのに同じハッシュ（波が走っている時点で比べること）");

            // 【場は覚えず、体が覚える】t=100 の側方刺激は t=805 の**場**には
            // 何も残さない（E・R・A は拍動の位相だけで決まる）。
            // 残るのは**体の位置**である。興奮性媒質が痕跡を持たないこと（追記3）と、
            // それでも刺激の影響が世界に残ることが両立している。
            var plain = Run(8, Array.Empty<(int, int)>());
            var poked = Run(8, poke);
            Assert.AreEqual(plain.hash, poked.hash,
                "場は側方刺激を覚えていないはず（興奮性媒質に痕跡は残らない）");
            Assert.Greater(Math.Abs(plain.y - poked.y), 1.0,
                "体の位置には残るはず（横に押された分だけずれる）");
        }

        /// <summary>
        /// **発火セル列がプロトタイプと一致すること。**（移植の正しさ）
        ///
        /// ペースメーカー cell8 / T=40 の最初の1拍動。
        /// t=0 に刺激が入り、t=1 から左右へ広がって t=8 で対蹠（cell 0）にて対消滅する。
        /// 刺激セル自身は `fired` に現れない（直接書き込まれるため）。
        /// </summary>
        [Test]
        public void MJ3c_FiringSequenceMatchesThePrototype()
        {
            var p = ExcitableParams.Default;
            var s = new RingSwimmer(N);

            var expected = new[]
            {
                new[] { 7, 9 }, new[] { 6, 10 }, new[] { 5, 11 }, new[] { 4, 12 },
                new[] { 3, 13 }, new[] { 2, 14 }, new[] { 1, 15 }, new[] { 0 },
            };

            s.TryStimulate(8, p);
            for (int t = 0; t < 8; t++)
            {
                s.Step(p, Drag);
                CollectionAssert.AreEqual(expected[t], s.Field.LastFired.ToArray(),
                    $"t={t + 1} の発火セル");
            }
            for (int t = 8; t < 40; t++)
            {
                s.Step(p, Drag);
                CollectionAssert.IsEmpty(s.Field.LastFired.ToArray(),
                    $"t={t + 1}: 次の拍動まで発火しないはず");
            }
        }

        // ================= 観察（判定ではない） =================

        /// <summary>
        /// **拍動周期 T を R₀ に近づけると、T = R₀ に「谷」がある。**（観察・追記5 の限定2 の延長）
        ///
        /// jelly_2 で拍動周期を遺伝子にするなら、これが探索空間の境界になる。
        /// 判定ではないが、境界が動いたら気づけるようテストにしておく。
        ///
        /// | T | 刺激が入る | 波が出る | 平均速度 |
        /// |---|---|---|---|
        /// | 16 | 100/100 | 100/100 | 0.745 |
        /// | **15** | 107/107 | 107/107 | **0.792（最速）** |
        /// | **14 (= R₀)** | **115/115** | **58/115** | **0.428（谷）** |
        /// | 13 | 62/124 | 62/124 | 0.462 |
        ///
        /// **T = R₀ では刺激は毎回入るのに、半分の拍動が波を出さない。**
        /// 刺激セル自身は R₀ ステップで回復するが、**両隣は1ステップ遅れて回復する**ので、
        /// 拍動の瞬間に隣が不応期のままだと波が伝わらない（追記5 の限定2）。
        /// 「刺激が入った」と「波が出た」は別物である。
        /// </summary>
        [Test]
        public void Observation_PeriodEqualToRefractoryIsADeadZone()
        {
            (int landed, int waves, double speed) Beat(int tPace)
            {
                var p = ExcitableParams.Default;
                var s = new RingSwimmer(N);
                int landed = 0, waves = 0;
                bool check = false;
                var xs = new double[1600]; var ys = new double[1600];
                for (int t = 0; t < 1600; t++)
                {
                    if (t % tPace == 0 && s.TryStimulate(8, p)) { landed++; check = true; }
                    s.Step(p, Drag);
                    if (check) { if (s.Field.LastFired.Count > 0) waves++; check = false; }
                    xs[t] = s.X; ys[t] = s.Y;
                }
                return (landed, waves, IntervalSpeed(xs, ys, 800, 1600));
            }

            var t15 = Beat(15);
            var t14 = Beat(14);
            var t13 = Beat(13);

            Assert.AreEqual(t15.landed, t15.waves, "T=15 は入った刺激がすべて波になる");
            Assert.AreEqual(115, t14.landed, "T=14 では刺激は毎回入る");
            Assert.AreEqual(58, t14.waves, "T=14 では半分しか波にならない");
            Assert.Less(t14.speed, t15.speed, "T=14 は T=15 より遅い");
            Assert.Less(t14.speed, t13.speed, "T=14 は T=13 よりも遅い（ここが谷）");
            Assert.AreEqual(0.791865, t15.speed, 1e-5, "最速は T = R₀ + 1");
        }

        /// <summary>
        /// **複数ペースメーカーは線形合成にならない。**（観察・追記5 の限定1 の延長）
        ///
        /// 各ペースメーカーを単独で走らせた変位のベクトル和と比べると、
        /// 実際の移動距離は **55〜67%** にしかならない。
        /// 原因は重ね合わせの非線形性（各セルは不応期のあいだ1度しか発火できない）。
        ///
        /// 対蹠に同周期で2つ置くと**完全に相殺して動かない**（距離 0）。
        /// jelly_2 で複数の拍動源や刺激が同時に来る設計をするなら、
        /// 線形合成を前提にしてはいけない。
        /// </summary>
        [Test]
        public void Observation_MultiplePacemakersDoNotSuperposeLinearly()
        {
            (double x, double y) Run((int cell, int period)[] paces)
            {
                var p = ExcitableParams.Default;
                var s = new RingSwimmer(N);
                for (int t = 0; t < 1600; t++)
                {
                    foreach (var (cell, period) in paces)
                    {
                        if (t % period == 0) s.TryStimulate(cell, p);
                    }
                    s.Step(p, Drag);
                }
                return (s.X, s.Y);
            }

            // 対蹠・同周期は完全相殺
            var opposed = Run(new[] { (8, 40), (0, 40) });
            Assert.Less(Math.Sqrt(opposed.x * opposed.x + opposed.y * opposed.y), 1e-9,
                "対蹠に同周期で置くと相殺するはず");

            // 周期の違う2つ: 単独の和より小さい
            var pair = new[] { (8, 40), (2, 43) };
            var actual = Run(pair);
            double px = 0, py = 0;
            foreach (var one in pair)
            {
                var r = Run(new[] { one });
                px += r.x; py += r.y;
            }
            double actualDist = Math.Sqrt(actual.x * actual.x + actual.y * actual.y);
            double linearDist = Math.Sqrt(px * px + py * py);

            Assert.Less(actualDist, linearDist * 0.8,
                $"実測 {actualDist:F1} は線形合成の予測 {linearDist:F1} より大きく下回るはず");
            Assert.Greater(AngleError(Heading(actual.x, actual.y), Heading(px, py)), 5.0,
                "方向も線形合成の予測から外れるはず");
        }
    }
}
