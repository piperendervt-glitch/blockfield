using System.Collections.Generic;
using BlockField;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    /// <summary>
    /// Demo 3 退行の回帰テスト: World から消えたエンティティの表示エントリが
    /// 破棄対象として検出されること（EntityRenderer の破棄判定ロジック）。
    /// </summary>
    public class EntityRendererTests
    {
        [Test]
        public void CollectRemovedVisualIds_DetectsIdsMissingFromWorld()
        {
            var liveIds = new HashSet<int> { 1, 3, 5 };
            var visualIds = new List<int> { 1, 2, 3, 4, 5 };
            var result = new List<int>();

            EntityRenderer.CollectRemovedVisualIds(liveIds, visualIds, result);

            CollectionAssert.AreEquivalent(new[] { 2, 4 }, result, "World に存在しない id が破棄対象になっていない");
        }

        [Test]
        public void CollectRemovedVisualIds_EmptyWhenAllAlive()
        {
            var liveIds = new HashSet<int> { 1, 2 };
            var result = new List<int> { 99 }; // Clear されることも確認

            EntityRenderer.CollectRemovedVisualIds(liveIds, new List<int> { 1, 2 }, result);

            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void CollectRemovedVisualIds_AgainstRealWorld_EatenPlantIsCollected()
        {
            // 実ワールドで植物が食べられた後、その id が破棄対象になることを end-to-end で検証
            var p = SimParams.Default;
            p.plantSpawnCandidates = 0;
            p.animalSpawnCandidates = 0;
            p.moveChance = 0f;
            p.turnChance = 0f;

            var tp = new TerrainParams
            {
                seed = 1u,
                width = 50,
                depth = 50,
                maxHeight = 16,
                reliefScale = 12f,
                plainsAmplitude = 0.25f,
                mountainAmplitude = 1f,
            };
            var world = World.Create(tp);

            // 平坦セルを探して羊と植物を隣接配置
            (int x, int z) = FindFlatPair(world);
            int sheepId = world.TrySpawn(EntityKind.Sheep, x, z, 0);
            int plantId = world.TrySpawn(EntityKind.GrassTuft, x + 1, z, 0);
            Assert.GreaterOrEqual(sheepId, 0);
            Assert.GreaterOrEqual(plantId, 0);

            // 表示側は両方を表示している状態を模擬
            var visualIds = new List<int> { sheepId, plantId };

            for (int t = 0; t < 80 && world.PlantCount > 0; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }
            Assert.AreEqual(0, world.PlantCount, "前提: 植物が食べられていること");

            var liveIds = new HashSet<int>();
            foreach (var e in world.Entities)
            {
                liveIds.Add(e.id);
            }
            var result = new List<int>();
            EntityRenderer.CollectRemovedVisualIds(liveIds, visualIds, result);

            CollectionAssert.Contains(result, plantId, "食べられた植物の表示が破棄対象になっていない");
            CollectionAssert.DoesNotContain(result, sheepId, "生存中の羊が誤って破棄対象になっている");
        }

        static (int x, int z) FindFlatPair(World world)
        {
            for (int z = 0; z < world.Depth; z++)
            {
                for (int x = 0; x + 2 <= world.Width; x++)
                {
                    int h = world.GetSurfaceHeight(x, z);
                    if (world.Suitability.GetAtColumn(x, z) >= 1f
                        && world.Suitability.GetAtColumn(x + 1, z) >= 1f
                        && world.GetSurfaceHeight(x + 1, z) == h)
                    {
                        return (x, z);
                    }
                }
            }
            Assert.Fail("平坦ペアが見つからない");
            return (0, 0);
        }
    }
}
