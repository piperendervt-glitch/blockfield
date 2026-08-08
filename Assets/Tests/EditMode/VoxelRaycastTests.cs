using BlockField.SimCore.Voxel;
using NUnit.Framework;

namespace BlockField.Tests.EditMode
{
    public class VoxelRaycastTests
    {
        [Test]
        public void HorizontalRay_HitsFirstBlock_WithEntryFaceNormal()
        {
            var grid = new VoxelGrid();
            grid.Set(new Int3(5, 0, 0), BlockId.Stone);
            grid.Set(new Int3(7, 0, 0), BlockId.Stone); // 後方のブロックは無視される

            bool hit = VoxelRaycast.Raycast(grid, 0.5f, 0.5f, 0.5f, 1f, 0f, 0f, 20f,
                out var cell, out var normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(new Int3(5, 0, 0), cell);
            Assert.AreEqual(new Int3(-1, 0, 0), normal, "-X 面から入射したはず");
        }

        [Test]
        public void VerticalRay_Down_HitsTopFace()
        {
            var grid = new VoxelGrid();
            grid.Set(new Int3(3, 2, 3), BlockId.Grass);

            bool hit = VoxelRaycast.Raycast(grid, 3.5f, 10.5f, 3.5f, 0f, -1f, 0f, 20f,
                out var cell, out var normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(new Int3(3, 2, 3), cell);
            Assert.AreEqual(new Int3(0, 1, 0), normal, "+Y (上面) から入射したはず");
        }

        [Test]
        public void DiagonalRay_HitsBlock()
        {
            var grid = new VoxelGrid();
            grid.Set(new Int3(4, 0, 4), BlockId.Dirt);

            bool hit = VoxelRaycast.Raycast(grid, 0.5f, 0.5f, 0.5f, 1f, 0f, 1f, 20f,
                out var cell, out var normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(new Int3(4, 0, 4), cell);
            Assert.IsTrue(normal == new Int3(-1, 0, 0) || normal == new Int3(0, 0, -1),
                $"斜め入射の法線が軸方向でない: {normal}");
        }

        [Test]
        public void Miss_ReturnsFalse()
        {
            var grid = new VoxelGrid();
            grid.Set(new Int3(5, 5, 5), BlockId.Stone);

            // 届かない距離
            Assert.IsFalse(VoxelRaycast.Raycast(grid, 0.5f, 5.5f, 5.5f, 1f, 0f, 0f, 2f, out _, out _));
            // 何もない方向
            Assert.IsFalse(VoxelRaycast.Raycast(grid, 0.5f, 0.5f, 0.5f, -1f, 0f, 0f, 50f, out _, out _));
        }

        [Test]
        public void StartInsideSolid_ReturnsStartCellWithZeroNormal()
        {
            var grid = new VoxelGrid();
            grid.Set(new Int3(2, 2, 2), BlockId.Stone);

            bool hit = VoxelRaycast.Raycast(grid, 2.5f, 2.5f, 2.5f, 1f, 0f, 0f, 10f,
                out var cell, out var normal);

            Assert.IsTrue(hit);
            Assert.AreEqual(new Int3(2, 2, 2), cell);
            Assert.AreEqual(new Int3(0, 0, 0), normal);
        }
    }
}
