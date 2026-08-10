using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>Demo 8 第3段: 踏み荒らし場と、進化の基盤（重みの Entity 移管）。</summary>
    public class Demo8Stage3Tests
    {
        static readonly uint[] k_Seeds = { 12345u, 777u, 20260809u };

        /// <summary>踏み跡が積もるまでの時間。τ≈50 なので数百ティックで定常になる。</summary>
        const int k_Ticks = 2000;

        static World MakeDiorama(uint seed)
        {
            var tp = TerrainParams.Default;
            tp.seed = seed;
            tp.width = 50;
            tp.depth = 50;
            tp.maxHeight = 16;
            return World.Create(tp);
        }

        static World Run(uint seed, SimParams p, int ticks = k_Ticks)
        {
            var world = MakeDiorama(seed);
            for (int t = 0; t < ticks; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }
            return world;
        }

        // ---- J1: 踏み荒らし場への書き込み ----

        [Test]
        public void Trample_IsDepositedWhereAnimalsWalk()
        {
            var world = Run(k_Seeds[0], SimParams.Default, 500);

            var (mean, max) = EcologyStats.FieldStats(world.Trample);
            Assert.Greater(max, 0f, "踏み荒らし場に何も書かれていない（動物が歩いていない？）");
            Assert.Greater(mean, 0f);
            Assert.GreaterOrEqual(max, mean);
        }

        [Test]
        public void Trample_IsNotDepositedWhenNobodyMoves()
        {
            // 動物が1匹もいなければ踏み跡は立たない。
            // 「植物が書いている」等の取り違えをここで排除する
            var world = MakeDiorama(4u);
            var p = SimParams.Default;
            p.animalSpawnChance = 0f;

            for (int t = 0; t < 300; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }

            Assert.AreEqual(0, world.AnimalCount, "前提が成立していない（動物が湧いている）");
            var (_, max) = EcologyStats.FieldStats(world.Trample);
            Assert.AreEqual(0f, max, 1e-6f, "動物がいないのに踏み荒らし場が立っている");
        }

        [Test]
        public void Trample_DecaysFasterThanDeathAndSlowerThanFear()
        {
            // τの設計（踏まれた草が回復する速さ）を固定する。
            // ここが崩れると「通れば道になり、通らなければ戻る」が壊れる
            var p = SimParams.Default;
            Assert.Greater(p.trampleDecay, p.deathDecay,
                "踏み荒らしが死の場より長持ちしている（道が永久に残る）");
            Assert.Less(p.trampleDecay, p.fearDecay,
                "踏み荒らしが恐怖場より早く消えている（道として残らない）");
            Assert.AreEqual(1, p.trampleDiffusePasses,
                "拡散パス数が1から増えている。総量が頭打ちの場では広げるほど" +
                "1セルあたりが下がり、道の形が消える（第2段の実測）");
        }

        [Test]
        public void Trample_CrushesExistingPlants()
        {
            // 踏み潰しは「新しく生えない」だけでは足りないから入れた機構。
            // 実際に既存の植物が消えていること
            var world = Run(k_Seeds[0], SimParams.Default, k_Ticks);
            Assert.Greater(world.TrampleCrushCount, 0,
                "踏み潰しが一度も起きていない。閾値が高すぎるか確率が低すぎる");
        }

        [Test]
        public void Trample_CrushIsDisabledWhenChanceIsZero()
        {
            // 確率0のときは RNG を消費しないこと（消費すると対照実験がずれる）
            var p = SimParams.Default;
            p.trampleCrushChance = 0f;
            var world = Run(k_Seeds[0], p, 500);
            Assert.AreEqual(0, world.TrampleCrushCount);
        }

        // ---- M2: 踏み荒らしが植生を抑える ----

        /// <summary>
        /// 踏み跡の濃いセルでは植物が少ないこと。
        ///
        /// 【ここで対照比較を主張しない理由】
        /// 動物は餌のある所を歩き、そこで草を食べる。踏み跡の濃いセルは
        /// 「草が食べられた場所」でもあるので、抑制を切っても密度は下がる
        /// （対照でも比 0.28）。抑制そのものの寄与を切り出すには対照との比較が要るが、
        /// **その差はテスト規模では測れない**。48シードでは 2000/3000/5000ティックで
        /// 有効/対照 = 0.65 / 0.45 / 0.75 と一貫して1未満だが、
        /// 12シードでは 0.57 / 0.36 / 0.97、3シードでは 0.99 / 0.63 / 0.80 と
        /// 符号が消える組み合わせが出る（CLAUDE.md「生態系の判定は最低48シード」）。
        /// 対照比較は prereg のヘッドレス検証に記録し、ここでは
        /// **どの規模でも頑健な「踏み跡の草は明らかに少ない」**だけを固定する。
        ///
        /// 3シード×2,000ティック実測: 比 0.192（＝踏み跡の密度は静かな場所の約1/5）。
        /// </summary>
        [Test]
        public void M2_PlantsAreScarceOnTrampledCells()
        {
            float ratio = PooledTrampleRatio(SimParams.Default);

            Assert.Greater(ratio, 0f, "比が0。測定系が壊れている（踏み跡か植物が無い）");
            Assert.Less(ratio, 0.5f,
                $"踏み跡のセルで植物が減っていない（上位25%/下位25% = {ratio:F3}）");
        }

        [Test]
        public void M2_TrampleQuartilesActuallySeparate()
        {
            // 四分位が分かれていなければ M2 の比較は意味を持たない。
            // 死の場では上位25%の閾値がほぼ0になって比較が成立しなかった
            foreach (uint seed in k_Seeds)
            {
                var world = Run(seed, SimParams.Default);
                var (high, low) = EcologyStats.TrampleQuartileThresholds(world);
                Assert.Greater(high, 0f, $"seed {seed}: 上位25%の閾値が0（踏み跡が立っていない）");
                Assert.Greater(high, low * 2f,
                    $"seed {seed}: 四分位が分かれていない（上位 {high:F4} / 下位 {low:F4}）");
            }
        }

        /// <summary>3シードのセル数・植物数を合算してから比を取る（1シードでは揺れが大きい）。</summary>
        static float PooledTrampleRatio(SimParams p)
        {
            int highCells = 0, lowCells = 0, highPlants = 0, lowPlants = 0;
            foreach (uint seed in k_Seeds)
            {
                var world = Run(seed, p);
                var (high, low) = EcologyStats.TrampleQuartileThresholds(world);
                for (int z = 0; z < world.Depth; z++)
                {
                    for (int x = 0; x < world.Width; x++)
                    {
                        if (world.Suitability.GetAtColumn(x, z) <= 0f)
                        {
                            continue;
                        }
                        float v = world.Trample.GetAtColumn(x, z);
                        if (v >= high) highCells++;
                        else if (v <= low) lowCells++;
                    }
                }
                foreach (var e in world.Entities)
                {
                    if (!e.IsPlant)
                    {
                        continue;
                    }
                    float v = world.Trample.GetAtColumn(e.cell.x, e.cell.z);
                    if (v >= high) highPlants++;
                    else if (v <= low) lowPlants++;
                }
            }

            if (highCells == 0 || lowCells == 0 || lowPlants == 0)
            {
                return 0f;
            }
            return ((float)highPlants / highCells) / ((float)lowPlants / lowCells);
        }

        // ---- M4: 決定論 ----

        [Test]
        public void M4_TrampleFieldAndWeightsAreInContentHash()
        {
            var world = MakeDiorama(k_Seeds[0]);
            Assert.IsTrue(world.Fields.ContainsKey(TrampleField.FieldName),
                "踏み荒らし場が World.Fields に無い");

            var a = Run(k_Seeds[0], SimParams.Default, 300);
            var b = Run(k_Seeds[0], SimParams.Default, 300);
            Assert.AreEqual(a.ComputeContentHash(), b.ComputeContentHash(), "決定論が壊れている");

            var c = Run(k_Seeds[0], SimParams.Default, 300);
            c.Trample.SetAtColumn(0, 0, c.Trample.GetAtColumn(0, 0) + 0.5f);
            Assert.AreNotEqual(a.ComputeContentHash(), c.ComputeContentHash(),
                "踏み荒らし場がコンテンツハッシュに含まれていない");
        }

        [Test]
        public void M4_EntityWeightsChangeTheContentHash()
        {
            // 進化が入ると個体差そのものが世界の状態になる。
            // 重みがハッシュ対象でなければ、進化した集団と初期集団が同一に見えてしまう
            var world = MakeDiorama(5u);
            int x = 25, z = 25;
            var normal = SimParams.Default;
            var greedy = SimParams.Default;
            greedy.herbivoreVegetationWeight = 9f;

            var a = MakeDiorama(5u);
            a.TrySpawn(EntityKind.Sheep, x, z, 0, normal);
            var b = MakeDiorama(5u);
            b.TrySpawn(EntityKind.Sheep, x, z, 0, greedy);

            Assert.AreNotEqual(a.ComputeContentHash(), b.ComputeContentHash(),
                "個体の重みがコンテンツハッシュに含まれていない");
        }

        // ---- M6: 進化基盤の等価性 ----

        /// <summary>
        /// 重みを個体に移しても評価値が変わらないこと。
        ///
        /// 移管前は `w_veg × 植生 − w_fear × 恐怖`、移管後は
        /// 全ての場について `重み × 値` を名前昇順に合計する形。
        /// 重み0の項は 0×値 = 0 で、0 の加算は IEEE754 で厳密なので、
        /// 両者は**ビット単位で一致**するはずである。
        ///
        /// 48シード×1,000ティックでの全系検証（移管前のコミット 051e0a2 と
        /// 個体状態シグネチャを突き合わせ）も完全一致した。記録は prereg。
        /// </summary>
        [Test]
        public void M6_WeightedScoreMatchesThePreMigrationFormula()
        {
            var world = MakeDiorama(6u);
            var p = SimParams.Default;
            var weights = EntityWeights.ForagingFor(EntityKind.Sheep, p);

            // 場に色々な値を入れて、全セルで一致することを見る
            var rng = new BlockField.SimCore.Rng.Mulberry32(99u);
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    world.Vegetation.SetAtColumn(x, z, rng.NextFloat01());
                    world.Fear.SetAtColumn(x, z, rng.NextFloat01());
                    world.Prey.SetAtColumn(x, z, rng.NextFloat01());
                    world.Death.SetAtColumn(x, z, rng.NextFloat01());
                    world.Trample.SetAtColumn(x, z, rng.NextFloat01());
                }
            }

            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    float before = p.herbivoreVegetationWeight * world.Vegetation.GetAtColumn(x, z)
                        - p.herbivoreFearWeight * world.Fear.GetAtColumn(x, z);
                    float after = weights.Score(world, x, z);
                    Assert.AreEqual(before, after,
                        $"({x},{z}) で移管前後のスコアが違う: {before} vs {after}");
                }
            }
        }

        [Test]
        public void M6_WolfScoreIsThePreyFieldItself()
        {
            var world = MakeDiorama(7u);
            var weights = EntityWeights.ForagingFor(EntityKind.Wolf, SimParams.Default);
            world.Prey.SetAtColumn(10, 10, 0.37f);
            Assert.AreEqual(0.37f, weights.Score(world, 10, 10), 1e-7f,
                "狼のスコアが獲物場そのものになっていない");
        }

        [Test]
        public void M6_WeightOrderMatchesFieldNameOrder()
        {
            // 重みの並びは場の名前昇順（＝ハッシュの畳み込み順）と一致させる。
            // 場を足したときに並びがずれると、決定論が静かに壊れる
            var world = MakeDiorama(8u);
            var names = new List<string>(world.Fields.Keys);
            names.Sort(System.StringComparer.Ordinal);

            Assert.AreEqual(EntityWeights.FieldCount, names.Count,
                "EntityWeights.FieldCount が World の場の数と合っていない");
            CollectionAssert.AreEqual(names, EntityWeights.FieldNames,
                "EntityWeights の重みの並びが場の名前昇順と一致していない");
        }

        // ---- J2: 継承 ----

        [Test]
        public void Weights_AreInheritedByOffspringWithoutMutation()
        {
            // 本段では変異なし。親と子の重みが完全に一致すること。
            // 種ごとに見るのは、羊と豚が同一・狼だけ別の重みを持つため
            var world = Run(k_Seeds[0], SimParams.Default, k_Ticks);
            var p = SimParams.Default;

            int checkedAnimals = 0;
            foreach (var e in world.Entities)
            {
                if (!e.IsAnimal)
                {
                    continue;
                }
                checkedAnimals++;
                Assert.AreEqual(EntityWeights.ForagingFor(e.kind, p), e.forageWeights,
                    $"{e.kind} id={e.id} の採餌重みが初期値と違う（変異は未実装のはず）");
                Assert.AreEqual(EntityWeights.WanderingFor(e.kind, p), e.wanderWeights,
                    $"{e.kind} id={e.id} の徘徊重みが初期値と違う");
            }
            Assert.Greater(checkedAnimals, 0, "動物が1匹も残っていない");
        }

        [Test]
        public void Weights_AreCopiedByValueNotShared()
        {
            // 構造体にした理由そのもの。配列にすると親子で同じ実体を指し、
            // 変異を入れた瞬間に親の重みまで変わる
            var a = EntityWeights.ForagingFor(EntityKind.Sheep, SimParams.Default);
            var b = a;
            b.vegetation = 99f;
            Assert.AreNotEqual(b.vegetation, a.vegetation, "重みが参照で共有されている");
        }

        // ---- M5: 生態系の安定 ----

        /// <summary>
        /// 踏み荒らしを入れても生態系が保たれること。
        /// 48シード実測: 草食獣ギルドの全滅 0/48、植物の全滅 0/48、
        /// 狼の全滅 2/48（踏み荒らし無効の対照では 6/48 なので、
        /// むしろ改善している）。
        /// </summary>
        [Test]
        public void M5_EcosystemSurvivesWithTrampling()
        {
            foreach (uint seed in k_Seeds)
            {
                var world = MakeDiorama(seed);
                int minWolves = int.MaxValue, minHerbivores = int.MaxValue, minPlants = int.MaxValue;

                for (int t = 0; t < k_Ticks; t++)
                {
                    Simulation.Tick(world, world.Rng);
                    if (t < 300)
                    {
                        continue;
                    }
                    if (world.WolfCount < minWolves) minWolves = world.WolfCount;
                    int herbivores = world.SheepCount + world.PigCount;
                    if (herbivores < minHerbivores) minHerbivores = herbivores;
                    if (world.PlantCount < minPlants) minPlants = world.PlantCount;
                }

                Assert.Greater(minPlants, 0, $"seed {seed}: 植物が全滅した（踏み荒らしが強すぎる）");
                Assert.Greater(minHerbivores, 0, $"seed {seed}: 草食獣ギルドが全滅した");
                Assert.Greater(minWolves, 0, $"seed {seed}: 狼が全滅した");
            }
        }

        // ---- J3: 可視化 ----

        [Test]
        public void Display_TrampleHasItsOwnScale()
        {
            float trample = EcologyStats.FieldDisplayScale(TrampleField.FieldName);
            float death = EcologyStats.FieldDisplayScale(DeathField.FieldName);
            Assert.Greater(trample, death,
                "踏み荒らしの表示基準が死の場以下になっている（踏み跡の方が濃く出る）");

            // 実測の90%点 0.257 で十分な濃さになること
            float intensity = EcologyStats.FieldDisplayIntensity(0.257f, trample);
            Assert.Greater(intensity, 0.7f, $"90%点での濃さが {intensity:F2} しかない");
        }

        [Test]
        public void Display_WeightStatsAreReadable()
        {
            var world = Run(k_Seeds[0], SimParams.Default, 500);
            var (mean, variance, count) = EcologyStats.AnimalForageWeightStats(world);

            Assert.Greater(count, 0, "動物がいない");
            Assert.AreEqual(EntityWeights.FieldCount, mean.Length);
            Assert.AreEqual(EntityWeights.FieldCount, variance.Length);

            // 誰も踏み荒らし場を行動に使っていないので、その重みは全個体0
            int trampleIndex = System.Array.IndexOf(EntityWeights.FieldNames, TrampleField.FieldName);
            Assert.AreEqual(0f, mean[trampleIndex], 1e-6f);
            Assert.AreEqual(0f, variance[trampleIndex], 1e-6f);
        }
    }
}
