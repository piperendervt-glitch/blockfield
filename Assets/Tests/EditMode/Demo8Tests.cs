using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>Demo 8 第1段: 恐怖場と獲物場のテスト。</summary>
    public class Demo8Tests
    {
        static readonly uint[] k_Seeds = { 12345u, 777u, 20260809u };

        /// <summary>
        /// 置換前（狼が視界内の全個体を走査していた頃）の実測値。
        /// 同条件（箱庭 50x50、3シード、2,000ティック）でのヘッドレス計測。
        /// M5 はこの値の半分を下回らないことを求める。
        /// </summary>
        const double k_BaselinePredationPer1000 = 17.83;

        static TerrainParams DioramaParams(uint seed) => new TerrainParams
        {
            seed = seed,
            width = 50,
            depth = 50,
            maxHeight = 16,
            reliefScale = 24f,
            plainsAmplitude = 0.25f,
            mountainAmplitude = 1f,
        };

        static World MakeDiorama(uint seed) => World.Create(DioramaParams(seed));

        // ---- 場読みの振る舞い（決定論的な単体検証）----

        [Test]
        public void Wolf_FollowsPreyFieldGradient()
        {
            var world = MakeDiorama(1u);
            var cell = new Int3(25, world.GetSurfaceHeight(25, 25), 25);

            // +X 側だけに匂いを置く。狼はそちらを向くはず（facing 0 = +X）
            world.Prey.SetAtColumn(26, 25, 0.8f);
            Assert.AreEqual(0, Simulation.FindPreyGradientFacing(world, cell, 2),
                "獲物場が濃い +X 方向へ向いていない");

            // -Z 側をさらに濃くすると、そちらへ切り替わる（facing 3 = -Z）
            world.Prey.SetAtColumn(25, 24, 0.9f);
            Assert.AreEqual(3, Simulation.FindPreyGradientFacing(world, cell, 0),
                "より濃い -Z 方向へ向いていない");
        }

        [Test]
        public void Wolf_KeepsFacingWhenNoScent()
        {
            // 匂いが全く無ければ向きを変えない＝通常の徘徊に落ちる
            var world = MakeDiorama(2u);
            var cell = new Int3(25, world.GetSurfaceHeight(25, 25), 25);
            Assert.AreEqual(1, Simulation.FindPreyGradientFacing(world, cell, 1));
        }

        [Test]
        public void M2_HerbivoreTurnsAwayFromFear()
        {
            var world = MakeDiorama(3u);
            var p = SimParams.Default;
            var cell = new Int3(25, world.GetSurfaceHeight(25, 25), 25);

            // 4近傍を平らにしてから、+X に草、-X に恐怖を置く
            world.Vegetation.SetAtColumn(26, 25, 0.5f);
            world.Fear.SetAtColumn(24, 25, 0.5f);

            Assert.AreEqual(0, Simulation.FindForagingFacing(world, p, cell, 2),
                "草のある +X ではなく恐怖のある方へ向いている");

            // 同じ +X に強い恐怖を足すと、草があっても避けるようになる
            world.Fear.SetAtColumn(26, 25, 0.9f);
            Assert.AreNotEqual(0, Simulation.FindForagingFacing(world, p, cell, 2),
                "草はあるが恐怖が濃い方向を選んでいる（葛藤が表現できていない）");
        }

        [Test]
        public void M2_HerbivoreEscapesEvenWhenAllDirectionsAreScary()
        {
            // 全方向が負スコア（草が無く恐怖だけ）でも、最も薄い方向へ逃げること。
            // 「魅力が無いから動かない」にすると危険地帯のど真ん中で回避が効かなくなる
            var world = MakeDiorama(4u);
            var p = SimParams.Default;
            var cell = new Int3(25, world.GetSurfaceHeight(25, 25), 25);

            world.Fear.SetAtColumn(26, 25, 0.9f); // +X
            world.Fear.SetAtColumn(25, 26, 0.9f); // +Z
            world.Fear.SetAtColumn(24, 25, 0.9f); // -X
            world.Fear.SetAtColumn(25, 24, 0.2f); // -Z が最も薄い

            Assert.AreEqual(3, Simulation.FindForagingFacing(world, p, cell, 0),
                "全方向が危険なとき、最も恐怖の薄い方向へ逃げていない");
        }

        // ---- 場の書き込み ----

        [Test]
        public void Fields_AreDepositedAndDecay()
        {
            var world = MakeDiorama(5u);
            var p = SimParams.Default;

            for (int t = 0; t < 300; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }

            float fearSum = 0f, preySum = 0f;
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    fearSum += world.Fear.GetAtColumn(x, z);
                    preySum += world.Prey.GetAtColumn(x, z);
                }
            }

            Assert.Greater(preySum, 0f, "獲物場に痕跡が残っていない（草食獣が書いていない）");
            Assert.GreaterOrEqual(fearSum, 0f);

            // 上限1.0で飽和すること
            var cell = new Int3(10, world.GetSurfaceHeight(10, 10), 10);
            for (int i = 0; i < 20; i++)
            {
                world.Fear.Deposit(cell, 0.5f);
            }
            Assert.AreEqual(1f, world.Fear.GetAtColumn(10, 10), 1e-5f, "恐怖場が上限1.0で飽和していない");
        }

        [Test]
        public void Stats_WolfStepsAndFieldStatsAreRecorded()
        {
            var world = MakeDiorama(7u);
            for (int t = 0; t < 400; t++)
            {
                Simulation.Tick(world, world.Rng);
            }

            var (preyMean, preyMax) = EcologyStats.FieldStats(world.Prey);
            Assert.Greater(preyMax, 0f, "獲物場の最大値が0（誰も書いていない）");
            Assert.GreaterOrEqual(preyMax, preyMean, "最大が平均を下回っている");

            Assert.GreaterOrEqual(world.WolfStepCount, 0);
            Assert.GreaterOrEqual(EcologyStats.PredationPerKiloWolfStep(world), 0f);
            Assert.GreaterOrEqual(EcologyStats.HerbivoreFearExposure(world), 0f);
        }

        // ---- M4: 決定論 ----

        [Test]
        public void M4_SameSeedProducesIdenticalHashIncludingBothFields()
        {
            foreach (uint seed in k_Seeds)
            {
                var a = MakeDiorama(seed);
                var b = MakeDiorama(seed);
                for (int t = 0; t < 300; t++)
                {
                    Simulation.Tick(a, a.Rng);
                    Simulation.Tick(b, b.Rng);
                }

                Assert.AreEqual(a.ComputeContentHash(), b.ComputeContentHash(),
                    $"seed {seed} でハッシュが不一致（決定論が壊れている）");

                // 場が実際にハッシュへ入っていること（片方だけ書き換えると変わる）
                var c = MakeDiorama(seed);
                for (int t = 0; t < 300; t++)
                {
                    Simulation.Tick(c, c.Rng);
                }
                c.Fear.SetAtColumn(0, 0, c.Fear.GetAtColumn(0, 0) + 0.5f);
                Assert.AreNotEqual(a.ComputeContentHash(), c.ComputeContentHash(),
                    "恐怖場がコンテンツハッシュに含まれていない");
            }
        }

        [Test]
        public void M4_FieldsAreRegisteredInWorld()
        {
            var world = MakeDiorama(6u);
            Assert.IsTrue(world.Fields.ContainsKey(FearField.FieldName), "恐怖場が World.Fields に無い");
            Assert.IsTrue(world.Fields.ContainsKey(PreyField.FieldName), "獲物場が World.Fields に無い");
            Assert.IsTrue(world.Fields.ContainsKey(VegetationField.FieldName));
            Assert.IsTrue(world.Fields.ContainsKey(SuitabilityField.FieldName));
        }

        // ---- M5: 捕食率の維持 ----

        [Test]
        public void M5_PredationRateDoesNotHalveAfterFieldReplacement()
        {
            const int ticks = 2000;
            double sum = 0;

            foreach (uint seed in k_Seeds)
            {
                var world = MakeDiorama(seed);
                for (int t = 0; t < ticks; t++)
                {
                    Simulation.Tick(world, world.Rng);
                }
                sum += 1000.0 * world.PredationCount / ticks;
            }

            double after = sum / k_Seeds.Length;
            double ratio = after / k_BaselinePredationPer1000;

            Assert.Greater(ratio, 0.5,
                $"場読みへの置換で捕食率が半減した: {after:F2} 回/1000t " +
                $"(置換前 {k_BaselinePredationPer1000:F2} の {ratio:P0})");
        }
    }
}
