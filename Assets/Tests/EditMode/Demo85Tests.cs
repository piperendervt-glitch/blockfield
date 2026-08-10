using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// Demo 8.5: 植物の場化。
    ///
    /// 段階0（準備）の時点では、追加した API とパラメータが
    /// **既存の挙動を一切変えていないこと**を固定するのが主な役目。
    /// 段階1以降でここに摂食・成長・踏み潰しのテストが増える。
    /// </summary>
    public class Demo85Tests
    {
        static World MakeDiorama(uint seed)
        {
            var tp = TerrainParams.Default;
            tp.seed = seed;
            tp.width = 50;
            tp.depth = 50;
            tp.maxHeight = 16;
            return World.Create(tp);
        }

        // ---- ScalarField.Consume の契約 ----

        [Test]
        public void Consume_ReturnsTheAmountActuallyTaken()
        {
            var world = MakeDiorama(1u);
            world.Vegetation.SetAtColumn(10, 10, 0.8f);

            float taken = world.Vegetation.Consume(10, 10, 0.5f);

            Assert.AreEqual(0.5f, taken, 1e-6f, "要求量を全て取れるはずの場面で取れていない");
            Assert.AreEqual(0.3f, world.Vegetation.GetAtColumn(10, 10), 1e-6f, "場が減っていない");
        }

        [Test]
        public void Consume_IsLimitedByWhatIsThere()
        {
            // 「そこにあるだけ食べる」。草の薄いセルでは部分的にしか食べられず、
            // 回復も少ない — これが摂食を連続量にする意味そのもの
            var world = MakeDiorama(2u);
            world.Vegetation.SetAtColumn(5, 5, 0.2f);

            float taken = world.Vegetation.Consume(5, 5, 0.5f);

            Assert.AreEqual(0.2f, taken, 1e-6f, "そこにある量を超えて取れている");
            Assert.AreEqual(0f, world.Vegetation.GetAtColumn(5, 5), 1e-6f, "食べ尽くしたのに残っている");
        }

        [Test]
        public void Consume_NeverGoesNegative()
        {
            var world = MakeDiorama(3u);
            world.Vegetation.SetAtColumn(7, 7, 0f);

            Assert.AreEqual(0f, world.Vegetation.Consume(7, 7, 1f), 1e-6f);
            Assert.AreEqual(0f, world.Vegetation.GetAtColumn(7, 7), 1e-6f);
            Assert.GreaterOrEqual(world.Vegetation.GetAtColumn(7, 7), 0f, "場が負になっている");
        }

        [Test]
        public void Consume_IgnoresNonPositiveRequests()
        {
            var world = MakeDiorama(4u);
            world.Vegetation.SetAtColumn(3, 3, 0.4f);

            Assert.AreEqual(0f, world.Vegetation.Consume(3, 3, 0f), 1e-6f);
            Assert.AreEqual(0f, world.Vegetation.Consume(3, 3, -1f), 1e-6f);
            Assert.AreEqual(0.4f, world.Vegetation.GetAtColumn(3, 3), 1e-6f, "取らないはずの呼び出しで場が動いた");
        }

        [Test]
        public void Consume_WorksOnEveryField()
        {
            // 摂食は植生場にしか使わないが、API は ScalarField にあるので
            // 他の場でも同じ契約で動くこと（将来 踏み潰しの減算などで使う）
            var world = MakeDiorama(5u);
            world.Trample.SetAtColumn(12, 12, 0.6f);
            Assert.AreEqual(0.25f, world.Trample.Consume(12, 12, 0.25f), 1e-6f);
            Assert.AreEqual(0.35f, world.Trample.GetAtColumn(12, 12), 1e-6f);
        }

        // ---- 段階0が既存の挙動を変えていないこと ----

        /// <summary>
        /// 追加したパラメータはまだどこからも読まれていないので、
        /// 値を変えても世界は1ビットも変わらないはず。
        /// これが崩れたら「未使用のつもりが配線されている」ということで、
        /// 段階0の前提が壊れている。
        /// </summary>
        [Test]
        public void Stage0_NewParametersAreNotWiredYet()
        {
            const int ticks = 300;
            uint seed = 12345u;

            var baseline = MakeDiorama(seed);
            for (int t = 0; t < ticks; t++)
            {
                Simulation.Tick(baseline, baseline.Rng, SimParams.Default);
            }

            var changed = SimParams.Default;
            changed.grazeBite = 0.123f;
            changed.grazeRecovery = 7f;
            changed.grazeThreshold = 0.9f;
            changed.vegetationGrowth = 0.99f;

            var other = MakeDiorama(seed);
            for (int t = 0; t < ticks; t++)
            {
                Simulation.Tick(other, other.Rng, changed);
            }

            Assert.AreEqual(baseline.ComputeContentHash(), other.ComputeContentHash(),
                "Demo 8.5 用のパラメータが既に挙動へ影響している（段階0では未配線のはず）");
        }

        [Test]
        public void Stage0_DefaultsAreConsistentWithTheMigrationPlan()
        {
            var p = SimParams.Default;

            // 一口 × 回復係数 = 1.0。草が十分あるセルでは移行前の
            // 「植物1本で hunger=0」と同等の回復になるよう設計している
            Assert.AreEqual(1f, p.grazeBite * p.grazeRecovery, 1e-6f,
                "一口と回復係数の積が1.0でない。草の茂ったセルでの回復量が移行前と揃わない");

            // 食べる閾値は表示の最低段階(0.2)より低い。
            // 「見えるより先に食べ尽くす」ほうが自然なため
            Assert.Less(p.grazeThreshold, 0.2f, "摂食閾値が表示の最低段階以上になっている");
            Assert.Greater(p.grazeThreshold, 0f);
        }
    }
}
