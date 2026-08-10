using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    public class EcologyTests
    {
        static TerrainParams WorldParams(uint seed) => new TerrainParams
        {
            seed = seed,
            width = 50,
            depth = 50,
            maxHeight = 16,
            reliefScale = 12f,
            plainsAmplitude = 0.25f,
            mountainAmplitude = 1f,
        };

        /// <summary>適性セル数が2倍以上になる広いワールド（密度の比較用）。</summary>
        static TerrainParams LargeWorldParams(uint seed)
        {
            var p = WorldParams(seed);
            p.width = 80;
            p.depth = 80;
            return p;
        }

        static World CreateAndTick(uint seed, int ticks, SimParams p)
        {
            var world = World.Create(WorldParams(seed));
            for (int i = 0; i < ticks; i++)
            {
                Simulation.Tick(world, world.Rng, p);
            }
            return world;
        }

        [Test]
        public void M3_SameSeedAndTicks_ProduceIdenticalWorldContentHash()
        {
            // Demo 2 M3: 地形＋エンティティ＋場＋ティックを含むワールドハッシュの決定論
            ulong hash1 = CreateAndTick(777u, 100, SimParams.Default).ComputeContentHash();
            ulong hash2 = CreateAndTick(777u, 100, SimParams.Default).ComputeContentHash();
            Assert.AreEqual(hash1, hash2, "同一シード＋同一ティック数でハッシュが不一致（決定論が壊れている）");

            ulong hash3 = CreateAndTick(778u, 100, SimParams.Default).ComputeContentHash();
            Assert.AreNotEqual(hash1, hash3, "異なるシードで同一ハッシュは異常");
        }

        [Test]
        public void Spawn_After100Ticks_PlantsAndAnimalsExist()
        {
            var world = CreateAndTick(1u, 100, SimParams.Default);
            Assert.Greater(world.VegetationTotal, 0f, "100ティックで草が育っていない");
            Assert.Greater(world.AnimalCount, 0, "100ティックで動物が1匹も湧いていない");
        }

        [Test]
        public void Spawn_CapsAreNotExceeded()
        {
            var p = SimParams.Default;
            var world = CreateAndTick(2u, 1000, p);

            // Demo 5a: 上限は基準スケールの値なので、ワールドの適性セル数へ換算してから比べる
            var resolved = p.Resolve(world.SuitableCellCount);
            Assert.LessOrEqual(world.VegetationTotal, world.Width * world.Depth, "草の総量がセル数×1.0を超えている");
            Assert.LessOrEqual(world.AnimalCount, resolved.animalCap);
        }

        [Test]
        public void Density_ResolveScalesWithSuitableCells()
        {
            var p = SimParams.Default;

            // 基準スケールちょうどなら既定値そのまま（箱庭の従来挙動が変わらないことの担保）
            var atReference = p.Resolve(SimParams.ReferenceSuitableCells);
            Assert.AreEqual(p.plantCap, atReference.plantCap);
            Assert.AreEqual(p.animalCap, atReference.animalCap);
            Assert.AreEqual(p.plantSpawnCandidates, atReference.plantSpawnCandidates);

            // 2倍の広さなら上限も頻度も2倍
            var doubled = p.Resolve(SimParams.ReferenceSuitableCells * 2);
            Assert.AreEqual(p.plantCap * 2, doubled.plantCap);
            Assert.AreEqual(p.animalCap * 2, doubled.animalCap);
            Assert.AreEqual(p.plantSpawnCandidates * 2, doubled.plantSpawnCandidates);

            // 確率・速度は密度と無関係なので換算しない
            Assert.AreEqual(p.moveChance, doubled.moveChance);
            Assert.AreEqual(p.hungerPerTick, doubled.hungerPerTick);
            Assert.AreEqual(p.vegetationDecay, doubled.vegetationDecay);
        }

        [Test]
        public void Density_ZeroCandidatesStayZeroAndTinyWorldsKeepOne()
        {
            var frozen = SimParams.Default;
            frozen.plantSpawnCandidates = 0;
            frozen.animalSpawnCandidates = 0;

            // 0 は「無効」の意味。スケールしても 0 のまま（テストがスポーンを止めるのに使う）
            var big = frozen.Resolve(SimParams.ReferenceSuitableCells * 10);
            Assert.AreEqual(0, big.plantSpawnCandidates);
            Assert.AreEqual(0, big.animalSpawnCandidates);

            // 0 より大きい値は、極端に小さいワールドでも 1 を下回らない
            var tiny = SimParams.Default.Resolve(1);
            Assert.AreEqual(1, tiny.plantSpawnCandidates);
            Assert.AreEqual(1, tiny.plantCap);
            Assert.AreEqual(1, tiny.animalCap);
        }

        [Test]
        public void Stats_DensitiesAreRelativeToSuitableCells()
        {
            var world = CreateAndTick(11u, 800, SimParams.Default);

            Assert.Greater(world.VegetationTotal, 0f, "草が育っていない（テストが空回りしている）");
            Assert.AreEqual(
                world.VegetationTotal / world.SuitableCellCount,
                EcologyStats.PlantDensity(world), 1e-6f);
            Assert.AreEqual(
                (float)world.AnimalCount / world.SuitableCellCount,
                EcologyStats.AnimalDensity(world), 1e-6f);
        }

        [Test]
        public void Stats_FeedCountersAreRecordedAndBounded()
        {
            var world = CreateAndTick(12u, 800, SimParams.Default);

            Assert.Greater(world.FeedAttemptCount, 0, "摂食試行が記録されていない");
            Assert.LessOrEqual(world.FeedSuccessCount, world.FeedAttemptCount,
                "成功が試行を上回っている");

            float rate = EcologyStats.FeedSuccessRate(world);
            Assert.GreaterOrEqual(rate, 0f);
            Assert.LessOrEqual(rate, 1f);
            Assert.AreEqual((float)world.FeedSuccessCount / world.FeedAttemptCount, rate, 1e-6f);
        }

        [Test]
        public void Stats_DiagnosticCountersDoNotAffectContentHash()
        {
            // 表示と真実の分離: 診断用の統計は導出値であり、決定論に影響しない。
            // 同一シードの2ワールドでハッシュが一致し、かつ統計も同じ値になること
            var a = CreateAndTick(13u, 300, SimParams.Default);
            var b = CreateAndTick(13u, 300, SimParams.Default);

            Assert.AreEqual(a.ComputeContentHash(), b.ComputeContentHash());
            Assert.AreEqual(a.FeedAttemptCount, b.FeedAttemptCount);
            Assert.AreEqual(a.FeedSuccessCount, b.FeedSuccessCount);
            Assert.Greater(a.FeedAttemptCount, 0);
        }

        [Test]
        public void Stats_StarvationIsNormalisedPerAnimal()
        {
            var world = CreateAndTick(14u, 1000, SimParams.Default);

            // 分母は延べ生存ティック数（各ティックの動物数の総和）
            long animalTicks = 0;
            for (int i = 0; i < world.PopulationLog.Count; i++)
            {
                animalTicks += world.PopulationLog.GetSample(i).Animals;
            }
            Assert.Greater(animalTicks, 0);

            Assert.AreEqual(
                1000f * world.StarvationCount / animalTicks,
                EcologyStats.StarvationPerAnimalPerKiloTick(world), 1e-4f);
        }

        [Test]
        public void Stats_PopulationLogSampleSplitsHerbivoresAndWolves()
        {
            var world = CreateAndTick(15u, 200, SimParams.Default);
            var last = world.PopulationLog.GetSample(world.PopulationLog.Count - 1);

            Assert.AreEqual(world.TickCount, last.tick + 1,
                "最後の記録はティック加算の直前に取られる");
            Assert.AreEqual((int)world.VegetationTotal, last.plants);
            Assert.AreEqual(world.SheepCount + world.PigCount, last.herbivores);
            Assert.AreEqual(world.WolfCount, last.wolves);
            Assert.AreEqual(world.AnimalCount, last.Animals);
        }

        [Test]
        public void Density_PlantDensityMatchesAcrossScales()
        {
            // Demo 5a の目的そのもの: 広さが変わっても定常の植物密度が同じになること。
            // 同一シード・同一地形で、適性セル数だけが違う2つのワールドを比べる
            var small = World.Create(WorldParams(9u));
            var large = World.Create(LargeWorldParams(9u));

            for (int i = 0; i < 1500; i++)
            {
                Simulation.Tick(small, small.Rng, SimParams.Default);
                Simulation.Tick(large, large.Rng, SimParams.Default);
            }

            Assert.Greater(large.SuitableCellCount, small.SuitableCellCount * 2,
                "広い方が十分に広くない（テストが意味を成さない）");

            double dSmall = (double)small.VegetationTotal / small.SuitableCellCount;
            double dLarge = (double)large.VegetationTotal / large.SuitableCellCount;

            // 上限に張り付く水準まで育つので、密度は 10% 以内に収まるはず
            Assert.AreEqual(dSmall, dLarge, dSmall * 0.1,
                $"植物密度がスケールで揃っていない: 小={dSmall:F4} 大={dLarge:F4}");
        }

        [Test]
        public void Spawn_AllEntitiesOccupyUniqueCells()
        {
            var world = CreateAndTick(3u, 500, SimParams.Default);
            var seen = new HashSet<Int3>();
            foreach (var e in world.Entities)
            {
                Assert.IsTrue(seen.Add(e.cell), $"セル {e.cell} に複数エンティティ（同一セル二重スポーン/移動）");
            }
        }

        [Test]
        public void Animals_SpawnOnlyOnFullSuitabilityCells()
        {
            // 検証対象は「野生スポーン (SpawnAnimals) は suitability 1.0 のセルにしか湧かない」。
            // スポーン位置だけを見たいので、そこから動く経路を全て塞ぐ:
            // - moveChance/turnChance = 0 で徘徊を止める
            // - breedChance = 0 で出生を止める（Breed は隣接空きセルへ高低差だけで
            //   子を置くので、適性 0.5 のセルにも湧きうる。野生スポーンとは別の規則）
            // 狼だけは除外する。捕食モードの ChaseStep は moveChance を見ずに動くため、
            // 現在位置がスポーン位置とは限らない（Demo 5a で個体数が増えて顕在化した）。
            var p = SimParams.Default;
            p.moveChance = 0f;
            p.turnChance = 0f;
            p.breedChance = 0f;

            var world = CreateAndTick(4u, 500, p);
            int checkedAnimals = 0;
            foreach (var e in world.Entities)
            {
                if (!e.IsHerbivore)
                {
                    continue;
                }
                checkedAnimals++;
                Assert.AreEqual(1f, world.Suitability.GetAtColumn(e.cell.x, e.cell.z),
                    $"動物 {e.kind} が suitability 1.0 以外のセル {e.cell} にスポーンした");
            }
            Assert.Greater(checkedAnimals, 0, "検証対象の動物が湧いていない");
        }

        [Test]
        public void Wander_ClimbsHeightDifference_AndStaysInBounds()
        {
            var world = World.Create(WorldParams(1u)); // 山を含むシード（高低差あり）
            var p = SimParams.Default;

            int climbMoves = 0;
            var previous = new Dictionary<int, Int3>();

            for (int t = 0; t < 1000; t++)
            {
                previous.Clear();
                foreach (var e in world.Entities)
                {
                    if (e.IsAnimal)
                    {
                        previous[e.id] = e.cell;
                    }
                }

                Simulation.Tick(world, world.Rng, p);

                foreach (var e in world.Entities)
                {
                    if (!e.IsAnimal)
                    {
                        continue;
                    }

                    // 地形外に出ない
                    Assert.IsTrue(world.InBounds(e.cell.x, e.cell.z), $"動物が地形外に出た: {e.cell}");

                    if (previous.TryGetValue(e.id, out var prev) && prev != e.cell)
                    {
                        int dy = System.Math.Abs(e.cell.y - prev.y);
                        Assert.LessOrEqual(dy, 1, "1回の移動で高低差2以上を移動した");
                        if (dy == 1)
                        {
                            climbMoves++;
                        }
                    }
                }
            }

            Assert.GreaterOrEqual(climbMoves, 1, "1000ティックで高低差1の昇降移動が一度も発生しなかった");
        }
    }
}
