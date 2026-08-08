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
            Assert.Greater(world.PlantCount, 0, "100ティックで植物が1つも湧いていない");
            Assert.Greater(world.AnimalCount, 0, "100ティックで動物が1匹も湧いていない");
        }

        [Test]
        public void Spawn_CapsAreNotExceeded()
        {
            var p = SimParams.Default;
            var world = CreateAndTick(2u, 1000, p);
            Assert.LessOrEqual(world.PlantCount, p.plantCap);
            Assert.LessOrEqual(world.AnimalCount, p.animalCap);
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
            // 移動を無効化してスポーン位置だけを検証する
            var p = SimParams.Default;
            p.moveChance = 0f;
            p.turnChance = 0f;

            var world = CreateAndTick(4u, 500, p);
            int animals = 0;
            foreach (var e in world.Entities)
            {
                if (!e.IsAnimal)
                {
                    continue;
                }
                animals++;
                Assert.AreEqual(1f, world.Suitability.Get(e.cell.x, e.cell.z),
                    $"動物 {e.kind} が suitability 1.0 以外のセル {e.cell} にスポーンした");
            }
            Assert.Greater(animals, 0, "検証対象の動物が湧いていない");
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
