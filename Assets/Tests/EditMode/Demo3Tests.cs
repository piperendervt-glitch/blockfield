using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>Demo 3 (E1-E5): 植生場・摂食・捕食・繁殖・ログのテスト。</summary>
    public class Demo3Tests
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

        /// <summary>スポーンと移動を止めたパラメータ（シナリオを固定配置で検証するため）。</summary>
        static SimParams ScenarioParams()
        {
            var p = SimParams.Default;
            p.plantSpawnCandidates = 0;
            p.animalSpawnCandidates = 0;
            p.moveChance = 0f;
            p.turnChance = 0f;
            return p;
        }

        /// <summary>X方向に len 連続で suitability 1.0 かつ同一高さの行を探す。</summary>
        static (int x, int z) FindFlatRun(World world, int len)
        {
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x + len <= world.Width; x++)
                {
                    bool ok = true;
                    int h = world.GetSurfaceHeight(x, z);
                    for (int i = 0; i < len; i++)
                    {
                        if (world.Suitability.GetAtColumn(x + i, z) < 1f || world.GetSurfaceHeight(x + i, z) != h)
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok)
                    {
                        return (x, z);
                    }
                }
            }
            Assert.Fail("平坦な行が見つからない（テスト前提の不成立）");
            return (0, 0);
        }

        [Test]
        public void M4_NewPlantSpawns_ClusterNearExistingPlants()
        {
            // Demo 3 M4: 場の効果の定量判定（植物ダイナミクスのみで測定）。
            // ticks 100-200 の新規スポーンの過半が「スポーン時点で既存植物の3セル以内(Chebyshev)」
            var world = World.Create(WorldParams(5u));
            var p = SimParams.Default;
            p.animalSpawnCandidates = 0;

            int near = 0;
            int total = 0;
            var prevPlantIds = new HashSet<int>();
            var prevPlantCells = new List<Int3>();

            for (int t = 0; t < 200; t++)
            {
                prevPlantIds.Clear();
                prevPlantCells.Clear();
                foreach (var e in world.Entities)
                {
                    if (e.IsPlant)
                    {
                        prevPlantIds.Add(e.id);
                        prevPlantCells.Add(e.cell);
                    }
                }

                Simulation.Tick(world, world.Rng, p);

                if (t < 100)
                {
                    continue;
                }

                foreach (var e in world.Entities)
                {
                    if (!e.IsPlant || prevPlantIds.Contains(e.id))
                    {
                        continue;
                    }
                    total++;
                    foreach (var c in prevPlantCells)
                    {
                        if (System.Math.Max(System.Math.Abs(c.x - e.cell.x), System.Math.Abs(c.z - e.cell.z)) <= 3)
                        {
                            near++;
                            break;
                        }
                    }
                }
            }

            Assert.GreaterOrEqual(total, 20, "測定窓内の新規スポーンが少なすぎて判定できない");
            Assert.Greater((float)near / total, 0.5f,
                $"クラスタ化が不足: 近傍3セル以内 {near}/{total} ({(float)near / total:P0})");
        }

        [Test]
        public void Eating_AdjacentPlantIsConsumed_AndHungerResets()
        {
            var world = World.Create(WorldParams(1u));
            var p = ScenarioParams();
            var (x, z) = FindFlatRun(world, 2);
            world.TrySpawn(EntityKind.Sheep, x, z, 0);
            world.TrySpawn(EntityKind.GrassTuft, x + 1, z, 0);

            int eatenAt = -1;
            for (int t = 0; t < 80 && eatenAt < 0; t++)
            {
                Simulation.Tick(world, world.Rng, p);
                if (world.PlantCount == 0)
                {
                    eatenAt = t;
                }
            }

            Assert.GreaterOrEqual(eatenAt, 0, "80ティック以内に植物が食べられなかった");
            Assert.AreEqual(1, world.SheepCount, "羊が消えている");
            // hunger は摂食でリセットされている（摂食直後 < 摂食閾値0.5）
            foreach (var e in world.Entities)
            {
                if (e.kind == EntityKind.Sheep)
                {
                    Assert.Less(e.hunger, 0.5f, "摂食後も hunger が高いまま");
                }
            }
        }

        [Test]
        public void Starvation_WithoutPlants_HerbivoreDies()
        {
            var world = World.Create(WorldParams(1u));
            var p = ScenarioParams();
            var (x, z) = FindFlatRun(world, 1);
            world.TrySpawn(EntityKind.Sheep, x, z, 0);

            for (int t = 0; t < 130; t++) // hungerPerTick 0.01 → 餓死は100ティック目
            {
                Simulation.Tick(world, world.Rng, p);
            }

            Assert.AreEqual(0, world.AnimalCount, "植物ゼロの世界で草食獣が餓死していない");
            Assert.AreEqual(1, world.StarvationCount);
        }

        [Test]
        public void Predation_WolfEatsNearbySheep()
        {
            // Demo 8 で狼の追跡が「視界内の最近接へ直進」から「獲物場の勾配を追う」に
            // 変わり、この筋書きの前提が2つ崩れた:
            // 1. 旧実装の追跡 (ChaseStep) は moveChance を無視して動いていた。
            //    このシナリオは moveChance=0 で移動を止めているため、新実装の狼は
            //    通常の移動規則に従って**一歩も動けない**
            // 2. 3セル離れた状態からの追跡成否は匂いの育ち方に左右され、
            //    シードによって成功したりしなかったりする（実測 6シード中3成功）
            //
            // そこで本テストは**リファクタで保存された機構＝隣接捕食**の検証に絞る。
            // 匂いを辿って接近できることは Demo8Tests の場読みテストと、
            // M5（1000ティックあたり捕食回数が半減しない）で担保する。
            var world = World.Create(WorldParams(1u));
            var p = ScenarioParams();
            var (x, z) = FindFlatRun(world, 4);
            world.TrySpawn(EntityKind.Wolf, x, z, 0);
            world.TrySpawn(EntityKind.Sheep, x + 1, z, 0);

            // 狼は hunger > 0.5（51ティック目）から捕食モードに入る。
            // 羊が餓死する100ティックより前に捕食が起きること
            int predatedAt = -1;
            for (int t = 0; t < 100 && predatedAt < 0; t++)
            {
                Simulation.Tick(world, world.Rng, p);
                if (world.PredationCount > 0)
                {
                    predatedAt = t;
                }
            }

            Assert.GreaterOrEqual(predatedAt, 0, "100ティック以内に隣接する羊が捕食されなかった");
            Assert.AreEqual(0, world.SheepCount, "羊が残っている");
            Assert.AreEqual(0, world.StarvationCount, "餓死が起きている（捕食ではない可能性）");
        }

        [Test]
        public void Breeding_AdjacentFedPair_ProducesChild_AndSetsCooldown()
        {
            // シード1で出生がティック11に決定論的に発生することをハーネスで確認済み
            var world = World.Create(WorldParams(1u));
            var p = ScenarioParams();
            var (x, z) = FindFlatRun(world, 3);
            world.TrySpawn(EntityKind.Sheep, x, z, 0);
            world.TrySpawn(EntityKind.Sheep, x + 1, z, 0);

            int bornAt = -1;
            for (int t = 0; t < 100 && bornAt < 0; t++)
            {
                Simulation.Tick(world, world.Rng, p);
                if (world.SheepCount >= 3)
                {
                    bornAt = t;
                }
            }

            Assert.GreaterOrEqual(bornAt, 0, "100ティック以内に子が生まれなかった");
            Assert.GreaterOrEqual(world.BirthCount, 1);

            // クールダウン: 出生直後、関与個体（親2＋子）にクールダウンが設定されている
            int withCooldown = 0;
            foreach (var e in world.Entities)
            {
                if (e.IsAnimal && e.breedCooldown > 0)
                {
                    withCooldown++;
                }
            }
            Assert.GreaterOrEqual(withCooldown, 3, "繁殖後のクールダウンが設定されていない");
        }

        [Test]
        public void PopulationLog_OutputsHeaderAndRows()
        {
            var world = World.Create(WorldParams(1u));
            for (int t = 0; t < 50; t++)
            {
                Simulation.Tick(world, world.Rng, SimParams.Default);
            }

            string csv = world.PopulationLog.ToCsv();
            var lines = csv.TrimEnd('\r', '\n').Split('\n');
            Assert.AreEqual(51, lines.Length, "ヘッダ1行＋50ティック分の行が必要");
            Assert.AreEqual("tick,plants,sheep,pigs,wolves", lines[0].TrimEnd('\r'));
        }
    }
}
