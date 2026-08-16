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

        // ================= M-J2c: 48シードの定量判定 =================

        /// <summary>
        /// プロトタイプ `j2c_waveamp.py` と同一の乱数列（LCG）。
        /// シードから 刺激セル / g / drag を作る。照合対象なので式を変えない。
        /// </summary>
        /// <remarks>
        /// M-J3a の48シードでも同じ列を使う（prereg 追記6-5）。
        /// 判定ごとに別の乱数列を使うと、条件の差なのか列の差なのかが分からなくなる。
        /// </remarks>
        internal static IEnumerable<double> RngStreamFor(uint seed)
        {
            uint s = seed;
            while (true)
            {
                unchecked { s = s * 1664525u + 1013904223u; }
                yield return s / 4294967296.0;
            }
        }

        static IEnumerable<double> RngStream(uint seed) => RngStreamFor(seed);

        /// <summary>
        /// **48シードで、刺激角と逃避角が 1:1 単調・オフセット 180° であること。**
        ///
        /// パラメータ揺らぎ（g ∈ [0.75, 0.92]、drag ∈ [0.05, 0.2]）込み。
        /// 実行条件: steps=120 / R₀=14。判定閾値は最大誤差 &lt; 5°。
        /// 実測は **最大 0.000°**（プロトタイプと一致）。
        /// </summary>
        [Test]
        public void MJ2c_StimulusAndEscapeMapOneToOneAcrossFortyEightSeeds()
        {
            double maxErr = 0;
            for (uint seed = 1000; seed < 1048; seed++)
            {
                var r = RngStream(seed).GetEnumerator();
                r.MoveNext(); int cell = (int)(r.Current * 16);
                r.MoveNext(); double g = 0.75 + r.Current * 0.17;
                r.MoveNext(); double drag = 0.05 + r.Current * 0.15;

                var (heading, _) = Swim(new[] { (cell, 0) }, g: g, drag: drag, steps: 120);
                double expected = (CellAngle(cell) + 180.0) % 360.0;
                double err = AngleError(heading, expected);
                maxErr = Math.Max(maxErr, err);

                Assert.Less(err, 5.0,
                    $"seed={seed} cell={cell} g={g:F4} drag={drag:F4}: " +
                    $"逃避 {heading:F1}°（期待 {expected:F1}°）");
            }
            Assert.Less(maxErr, 1e-9,
                $"最大誤差 {maxErr:E3}° — パラメータ揺らぎに対して厳密に頑健なはず");
        }

        // ================= M-J2d: 重ね合わせ =================

        /// <summary>
        /// 【比較専用】幾何距離モデル（旧版）の推力。**SimCore には無い。**
        ///
        /// 波振幅モデルが正しいことを示すための対照であって、本体で使う実装ではない。
        /// SimCore に置くと、後から誰かが「こちらもある」と思って使ってしまう。
        /// 刺激セルからのホップ数を毎回計算して g^hops を掛ける
        /// ——距離を計算するコードが要る時点で、創発の主張と両立しない（prereg 修正4）。
        /// </summary>
        static (double heading, double dist) SwimGeometric(
            (int cell, int tick)[] stims, double g = 0.85, double drag = Drag,
            int steps = 200, int r0 = 14)
        {
            var p = ExcitableParams.Default;
            p.RefractoryTicks = r0;
            p.Attenuation = g;

            var field = new ExcitableField(ExcitableGraphs.Ring(N));
            int origin = stims.OrderBy(s => s.tick).First().cell;

            double vx = 0, vy = 0, x = 0, y = 0;
            for (int t = 0; t < steps; t++)
            {
                foreach (var (cell, tick) in stims)
                {
                    if (tick == t && field.Refractory(cell) == 0) field.Stimulate(cell, p);
                }
                field.Step(p);
                foreach (int i in field.LastFired)
                {
                    int hops = Math.Min(((i - origin) % N + N) % N, ((origin - i) % N + N) % N);
                    double amp = Math.Pow(g, hops);
                    double a = 2.0 * Math.PI * i / N;
                    vx -= amp * Math.Cos(a);
                    vy -= amp * Math.Sin(a);
                }
                vx *= (1.0 - drag); vy *= (1.0 - drag); x += vx; y += vy;
            }
            double deg = Math.Atan2(y, x) * 180.0 / Math.PI;
            if (deg < 0) deg += 360.0;
            return (deg, Math.Sqrt(x * x + y * y));
        }

        /// <summary>
        /// **2点同時刺激の逃避方向がベクトル平均に一致すること。**（M-J2d の本体）
        ///
        /// 0° と 90° を同時に刺激すると、逃避は 225.0°（＝2つの逃避 180°・270° の
        /// ベクトル平均）。実行条件: steps=200 / R₀=14 / g=0.85。
        /// </summary>
        [Test]
        public void MJ2d_TwoSimultaneousStimuliEscapeAlongTheVectorAverage()
        {
            var (heading, _) = Swim(new[] { (0, 0), (4, 0) });
            Assert.Less(AngleError(heading, 225.0), 1e-9,
                $"逃避 {heading:F1}°（ベクトル平均の予測 225.0°）");
        }

        /// <summary>
        /// **幾何距離モデルとの弁別が steps の差で説明できないこと。**
        ///
        /// prereg の記載（旧 158.5° / 新 225.0°）は steps が違った（120 対 200）ため、
        /// そのままでは弁別に使えなかった（追記4）。**steps を揃えて**測ると、
        /// 120 / 200 / 400 のいずれでも 225.0° 対 158.5° で変わらない。
        /// 差はモデルの差であって実行条件の差ではない。
        /// </summary>
        [Test]
        public void MJ2d_ModelDiscriminationIsNotAnArtifactOfStepCount()
        {
            foreach (int steps in new[] { 120, 200, 400 })
            {
                var wave = Swim(new[] { (0, 0), (4, 0) }, steps: steps);
                var geo = SwimGeometric(new[] { (0, 0), (4, 0) }, steps: steps);

                Assert.Less(AngleError(wave.heading, 225.0), 1e-9,
                    $"steps={steps}: 波振幅モデルは 225.0° のはず（実際 {wave.heading:F1}°）");
                Assert.Less(AngleError(geo.heading, 158.5), 0.05,
                    $"steps={steps}: 幾何距離モデルは 158.5° のはず（実際 {geo.heading:F1}°）");
            }
        }

        /// <summary>
        /// **幾何距離モデルが破れる理由: 減衰の基準に1つの刺激を選んでしまうこと。**
        ///
        /// 0° と 90° の同時刺激は 45° 軸について対称なので、正味の力は
        /// その軸上（45° か 225°）にしか出ようがない。波振幅モデルは基準点を
        /// 持たないので対称性が保たれ、225.0° になる。
        /// 幾何距離モデルは「最初の刺激」を減衰の原点に選ぶため**対称性を人工的に壊す**。
        /// 刺激の順番を入れ替えると答えが変わることがそれを示す。
        /// </summary>
        [Test]
        public void MJ2d_GeometricModelBreaksTheSymmetryItShouldPreserve()
        {
            var wave1 = Swim(new[] { (0, 0), (4, 0) });
            var wave2 = Swim(new[] { (4, 0), (0, 0) });
            Assert.Less(AngleError(wave1.heading, wave2.heading), 1e-9,
                "波振幅モデルは刺激の並び順に依存しないはず");

            var geo1 = SwimGeometric(new[] { (0, 0), (4, 0) });
            var geo2 = SwimGeometric(new[] { (4, 0), (0, 0) });
            Assert.Greater(AngleError(geo1.heading, geo2.heading), 1.0,
                "幾何距離モデルは並び順で答えが変わるはず（原点の選び方が結果を決めている）");
        }

        /// <summary>
        /// **ベクトル平均は密な刺激では成り立たない。**（M-J2d の適用範囲の限定）
        ///
        /// 0°・90°・180° の3点同時刺激では、ベクトル平均の予測は 270° だが
        /// 実測は **90°（正反対）**である。しかも移動距離は 0.775 で、
        /// 単一刺激 11.921 の 6.5% しかない。**ほぼ相殺していて、
        /// 残差の符号が予測と逆を向いている**状態である。
        ///
        /// 原因は重ね合わせが線形でないこと。**各セルは不応期のあいだ1度しか
        /// 発火できない**ので、重なった波は足し算にならない
        /// （2点でも 4.845 で、線形なら 16.859 のはず）。
        ///
        /// M-J2d は「2点・90°離れ」で成立する。**一般の重ね合わせ則ではない。**
        /// </summary>
        [Test]
        public void MJ2d_VectorAveragingDoesNotSurviveDenseStimulation()
        {
            var three = Swim(new[] { (0, 0), (4, 0), (8, 0) });

            // 予測は「各刺激の逃避（刺激角+180°）の単位ベクトルの和」= 270°
            Assert.Less(AngleError(three.heading, 90.0), 1e-9,
                $"3点同時刺激の逃避は 90° のはず（実際 {three.heading:F1}°）");
            Assert.Greater(AngleError(three.heading, 270.0), 179.0,
                "ベクトル平均の予測 270° とは正反対になるはず（これが限定の中身）");

            // 大きさも線形でない
            double one = Swim(new[] { (0, 0) }).dist;
            double two = Swim(new[] { (0, 0), (4, 0) }).dist;
            Assert.AreEqual(11.921, one, 0.001);
            Assert.AreEqual(4.845, two, 0.001, "線形なら 16.859 のはず");
            Assert.AreEqual(0.775, three.dist, 0.001, "線形なら 11.921 のはず");
        }

        /// <summary>
        /// **R₀ は時間差刺激では効く。**（追記4-3 の限定の実測）
        ///
        /// 単一刺激では R₀ を変えても結果が変わらなかったが、それは
        /// 「対消滅で終わるので回復が一度も使われない」ためだった。
        /// 時間差刺激では、2つ目の刺激が入るかどうかを R₀ が左右する。
        ///
        /// ただし **prereg が例に挙げた t=6 では効かない**。t=6 の時点で
        /// セル4 は R₀=4 でも R=2、R₀=14 でも R=12 で、**どちらでも刺激が入らない**。
        /// 効くのは t=8〜18 の窓である。
        /// </summary>
        [Test]
        public void MJ2d_RefractoryPeriodMattersForDelayedStimuliButNotAtTickSix()
        {
            // prereg の例（t=6）: どちらの R₀ でも2つ目が入らないので同じ結果
            var a6 = Swim(new[] { (0, 0), (4, 6) }, r0: 4);
            var b6 = Swim(new[] { (0, 0), (4, 6) }, r0: 14);
            Assert.AreEqual(a6.heading, b6.heading, 1e-12, "t=6 では R₀ で変わらないはず");
            Assert.Less(AngleError(a6.heading, 180.0), 1e-9,
                "2つ目が入らないので、単一刺激と同じ 180° になるはず");

            // t=10: R₀=4 なら入り、R₀=14 なら入らない
            //
            // 【許容が同時刺激より緩い理由】2つの刺激が別の時刻に入ると、
            // 各刺激の寄与が別々の減衰段で積み上がるため、x 成分と y 成分が
            // 浮動小数として厳密には等しくならない（実測の残差は 5e-9 度）。
            // 同時刺激なら対称性がビット単位で保たれるので 1e-9 で通る。
            const double k_DelayedTolerance = 1e-6;   // 100万分の1度
            var a10 = Swim(new[] { (0, 0), (4, 10) }, r0: 4);
            var b10 = Swim(new[] { (0, 0), (4, 10) }, r0: 14);
            Assert.Less(AngleError(a10.heading, 225.0), k_DelayedTolerance,
                "R₀=4 なら2つ目が入って 225°");
            Assert.Less(AngleError(b10.heading, 180.0), k_DelayedTolerance,
                "R₀=14 なら入らず 180° のまま");

            // t=19: どちらでも入るので一致する
            var a19 = Swim(new[] { (0, 0), (4, 19) }, r0: 4);
            var b19 = Swim(new[] { (0, 0), (4, 19) }, r0: 14);
            Assert.AreEqual(a19.heading, b19.heading, 1e-12, "t=19 では両方入るので一致");
            Assert.Less(AngleError(a19.heading, 225.0), k_DelayedTolerance);
        }

        /// <summary>
        /// 刺激が「入る最初のティック」だけ結果が違うこと。
        ///
        /// 刺激セル自身の不応期が抜けても、**その両隣がまだ抜けていない**と
        /// 片側にしか波が出ず、非対称な逃避になる（186.8°）。
        /// 1ティック後に両隣が揃うと対称になり 225.0° に落ち着く。
        /// 「回復したか」は1セルの話ではなく近傍の話である、という記録。
        /// </summary>
        [Test]
        public void MJ2d_TheFirstTickAfterRecoveryGivesAnAsymmetricEscape()
        {
            // R₀=4: セル4 は t=8 で回復するが、隣のセル5 は t=9 まで不応期
            Assert.Less(AngleError(Swim(new[] { (0, 0), (4, 8) }, r0: 4).heading, 186.8), 0.05,
                "t=8 は片側にしか波が出ないので中途半端な向きになる");
            // 時間差刺激なので許容は 1e-6（同上の理由）
            Assert.Less(AngleError(Swim(new[] { (0, 0), (4, 9) }, r0: 4).heading, 225.0), 1e-6,
                "t=9 では両隣が揃うので対称な 225° になる");
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
