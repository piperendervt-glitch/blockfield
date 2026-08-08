using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    public class FaceVisibilityTests
    {
        [Test]
        public void SingleBlock_Has6VisibleFaces()
        {
            var grid = new VoxelGrid();
            grid.Set(new Int3(0, 0, 0), BlockId.Stone);

            Assert.AreEqual(6, FaceVisibility.CountVisibleFaces(grid));
        }

        [Test]
        public void TwoAdjacentBlocks_Have10VisibleFaces()
        {
            // 接する2面が消えて 12-2=10
            var grid = new VoxelGrid();
            grid.Set(new Int3(0, 0, 0), BlockId.Stone);
            grid.Set(new Int3(1, 0, 0), BlockId.Grass);

            Assert.AreEqual(10, FaceVisibility.CountVisibleFaces(grid));
        }

        [Test]
        public void AdjacentAcrossChunkBoundary_Have10VisibleFaces()
        {
            // (15,0,0) と (16,0,0) は別チャンク。境界面が正しく消えて 10
            var grid = new VoxelGrid();
            grid.Set(new Int3(15, 0, 0), BlockId.Stone);
            grid.Set(new Int3(16, 0, 0), BlockId.Grass);

            Assert.AreEqual(2, grid.ChunkCount, "前提: 2つの別チャンクにある");
            Assert.AreEqual(10, FaceVisibility.CountVisibleFaces(grid));

            // 個別確認: (15,0,0) の +X 面と (16,0,0) の -X 面が不可視
            Assert.IsFalse(FaceVisibility.IsFaceVisible(grid, new Int3(15, 0, 0), 0), "+X 面は隣接ブロックで隠れる");
            Assert.IsFalse(FaceVisibility.IsFaceVisible(grid, new Int3(16, 0, 0), 1), "-X 面は隣接ブロックで隠れる");
            Assert.IsTrue(FaceVisibility.IsFaceVisible(grid, new Int3(15, 0, 0), 1), "-X 面は可視");
        }
    }
}
