using System;
using System.Collections.Generic;
using System.Linq;
using BlockField.SimCore.Excitable;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// jelly_1 J1: 興奮性媒質リングの伝播特性（M-J1a / M-J1b / M-J1c）。
    ///
    /// 【この段の位置づけ】移植の最初の作業は Python プロトタイプの数値の再現である
    /// （prereg §6.3）。したがって本ファイルの期待値は、すべて
    /// <c>docs/prototypes/jelly/excitable_ring.py</c> と <c>reentry_map.py</c> を
    /// **R₀=14 で実行した実測値**である（プロトタイプの既定 r0=4 は訂正前の値。
    /// prereg 修正3 / 追記3）。
    ///
    /// R₀ を 4 から 14 に変えると何が動くか:
    /// - **対消滅（t=8、セル8）は動かない** — 波は1ステップ1セルで進むので、
    ///   伝播と対消滅の幾何は不応期と無関係
    /// - **消滅時刻は動く** — 終了判定が「全 R == 0」まで待つので 8 + R₀。
    ///   r0=4 なら 12、R₀=14 なら **22**
    /// </summary>
    public class JellyJ1Tests
    {
        const int k_Ring = 16;

        static ExcitableField MakeRing(out ExcitableParams p)
        {
            p = ExcitableParams.Default;
            return new ExcitableField(ExcitableGraphs.Ring(k_Ring));
        }

        /// <summary>プロトタイプの run() と同じ手順で走らせ、各ステップの発火列を返す。</summary>
        static List<int[]> RunAndLog(
            ExcitableField f, ExcitableParams p, int steps, out int diedAt)
        {
            var log = new List<int[]>();
            diedAt = -1;
            for (int t = 1; t <= steps; t++)
            {
                f.Step(p);
                log.Add(f.LastFired.ToArray());
                if (f.IsQuiescent())
                {
                    diedAt = t;
                    return log;
                }
            }
            return log;
        }

        // ================= M-J1a: 伝播と対消滅 =================

        /// <summary>
        /// **単一刺激から左右に波が伝播し、対蹠で対消滅すること。**
        ///
        /// 期待値は R₀=14 での実測:
        ///   t1:[1,15] t2:[2,14] ... t7:[7,9] t8:[8] 以後は発火なし、t=22 で消滅
        /// </summary>
        [Test]
        public void MJ1a_WavesPropagateBothWaysAndAnnihilateAtTheAntipode()
        {
            var f = MakeRing(out var p);
            Assert.AreEqual(14, p.RefractoryTicks, "R₀ は 14 に統一する（prereg 修正3）");

            f.Stimulate(0, p);
            var log = RunAndLog(f, p, 80, out int diedAt);

            var expected = new[]
            {
                new[] { 1, 15 }, new[] { 2, 14 }, new[] { 3, 13 }, new[] { 4, 12 },
                new[] { 5, 11 }, new[] { 6, 10 }, new[] { 7, 9 }, new[] { 8 },
            };
            for (int i = 0; i < expected.Length; i++)
            {
                CollectionAssert.AreEqual(expected[i], log[i],
                    $"t={i + 1} の発火セルがプロトタイプと違う");
            }
            for (int t = expected.Length; t < log.Count; t++)
            {
                CollectionAssert.IsEmpty(log[t], $"t={t + 1} で余分な発火がある");
            }

            Assert.AreEqual(22, diedAt,
                "消滅時刻が 8 + R₀ になっていない（R₀=14 なので 22。r0=4 なら 12 だった）");
        }

        /// <summary>
        /// 刺激位置を変えても、対蹠での対消滅という構造が保たれること。
        /// セル3 を刺激すると対蹠は 3+8=11 になる（R₀=14 での実測）。
        /// </summary>
        [Test]
        public void MJ1a_AnnihilationFollowsTheStimulusPosition()
        {
            var f = MakeRing(out var p);
            f.Stimulate(3, p);
            var log = RunAndLog(f, p, 80, out int diedAt);

            var expected = new[]
            {
                new[] { 2, 4 }, new[] { 1, 5 }, new[] { 0, 6 }, new[] { 7, 15 },
                new[] { 8, 14 }, new[] { 9, 13 }, new[] { 10, 12 }, new[] { 11 },
            };
            for (int i = 0; i < expected.Length; i++)
            {
                CollectionAssert.AreEqual(expected[i], log[i], $"t={i + 1} の発火セル");
            }
            Assert.AreEqual(22, diedAt);
        }

        // ================= M-J1b: リエントリー境界 =================

        /// <summary>
        /// 一方向波を作る。プロトタイプ reentry_map.run_uni と同じで、
        /// 刺激セルの「後ろ側」を R₀+1 の不応期にして後方への波を潰す。
        /// </summary>
        static string RunUnidirectional(int n, int r0, int steps = 400)
        {
            var p = ExcitableParams.Default;
            p.RefractoryTicks = r0;
            var f = new ExcitableField(ExcitableGraphs.Ring(n));

            f.Stimulate(0, p);
            f.SetRefractory(n - 1, r0 + 1);

            for (int t = 1; t <= steps; t++)
            {
                f.Step(p);
                if (f.IsQuiescent())
                {
                    return "died";
                }
            }
            return "REENTRY";
        }

        /// <summary>
        /// **N=16 のリエントリー境界が 13/14 であること。**
        ///
        /// 一周に N ステップ要るので、境界条件は「一周時間 &gt; 不応期 + 回復(~1)」。
        /// N=16 なら R₀ ≥ 14 で波が自分の航跡に追いつけず消える。
        /// これが R₀=14 を採る根拠そのものである（prereg 修正3）。
        /// </summary>
        [Test]
        public void MJ1b_ReentryBoundaryOnA16RingIsBetween13And14()
        {
            Assert.AreEqual("REENTRY", RunUnidirectional(16, 13),
                "R₀=13 ではリエントリー（回転波）が起きるはず");
            Assert.AreEqual("died", RunUnidirectional(16, 14),
                "R₀=14 では波が消えるはず。ここが 14 を採る根拠");
        }

        /// <summary>
        /// 境界の周辺が単調であること（下側は回り続け、上側は消える）。
        /// R₀=11,12,13 → リエントリー / 14〜17 → 消滅（プロトタイプ実測）。
        /// </summary>
        [Test]
        public void MJ1b_BoundaryIsMonotonicAroundTheThreshold()
        {
            foreach (int r0 in new[] { 11, 12, 13 })
            {
                Assert.AreEqual("REENTRY", RunUnidirectional(16, r0), $"R₀={r0}");
            }
            foreach (int r0 in new[] { 14, 15, 16, 17 })
            {
                Assert.AreEqual("died", RunUnidirectional(16, r0), $"R₀={r0}");
            }
        }

        /// <summary>
        /// リングの大きさを変えても一般則「N ステップの一周 &gt; R₀ + 回復」で説明できること。
        /// プロトタイプの境界マップ（N × R₀ の格子）の再現。
        /// </summary>
        [Test]
        public void MJ1b_BoundaryScalesWithRingSize()
        {
            // プロトタイプ reentry_map.py の実測: R = リエントリー / . = 消滅
            //   N\R0    2  4  6  8 10 12 14 16 18 20
            //     8     R  R  .  .  .  .  .  .  .  .
            //    12     R  R  R  R  .  .  .  .  .  .
            //    16     R  R  R  R  R  R  .  .  .  .
            var expected = new Dictionary<int, string>
            {
                [8] = "RR........",
                [12] = "RRRR......",
                [16] = "RRRRRR....",
            };
            foreach (var (n, row) in expected.Select(kv => (kv.Key, kv.Value)))
            {
                for (int c = 0; c < row.Length; c++)
                {
                    int r0 = 2 + c * 2;
                    string want = row[c] == 'R' ? "REENTRY" : "died";
                    Assert.AreEqual(want, RunUnidirectional(n, r0), $"N={n}, R₀={r0}");
                }
            }
        }

        // ================= M-J1c: 決定論 =================

        /// <summary>
        /// **同一入力から2回走らせて同じ状態に到達すること。**
        /// ハッシュは C# 側の FNV-1a 64bit（Python の SHA-256 とは別物）。
        /// </summary>
        [Test]
        public void MJ1c_SameInputReachesTheSameState()
        {
            ulong Run(int stim, int steps)
            {
                var f = MakeRing(out var p);
                f.Stimulate(stim, p);
                for (int t = 0; t < steps; t++) f.Step(p);
                return f.ComputeContentHash();
            }

            foreach (int stim in new[] { 0, 3, 9 })
            {
                Assert.AreEqual(Run(stim, 60), Run(stim, 60),
                    $"stim={stim}: 2回の実行が違う状態になった");
                Assert.AreEqual(Run(stim, 5), Run(stim, 5),
                    $"stim={stim}: 波が生きている時点でも一致すること");
            }

            // ハッシュが本当に状態を見ているかは、**波が生きている間**に確かめる。
            // 静止後は下のテストのとおり刺激位置によらず同じ状態になるので、
            // そこで比べると「ハッシュが壊れている」と区別がつかない
            Assert.AreNotEqual(Run(0, 5), Run(3, 5),
                "刺激位置が違うのに同じハッシュ。ハッシュが状態を見ていない疑いがある");
        }

        /// <summary>
        /// **静止したあとの状態は、どこを刺激したかに依存しないこと。**
        ///
        /// 興奮性媒質は痕跡を残さない。E も R も A も 0 に戻り、
        /// 「どこから始まったか」の記憶が場に残らない。
        /// これは生態系側の場（deposit / diffuse / evaporate で痕跡が積もる）との
        /// 決定的な違いであり、jelly_2 で航跡場を別に持つ理由でもある。
        /// </summary>
        [Test]
        public void MJ1c_TheRestingStateKeepsNoMemoryOfTheStimulus()
        {
            ulong RunToRest(int stim)
            {
                var f = MakeRing(out var p);
                f.Stimulate(stim, p);
                RunAndLog(f, p, 80, out int diedAt);
                Assert.AreEqual(22, diedAt, $"stim={stim}: 消滅時刻が 8 + R₀ でない");
                for (int i = 0; i < f.CellCount; i++)
                {
                    Assert.AreEqual(0.0, f.Excitation(i), 1e-12);
                    Assert.AreEqual(0, f.Refractory(i));
                    Assert.AreEqual(0.0, f.Amplitude(i), 1e-12);
                }
                return f.ComputeContentHash();
            }

            Assert.AreEqual(RunToRest(0), RunToRest(3),
                "静止状態が刺激位置を覚えている（痕跡が残らないはず）");
            Assert.AreEqual(RunToRest(0), RunToRest(9));
        }

        /// <summary>
        /// **近傍リストの走査順を反転しても同一結果になること。**（prereg 修正1）
        ///
        /// 同期更新が守られていれば、走査順は結果に影響しない。
        /// インプレース更新に戻すとここが落ちる。
        /// </summary>
        [Test]
        public void MJ1c_ReversingTheNeighborOrderChangesNothing()
        {
            var forward = ExcitableGraphs.Ring(k_Ring);
            var reversed = forward.Select(a => a.Reverse().ToArray()).ToArray();

            var p = ExcitableParams.Default;
            var fa = new ExcitableField(forward);
            var fb = new ExcitableField(reversed);
            fa.Stimulate(0, p);
            fb.Stimulate(0, p);

            for (int t = 1; t <= 40; t++)
            {
                fa.Step(p);
                fb.Step(p);
                CollectionAssert.AreEqual(fa.LastFired.ToArray(), fb.LastFired.ToArray(),
                    $"t={t}: 近傍の走査順で発火列が変わった（同期更新が壊れている）");
            }
            Assert.AreEqual(fa.ComputeContentHash(), fb.ComputeContentHash());
        }

        /// <summary>
        /// **セルの走査順に依存しないこと。** 同期更新なら、セル添字の並びを
        /// 入れ替えたグラフでも「同じ波」が走る。ここでは添字を反転した
        /// リング（i → n-1-i の写像）で確かめる。
        /// </summary>
        [Test]
        public void MJ1c_RelabelingCellsMirrorsTheResultExactly()
        {
            var p = ExcitableParams.Default;
            var f = new ExcitableField(ExcitableGraphs.Ring(k_Ring));
            f.Stimulate(0, p);

            // 添字を反転した世界。セル 0 は 15 に、1 は 14 に…と写る
            int Mirror(int i) => (k_Ring - 1 - i);
            var mirrored = new int[k_Ring][];
            for (int i = 0; i < k_Ring; i++)
            {
                mirrored[Mirror(i)] = ExcitableGraphs.Ring(k_Ring)[i].Select(Mirror).ToArray();
            }
            var g = new ExcitableField(mirrored);
            g.Stimulate(Mirror(0), p);

            for (int t = 1; t <= 40; t++)
            {
                f.Step(p);
                g.Step(p);
                var a = f.LastFired.Select(Mirror).OrderBy(x => x).ToArray();
                var b = g.LastFired.OrderBy(x => x).ToArray();
                CollectionAssert.AreEqual(a, b, $"t={t}: セルの並べ替えで結果が変わった");
            }
        }

        // ================= 状態の持ち方（修正4 の器） =================

        /// <summary>
        /// **振幅 A が「波が運ぶ状態」として伝わること。**（prereg 修正4）
        ///
        /// 刺激セルは A=1、そこから n ホップ目のセルは g^n になる。
        /// これは幾何距離を計算した結果ではなく、
        /// 「発火した近傍の A の最大値 × g」を繰り返した結果である。
        /// </summary>
        [Test]
        public void Amplitude_IsCarriedByTheWaveNotComputedFromDistance()
        {
            var f = MakeRing(out var p);
            f.Stimulate(0, p);

            for (int hop = 1; hop <= 8; hop++)
            {
                f.Step(p);
                double expected = Math.Pow(p.Attenuation, hop);
                foreach (int cell in f.LastFired)
                {
                    Assert.AreEqual(expected, f.Amplitude(cell), 1e-12,
                        $"{hop} ホップ目のセル {cell} の振幅が g^{hop} でない");
                }
            }
        }

        /// <summary>
        /// 振幅を持たせても J1 の発火列は変わらないこと。
        /// A は E・R の力学に影響しない（J2 で推力に使うまで観測専用）。
        /// </summary>
        [Test]
        public void Amplitude_DoesNotAffectFiringAtAll()
        {
            var p = ExcitableParams.Default;
            var noAttenuation = p;
            noAttenuation.Attenuation = 1.0;

            var a = new ExcitableField(ExcitableGraphs.Ring(k_Ring));
            var b = new ExcitableField(ExcitableGraphs.Ring(k_Ring));
            a.Stimulate(0, p);
            b.Stimulate(0, noAttenuation);

            for (int t = 1; t <= 30; t++)
            {
                a.Step(p);
                b.Step(noAttenuation);
                CollectionAssert.AreEqual(a.LastFired.ToArray(), b.LastFired.ToArray(),
                    $"t={t}: g を変えたら発火列が変わった（A が E/R に漏れている）");
            }
        }

        // ================= 汎用グラフであること =================

        /// <summary>
        /// リングが「近傍＝左右2セル」の特殊例にすぎないこと。
        /// 鎖では端で波が消え、リエントリーが起こりえない。
        /// </summary>
        [Test]
        public void Graph_ChainIsTheSameClassWithDifferentNeighbors()
        {
            var p = ExcitableParams.Default;
            var f = new ExcitableField(ExcitableGraphs.Chain(16));
            f.Stimulate(0, p);

            var fired = new List<int[]>();
            for (int t = 1; t <= 40; t++)
            {
                f.Step(p);
                fired.Add(f.LastFired.ToArray());
            }
            // 端から入れた波は一方向に走り、15ステップで反対の端に着いて終わる
            for (int t = 0; t < 15; t++)
            {
                CollectionAssert.AreEqual(new[] { t + 1 }, fired[t], $"t={t + 1}");
            }
            for (int t = 15; t < fired.Count; t++)
            {
                CollectionAssert.IsEmpty(fired[t], $"t={t + 1}: 鎖で波が残っている");
            }
        }

        /// <summary>2次元シートも同じクラスで扱えること（器の確認）。</summary>
        [Test]
        public void Graph_SheetHasFourNeighborsInsideAndFewerAtTheEdge()
        {
            var sheet = ExcitableGraphs.Sheet(5, 4);
            Assert.AreEqual(20, sheet.Length);
            Assert.AreEqual(4, sheet[2 * 5 + 2].Length, "内部セルの近傍は4つ");
            Assert.AreEqual(2, sheet[0].Length, "角セルの近傍は2つ");
            Assert.AreEqual(3, sheet[1].Length, "辺セルの近傍は3つ");

            var torus = ExcitableGraphs.Sheet(5, 4, wrap: true);
            Assert.AreEqual(4, torus[0].Length, "トーラスなら角でも4つ");

            // 同じクラスで動くこと
            var p = ExcitableParams.Default;
            var f = new ExcitableField(sheet);
            f.Stimulate(2 * 5 + 2, p);
            f.Step(p);
            Assert.AreEqual(4, f.LastFired.Count, "中央から4方向へ広がるはず");
        }

        [Test]
        public void Graph_RingRejectsDegenerateSizes()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ExcitableGraphs.Ring(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => ExcitableGraphs.Ring(0));
        }
    }
}
