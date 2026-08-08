using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    public class TerrainTests
    {
        static TerrainParams SmallParams(uint seed) => new TerrainParams
        {
            seed = seed,
            width = 50,
            depth = 50,
            maxHeight = 16,
            reliefScale = 12f,
            plainsAmplitude = 0.25f,
            mountainAmplitude = 1f,
        };

        [Test]
        public void ValueNoise_SameSeed_SameCoords_SameValue()
        {
            var a = new ValueNoise(42u);
            var b = new ValueNoise(42u);
            for (int i = 0; i < 50; i++)
            {
                float x = i * 0.37f;
                float z = i * 1.21f;
                Assert.AreEqual(a.Sample(x, z), b.Sample(x, z), $"({x}, {z}) で不一致");
                Assert.AreEqual(a.Fbm(x, z, 4), b.Fbm(x, z, 4), $"Fbm({x}, {z}) で不一致");
            }
        }

        [Test]
        public void ValueNoise_Range_IsZeroToOne()
        {
            var noise = new ValueNoise(7u);
            for (int i = 0; i < 500; i++)
            {
                float v = noise.Fbm(i * 0.13f, i * 0.29f, 4);
                Assert.GreaterOrEqual(v, 0f);
                Assert.Less(v, 1f);
            }
        }

        [Test]
        public void M3_SameParams_ProduceIdenticalContentHash()
        {
            // Demo 1 M3: 同一シードで2回生成した地形のハッシュが一致する（決定論）
            var p = SmallParams(12345u);
            ulong hash1 = TerrainGenerator.Generate(p).ComputeContentHash();
            ulong hash2 = TerrainGenerator.Generate(p).ComputeContentHash();
            Assert.AreEqual(hash1, hash2, "同一パラメータの生成結果ハッシュが不一致（決定論が壊れている）");
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentContentHash()
        {
            ulong hash1 = TerrainGenerator.Generate(SmallParams(1u)).ComputeContentHash();
            ulong hash2 = TerrainGenerator.Generate(SmallParams(2u)).ComputeContentHash();
            Assert.AreNotEqual(hash1, hash2, "異なるシードで同一ハッシュは異常");
        }

        [Test]
        public void Heights_AreWithinRange_And_SurfaceIsGrassOrStone()
        {
            var p = SmallParams(999u);
            var grid = TerrainGenerator.Generate(p);

            for (int z = 0; z < p.depth; z++)
            {
                for (int x = 0; x < p.width; x++)
                {
                    // 最上段の非Airブロックを探す
                    int top = -1;
                    for (int y = p.maxHeight; y >= 0; y--)
                    {
                        if (grid.Get(new Int3(x, y, z)) != BlockId.Air)
                        {
                            top = y;
                            break;
                        }
                    }

                    int height = top + 1;
                    Assert.GreaterOrEqual(height, 1, $"柱 ({x}, {z}) の高さが 1 未満");
                    Assert.LessOrEqual(height, p.maxHeight, $"柱 ({x}, {z}) の高さが maxHeight 超過");
                    Assert.AreEqual(BlockId.Air, grid.Get(new Int3(x, p.maxHeight, z)), $"柱 ({x}, {z}) が maxHeight を超えている");

                    var surface = grid.Get(new Int3(x, top, z));
                    Assert.IsTrue(surface == BlockId.Grass || surface == BlockId.Stone,
                        $"柱 ({x}, {z}) の表層が {surface}（Grass/Stone 以外）");

                    // 柱は隙間なく詰まっている（y=0..top まで非Air）
                    for (int y = 0; y <= top; y++)
                    {
                        Assert.AreNotEqual(BlockId.Air, grid.Get(new Int3(x, y, z)), $"柱 ({x}, {z}) の y={y} に空洞");
                    }
                }
            }
        }
    }
}
