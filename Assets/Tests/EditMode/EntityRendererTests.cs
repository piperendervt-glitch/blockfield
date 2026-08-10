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
        public void CollectRemovedVisualIds_AgainstRealWorld_StarvedAnimalIsCollected()
        {
            // 実ワールドで個体が消えた後、その id が破棄対象になることを end-to-end で検証。
            //
            // 【Demo 8.5 で対象が変わった】移行前は「食べられた植物」で見ていたが、
            // 草が場になり植物 Entity が存在しなくなったため、
            // **餓死した動物**で同じことを見る。表示側の関心
            // （World から消えた id の Visual を破棄する）は変わらない。
            var p = SimParams.Default;
            p.animalSpawnCandidates = 0;
            p.moveChance = 0f;
            p.turnChance = 0f;
            p.vegetationGrowth = 0f;   // 草を生やさない＝必ず餓死する
            p.vegetationFloor = 0f;
            p.hungerPerTick = 0.2f;    // 数ティックで餓死させる

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

            // 平坦セルを探して羊を2匹配置。片方だけ餓死させたいので
            // 世界には草を置かず、生存側は表示だけを模擬する
            (int x, int z) = FindFlatPair(world);
            int starvingId = world.TrySpawn(EntityKind.Sheep, x, z, 0);
            Assert.GreaterOrEqual(starvingId, 0);

            // 実在しない id も混ぜて「World に無い id は破棄対象」になることを見る
            const int ghostId = 99999;
            var visualIds = new List<int> { starvingId, ghostId };

            for (int t = 0; t < 40 && world.AnimalCount > 0; t++)
            {
                Simulation.Tick(world, world.Rng, p);
            }
            Assert.AreEqual(0, world.AnimalCount, "前提: 羊が餓死していること");

            var liveIds = new HashSet<int>();
            foreach (var e in world.Entities)
            {
                liveIds.Add(e.id);
            }
            var result = new List<int>();
            EntityRenderer.CollectRemovedVisualIds(liveIds, visualIds, result);

            CollectionAssert.Contains(result, starvingId, "餓死した羊の表示が破棄対象になっていない");
            CollectionAssert.Contains(result, ghostId, "World に存在しない id が破棄対象になっていない");
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
