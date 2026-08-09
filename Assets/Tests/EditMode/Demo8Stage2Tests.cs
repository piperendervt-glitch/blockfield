using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// Demo 8 第2段: 死の場のテスト。
    ///
    /// 【シードをまとめて集計する理由】墓場は 3,000ティックでも 100セル前後にしかならない。
    /// 1シードだけで植物密度を出すと、たまたま墓場に植物が1本も無い、といった揺れで
    /// 比が 0.00 にも 4.73 にもなる（実測）。シードごとに判定せず、
    /// 3シードのセル数・植物数を**合算してから**比を取ることで推定を安定させる。
    /// </summary>
    public class Demo8Stage2Tests
    {
        static readonly uint[] k_Seeds = { 12345u, 777u, 20260809u };

        /// <summary>墓場が育つまでの時間。死の場は τ≈333 なので短いと痕跡が溜まらない。</summary>
        const int k_Ticks = 3000;

        static World MakeDiorama(uint seed)
        {
            var tp = TerrainParams.Default;
            tp.seed = seed;
            tp.width = 50;
            tp.depth = 50;
            tp.maxHeight = 16;
            return World.Create(tp);
        }

        // ---- M2: 死骸が養分になる ----

        /// <summary>
        /// 養分効果は**対照との比較**で判定する。
        /// 「墓場の方が植物が多い」を直接見ることはできない。餓死は餌の乏しい所で
        /// 起きるので、墓場はもともと植物が少ない土地に偏るため
        /// （養分係数0の対照でも比は 0.33 しかない）。
        /// 係数を入れると同じ交絡のもとで比が上がる、という形でのみ効果を切り出せる。
        /// 実測: 対照 0.33 → 採用値 0.86（約2.6倍）。
        /// </summary>
        [Test]
        public void M2_NutrientBoostRaisesPlantDensityInGraveyardsVersusControl()
        {
            float withBoost = PooledGraveyardRatio(SimParams.Default.deathNutrientBoost);
            float control = PooledGraveyardRatio(0f);

            Assert.Greater(control, 0f, "対照の比が0。測定系が壊れている（墓場か植物が無い）");
            Assert.Greater(withBoost, control * 1.5f,
                $"養分係数が植物の分布を動かしていない: 係数あり {withBoost:F2} / 対照 {control:F2}。" +
                "実測では 0.86 / 0.33 だった");
        }

        /// <summary>
        /// 墓場の標本数そのものを固定する。閾値やパラメータを触って墓場が
        /// 数十セルまで減ると、M2 の比が雑音に埋もれて意味を失う
        /// （実測: 閾値0.05 では5シード中3つで比が 0.00 になった）。
        /// </summary>
        [Test]
        public void M2_GraveyardSampleIsLargeEnoughToMeasure()
        {
            foreach (uint seed in k_Seeds)
            {
                var world = Run(seed, SimParams.Default);
                int cells = CountGraveyardCells(world);
                Assert.GreaterOrEqual(cells, 40,
                    $"seed {seed}: 墓場が {cells} セルしかない。植物密度の推定が雑音に埋もれる");
            }
        }

        // ---- 死の場への書き込み（餓死・被食の両方）----

        [Test]
        public void DeathField_DepositsOnStarvation()
        {
            // 被食側の寄与を0にして、餓死だけで場が立つことを見る。
            // 減衰と拡散も止めて、合計値がそのまま「書き込まれた量」になるようにする
            var p = SimParams.Default;
            p.deathDecay = 0f;
            p.deathDiffuse = 0f;
            p.deathDepositPredated = 0f;

            var world = Run(k_Seeds[0], p, 1000);

            Assert.Greater(world.StarvationCount, 0, "餓死が一度も起きていない（前提が成立していない）");
            Assert.Greater(FieldSum(world), 0f, "餓死しても死の場に痕跡が残っていない");
        }

        [Test]
        public void DeathField_DepositsOnPredation()
        {
            var p = SimParams.Default;
            p.deathDecay = 0f;
            p.deathDiffuse = 0f;
            p.deathDepositStarved = 0f;

            var world = Run(k_Seeds[0], p, 1000);

            Assert.Greater(world.PredationCount, 0, "捕食が一度も起きていない（前提が成立していない）");
            Assert.Greater(FieldSum(world), 0f, "捕食されても死の場に痕跡が残っていない");
        }

        [Test]
        public void DeathField_StarvationLeavesMoreThanPredation()
        {
            // 死因で量を変える設計（餓死は死骸が残る／被食は肉が持ち去られる）が
            // 効いていること。同じにされたら「どんな死が起きたか」の情報が消える
            var p = SimParams.Default;
            Assert.Greater(p.deathDepositStarved, p.deathDepositPredated,
                "餓死と被食の痕跡の大きさが逆転または同一になっている");
        }

        // ---- M3: 迂回行動 ----

        /// <summary>
        /// 第1段で判定不能だった「恐怖場を避けているか」を作り直した指標で測る。
        /// **実際に動いた1歩**だけを、しかも**恐怖が近くにあるときだけ**数えるので、
        /// 動かなかったティックの希釈も、危険地帯で食われて消えた個体による
        /// 生存者バイアスも入らない。
        /// 実測 (2,000t): 既定 61.4/67.2/61.9% に対し w_fear=0 の対照は 56.3/56.9/53.5%。
        /// </summary>
        [Test]
        public void M3_HerbivoresMoveAwayFromFearMoreThanControl()
        {
            foreach (uint seed in k_Seeds)
            {
                var noFear = SimParams.Default;
                noFear.herbivoreFearWeight = 0f;

                var a = Run(seed, SimParams.Default, 2000);
                var b = Run(seed, noFear, 2000);

                float withFear = EcologyStats.FearAvoidanceRatio(a);
                float control = EcologyStats.FearAvoidanceRatio(b);
                int samples = a.HerbivoreMovesAwayFromFear + a.HerbivoreMovesTowardFear;

                Assert.Greater(samples, 300, $"seed {seed}: 標本 {samples} 歩では判定できない");
                Assert.Greater(withFear, control + 0.03f,
                    $"seed {seed}: 迂回が対照と区別できない（{withFear:P1} vs {control:P1}）");
            }
        }

        [Test]
        public void M3_AvoidanceIsCountedOnlyOnActualMoves()
        {
            // 分母が「動いた歩数」であること。総ティック数より必ず小さくなる
            var world = Run(k_Seeds[0], SimParams.Default, 500);
            int counted = world.HerbivoreMovesAwayFromFear + world.HerbivoreMovesTowardFear;
            Assert.Greater(counted, 0, "1歩も数えられていない");
            Assert.LessOrEqual(EcologyStats.FearAvoidanceRatio(world), 1f);
        }

        // ---- M4: 決定論 ----

        [Test]
        public void M4_DeathFieldIsRegisteredAndAffectsContentHash()
        {
            var world = MakeDiorama(k_Seeds[0]);
            Assert.IsTrue(world.Fields.ContainsKey(DeathField.FieldName), "死の場が World.Fields に無い");

            var a = Run(k_Seeds[0], SimParams.Default, 300);
            var b = Run(k_Seeds[0], SimParams.Default, 300);
            Assert.AreEqual(a.ComputeContentHash(), b.ComputeContentHash(),
                "死の場を足したことで決定論が壊れている");

            var c = Run(k_Seeds[0], SimParams.Default, 300);
            c.Death.SetAtColumn(0, 0, c.Death.GetAtColumn(0, 0) + 0.5f);
            Assert.AreNotEqual(a.ComputeContentHash(), c.ComputeContentHash(),
                "死の場がコンテンツハッシュに含まれていない");
        }

        [Test]
        public void M4_DisplayCountersAreNotWritableFromOutsideSimCore()
        {
            // 迂回の集計は表示専用（表示と真実の分離）。表示側から書けてしまうと
            // 「見るために数えた値」が世界の状態に混ざる。
            //
            // 本当は「カウンタを動かしてもハッシュが変わらないこと」を見たいが、
            // setter が internal なのでテストアセンブリからは代入できない
            // ＝**書けないこと自体がここで保証される性質**なので、それを固定する。
            // ハッシュ側の不変性は M4_DeathFieldIsRegisteredAndAffectsContentHash で見ている。
            foreach (string name in new[] { "HerbivoreMovesAwayFromFear", "HerbivoreMovesTowardFear" })
            {
                var setter = typeof(World).GetProperty(name).GetSetMethod();
                Assert.IsNull(setter, $"{name} の setter が public になっている（表示側から書ける）");
            }
        }

        // ---- M5: Demo 5b の安定条件が生きていること ----

        /// <summary>
        /// 死の場と養分効果を入れても狼が生き残ること。
        /// 養分係数を上げすぎると植物が墓場へ偏って狼が落ちる（実測で k≧40 だと
        /// 最小狼数が0になった）。k=20 を選んだのはこの制約があるため。
        /// 詳細な安定条件は <see cref="Demo5bTests"/> 側で見ているので、
        /// ここは「係数を上げると壊れる」という一点に絞る。
        /// </summary>
        [Test]
        public void M5_WolvesStillSurviveWithNutrientBoost()
        {
            foreach (uint seed in k_Seeds)
            {
                var world = MakeDiorama(seed);
                var p = SimParams.Default;
                int minWolves = int.MaxValue;

                for (int t = 0; t < 2000; t++)
                {
                    Simulation.Tick(world, world.Rng, p);
                    if (t >= 300 && world.WolfCount < minWolves)
                    {
                        minWolves = world.WolfCount;
                    }
                }

                Assert.Greater(minWolves, 0,
                    $"seed {seed}: 養分効果を入れたら狼が0になった（最小 {minWolves}）。" +
                    $"deathNutrientBoost={p.deathNutrientBoost} が高すぎる");
            }
        }

        // ---- 補助 ----

        static World Run(uint seed, SimParams p, int ticks = k_Ticks)
        {
            var world = MakeDiorama(seed);
            for (int t = 0; t < ticks; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }
            return world;
        }

        /// <summary>3シードのセル数・植物数を合算してから比を取る（1シードでは揺れが大きい）。</summary>
        static float PooledGraveyardRatio(float boost)
        {
            var p = SimParams.Default;
            p.deathNutrientBoost = boost;

            int graveCells = 0, otherCells = 0, gravePlants = 0, otherPlants = 0;
            foreach (uint seed in k_Seeds)
            {
                var world = Run(seed, p);
                for (int z = 0; z < world.Depth; z++)
                {
                    for (int x = 0; x < world.Width; x++)
                    {
                        if (world.Suitability.GetAtColumn(x, z) <= 0f)
                        {
                            continue;
                        }
                        if (world.Death.GetAtColumn(x, z) >= EcologyStats.GraveyardThreshold) graveCells++;
                        else otherCells++;
                    }
                }
                foreach (var e in world.Entities)
                {
                    if (!e.IsPlant)
                    {
                        continue;
                    }
                    if (world.Death.GetAtColumn(e.cell.x, e.cell.z) >= EcologyStats.GraveyardThreshold) gravePlants++;
                    else otherPlants++;
                }
            }

            if (graveCells == 0 || otherCells == 0 || otherPlants == 0)
            {
                return 0f;
            }
            return ((float)gravePlants / graveCells) / ((float)otherPlants / otherCells);
        }

        static int CountGraveyardCells(World world)
        {
            int cells = 0;
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.Suitability.GetAtColumn(x, z) > 0f &&
                        world.Death.GetAtColumn(x, z) >= EcologyStats.GraveyardThreshold)
                    {
                        cells++;
                    }
                }
            }
            return cells;
        }

        static float FieldSum(World world)
        {
            float sum = 0f;
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    sum += world.Death.GetAtColumn(x, z);
                }
            }
            return sum;
        }
    }
}
