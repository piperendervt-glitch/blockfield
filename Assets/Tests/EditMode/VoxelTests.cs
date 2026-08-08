using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    public class VoxelTests
    {
        [Test]
        public void Int3_Equality_And_HashCode()
        {
            var a = new Int3(1, -2, 3);
            var b = new Int3(1, -2, 3);
            var c = new Int3(3, -2, 1);

            Assert.IsTrue(a == b);
            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode(), "等価な Int3 のハッシュが一致しない");
            Assert.IsTrue(a != c);
            Assert.AreEqual(new Int3(4, -4, 4), a + c);
        }

        [Test]
        public void Chunk_SetGet_Roundtrip()
        {
            var chunk = new Chunk();
            Assert.AreEqual(BlockId.Air, chunk.Get(0, 0, 0), "初期値は Air");

            chunk.Set(0, 0, 0, BlockId.Grass);
            chunk.Set(15, 15, 15, BlockId.Stone);
            chunk.Set(7, 3, 11, BlockId.Dirt);

            Assert.AreEqual(BlockId.Grass, chunk.Get(0, 0, 0));
            Assert.AreEqual(BlockId.Stone, chunk.Get(15, 15, 15));
            Assert.AreEqual(BlockId.Dirt, chunk.Get(7, 3, 11));
        }

        [Test]
        public void VoxelGrid_UngeneratedChunk_ReturnsAir()
        {
            var grid = new VoxelGrid();
            Assert.AreEqual(BlockId.Air, grid.Get(new Int3(0, 0, 0)));
            Assert.AreEqual(BlockId.Air, grid.Get(new Int3(-100, 50, 9999)));
            Assert.AreEqual(0, grid.ChunkCount, "Get だけではチャンクは生成されない");

            // Air の書き込みもチャンクを生成しない
            grid.Set(new Int3(3, 3, 3), BlockId.Air);
            Assert.AreEqual(0, grid.ChunkCount);
        }

        [Test]
        public void VoxelGrid_SetGet_AcrossChunkBoundary()
        {
            var grid = new VoxelGrid();

            // チャンク境界 (15→16) をまたぐ隣接セル
            grid.Set(new Int3(15, 0, 0), BlockId.Grass);
            grid.Set(new Int3(16, 0, 0), BlockId.Stone);
            // 負座標側の境界 (-1 はチャンク(-1,0,0) のローカル15)
            grid.Set(new Int3(-1, 0, 0), BlockId.Sand);

            Assert.AreEqual(BlockId.Grass, grid.Get(new Int3(15, 0, 0)));
            Assert.AreEqual(BlockId.Stone, grid.Get(new Int3(16, 0, 0)));
            Assert.AreEqual(BlockId.Sand, grid.Get(new Int3(-1, 0, 0)));
            Assert.AreEqual(3, grid.ChunkCount, "3つの異なるチャンクに書かれるはず");

            // 座標変換の検証（シフト/マスクの負座標対応）
            Assert.AreEqual(new Int3(-1, 0, 0), VoxelGrid.WorldToChunk(new Int3(-1, 0, 0)));
            Assert.AreEqual(new Int3(15, 0, 0), VoxelGrid.WorldToLocal(new Int3(-1, 0, 0)));
            Assert.AreEqual(new Int3(-1, 0, 0), VoxelGrid.WorldToChunk(new Int3(-16, 0, 0)));
            Assert.AreEqual(new Int3(0, 0, 0), VoxelGrid.WorldToLocal(new Int3(-16, 0, 0)));
        }
    }
}
