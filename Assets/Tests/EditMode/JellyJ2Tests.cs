using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BlockField.SimCore.Excitable;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// jelly_1 J2: 収縮 → 推力（擬似流体）。M-J2a / M-J2b。
    ///
    /// 【中心的主張】逃避方向が「伝播の時間差 + 減衰勾配」だけから**創発**すること。
    /// 刺激の方向を求める計算を一切持たずに、刺激と正反対へ逃げる。
    ///
    /// 【実行条件を必ず書く】移動距離も到達方向も steps に依存する
    /// （推力が止まったあとも抗力で減衰しながら残速度が位置へ積み上がる）。
    /// prereg §7.2 の実行条件表と、追記4 を参照。
    /// </summary>
    public class JellyJ2Tests
    {
        const int N = 16;
        const double Drag = 0.1;

        /// <summary>
        /// 到達方向の読み出し。**測定側にしか置かない**（<see cref="RingSwimmer"/> には無い）。
        /// 「方向を計算するコードを持たない」という主張を grep で確かめられる形に保つため。
        /// </summary>
        static (double heading, double dist) Measure(RingSwimmer s)
        {
            double deg = Math.Atan2(s.Y, s.X) * 180.0 / Math.PI;
            if (deg < 0) deg += 360.0;
            return (deg, Math.Sqrt(s.X * s.X + s.Y * s.Y));
        }

        static (double heading, double dist) Swim(
            IEnumerable<(int cell, int tick)> stims,
            double g = 0.85, double drag = Drag, int steps = 200, int r0 = 14)
        {
            var p = ExcitableParams.Default;
            p.RefractoryTicks = r0;
            p.Attenuation = g;

            var s = new RingSwimmer(N);
            var list = stims.ToList();
            for (int t = 0; t < steps; t++)
            {
                foreach (var (cell, tick) in list)
                {
                    if (tick == t) s.TryStimulate(cell, p);
                }
                s.Step(p, drag);
            }
            return Measure(s);
        }

        static double AngleError(double a, double b)
        {
            double d = Math.Abs(a - b) % 360.0;
            return d > 180.0 ? 360.0 - d : d;
        }

        static double CellAngle(int cell) => 360.0 * cell / N;

        // ================= M-J2a: 全周同時収縮で直進 =================

        /// <summary>
        /// **全周が同時に収縮すると正味の横力がゼロになること。**
        ///
        /// 16セルを同時に刺激すると、各セルの推力ベクトルが円周上で
        /// 均等に配置されるので合力が打ち消し合う。
        /// 「収縮が推力になる」土台が非対称性からのみ方向を生むことの確認で、
        /// M-J2b（非対称なら方向が出る）と対になる。
        /// </summary>
        [Test]
        public void MJ2a_SimultaneousWholeRingContractionProducesNoNetLateralForce()
        {
            var stims = Enumerable.Range(0, N).Select(c => (c, 0));
            var (_, dist) = Swim(stims);

            Assert.Less(dist, 1e-9,
                $"全周同時収縮なのに移動した（距離 {dist:E3}）。推力の合成が対称でない");
        }

        /// <summary>
        /// 全周同時収縮では速度そのものが（丸め誤差を除いて）ゼロであること。
        /// 距離がゼロでも速度が残っていれば、後から流れ出す。
        /// </summary>
        [Test]
        public void MJ2a_VelocityStaysAtZeroThroughoutTheContraction()
        {
            var p = ExcitableParams.Default;
            var s = new RingSwimmer(N);
            for (int c = 0; c < N; c++) s.TryStimulate(c, p);

            for (int t = 0; t < 40; t++)
            {
                s.Step(p, Drag);
                Assert.Less(Math.Abs(s.Vx), 1e-12, $"t={t}: x方向に正味の力が出ている");
                Assert.Less(Math.Abs(s.Vy), 1e-12, $"t={t}: y方向に正味の力が出ている");
            }
        }

        /// <summary>
        /// 対称な**部分**収縮でも横力は出ないこと（対蹠の2セルを同時に刺激）。
        /// 全周だけでなく、対称性そのものが効いていることの確認。
        /// </summary>
        [Test]
        public void MJ2a_AntipodalPairAlsoCancels()
        {
            var (_, dist) = Swim(new[] { (0, 0), (8, 0) });
            Assert.Less(dist, 1e-9, $"対蹠の同時刺激で移動した（距離 {dist:E3}）");
        }

        // ================= M-J2b: 逃避方向の創発 =================

        /// <summary>
        /// **16方位すべてで、刺激と正反対へ逃げること。**（M-J2b の本体）
        ///
        /// 実行条件: R₀=14 / g=0.85 / drag=0.1 / steps=200。
        /// 実測は全方位で誤差 0.00°、距離も 11.92 で一定（リングの対称性から当然）。
        /// </summary>
        [Test]
        public void MJ2b_EscapeIsAlwaysOppositeToTheStimulus()
        {
            double maxErr = 0;
            for (int c = 0; c < N; c++)
            {
                var (heading, dist) = Swim(new[] { (c, 0) });
                double expected = (CellAngle(c) + 180.0) % 360.0;
                double err = AngleError(heading, expected);
                maxErr = Math.Max(maxErr, err);

                Assert.Less(err, 5.0,
                    $"刺激 {CellAngle(c):F1}° に対し逃避 {heading:F1}°（期待 {expected:F1}°）");
                Assert.AreEqual(11.92, dist, 0.01,
                    $"刺激 {CellAngle(c):F1}°: 距離がリングの対称性から外れている");
            }
            Assert.Less(maxErr, 1e-9, $"最大誤差 {maxErr:F6}° — 厳密に正反対のはず");
        }

        /// <summary>
        /// **減衰 g が符号を決めること。**（prereg 修正2）
        ///
        /// 減衰が無い（g=1.0）と、抗力との相互作用で「後発の側が勝つ」力学になり
        /// **刺激の方向へ進む**。減衰があると刺激と反対へ逃げる。
        /// 減衰は装飾ではなく操舵機構の構成要素である。
        ///
        /// 境界は 0.001 刻みの掃引で g=0.956（TOWARD）/ 0.955（AWAY）（追記4）。
        /// </summary>
        [Test]
        public void MJ2b_AttenuationDecidesTheSignOfTheEscape()
        {
            foreach (double g in new[] { 1.00, 0.98, 0.96 })
            {
                var (heading, _) = Swim(new[] { (0, 0) }, g: g);
                Assert.Less(AngleError(heading, 0.0), 1e-9,
                    $"g={g:F2}: 減衰が弱いので刺激方向へ進むはず（実際 {heading:F1}°）");
            }
            foreach (double g in new[] { 0.955, 0.95, 0.90, 0.85, 0.70 })
            {
                var (heading, _) = Swim(new[] { (0, 0) }, g: g);
                Assert.Less(AngleError(heading, 180.0), 1e-9,
                    $"g={g:F3}: 減衰があるので刺激と反対へ逃げるはず（実際 {heading:F1}°）");
            }
        }

        /// <summary>
        /// **符号反転の境界が g=0.955/0.956 の間にあること。**（追記4 の実測を固定）
        /// </summary>
        [Test]
        public void MJ2b_SignFlipBoundarySitsBetween0955And0956()
        {
            var (toward, _) = Swim(new[] { (0, 0) }, g: 0.956);
            var (away, _) = Swim(new[] { (0, 0) }, g: 0.955);

            Assert.Less(AngleError(toward, 0.0), 1e-9, "g=0.956 は TOWARD のはず");
            Assert.Less(AngleError(away, 180.0), 1e-9, "g=0.955 は AWAY のはず");
        }

        /// <summary>
        /// **R₀ はこの測定に影響しないこと。**（追記4-2、限定つきの主張）
        ///
        /// 単一刺激では2つの波が t=8 で対消滅し、以後どのセルも発火しない。
        /// R₀ が決めるのは「発火したセルがいつ回復するか」だけなので、
        /// 回復が一度も使われないこの測定には因果的に関与しない。
        ///
        /// **一般化してはならない。** J3 のペースメーカー（発火間隔の上限）と
        /// M-J2d の時間差刺激（対象セルが不応期中か）では R₀ は効く。
        /// </summary>
        [Test]
        public void MJ2b_RefractoryPeriodDoesNotAffectSingleStimulusEscape()
        {
            foreach (double g in new[] { 1.000, 0.960, 0.955, 0.950, 0.850 })
            {
                var a = Swim(new[] { (0, 0) }, g: g, steps: 80, r0: 4);
                var b = Swim(new[] { (0, 0) }, g: g, steps: 80, r0: 14);
                Assert.AreEqual(a.heading, b.heading, 1e-12, $"g={g:F3}: 方向が R₀ で変わった");
                Assert.AreEqual(a.dist, b.dist, 1e-12, $"g={g:F3}: 距離が R₀ で変わった");
            }
        }

        /// <summary>
        /// **プロトタイプ j2_attenuation.py との一致**（同条件: r0=4 / steps=80）。
        ///
        /// 条件を揃えないと値がずれる。steps=200 で測ると 8.99 が 9.00 になり、
        /// 移植の誤りに見える（追記4-4）。
        /// </summary>
        [Test]
        public void MJ2b_MatchesThePrototypeUnderItsOwnConditions()
        {
            var expected = new (double g, double heading, double dist)[]
            {
                (1.00, 0.0, 8.99), (0.95, 180.0, 0.93), (0.92, 180.0, 5.29),
                (0.90, 180.0, 7.64), (0.88, 180.0, 9.61), (0.85, 180.0, 11.92),
            };
            foreach (var (g, heading, dist) in expected)
            {
                var got = Swim(new[] { (0, 0) }, g: g, steps: 80, r0: 4);
                Assert.AreEqual(heading, got.heading, 0.05, $"g={g:F2} の方向");
                Assert.AreEqual(dist, got.dist, 0.005, $"g={g:F2} の距離");
            }
        }

        // ================= M-J2b の grep 検証 =================

        /// <summary>
        /// **方向を計算するコードが存在しないこと**を、ソースを走査して確かめる
        /// （prereg §4 の禁止パターン）。
        ///
        /// 【なぜテストにするか】grep は実行した時点の記録にしかならない。
        /// 判定の根拠が「方向を計算していない」ことである以上、
        /// 後から誰かが atan2 を1行足したら判定が崩れる。
        /// ゲートに載せて初めて主張が維持される。
        ///
        /// コメントは除いて走査する（説明文に「勾配」「heading」の語は出てくる）。
        /// </summary>
        [Test]
        public void MJ2b_NoCodeComputesADirection()
        {
            // Application.dataPath は <プロジェクト>/Assets を指す
            string dir = Path.Combine(
                UnityEngine.Application.dataPath, "Scripts", "SimCore", "Excitable");
            Assert.IsTrue(Directory.Exists(dir), $"走査対象が見つからない: {dir}");

            var files = Directory.GetFiles(dir, "*.cs");
            Assert.Greater(files.Length, 0, "走査対象のソースが1つも無い");

            var forbidden = new (string name, string pattern)[]
            {
                ("atan2 による刺激方向の算出", @"Atan2|\bAtan\b"),
                ("場の勾配の符号判定", @"Math\.Sign|\bgradient\b"),
                ("heading 変数", @"\bheading\b|\bbearing\b|\bdirection\b"),
                ("「刺激から遠い側」の明示的な選択", @"\bfarthest\b|\bnearest\b|\bclosest\b|\bopposite\b"),
            };

            int scanned = 0;
            var violations = new List<string>();
            foreach (string file in files)
            {
                string src = File.ReadAllText(file);
                src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
                foreach (string raw in src.Split('\n'))
                {
                    string line = Regex.Replace(raw, @"//.*$", "");
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    scanned++;
                    foreach (var (name, pattern) in forbidden)
                    {
                        if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                        {
                            violations.Add($"{Path.GetFileName(file)}: [{name}] {line.Trim()}");
                        }
                    }
                }
            }

            Assert.Greater(scanned, 100, "走査行数が少なすぎる（コメント除去が過剰）");
            CollectionAssert.IsEmpty(violations,
                "方向を計算するコードが混入した。M-J2b の主張が崩れる:\n  "
                + string.Join("\n  ", violations));
        }
    }
}
