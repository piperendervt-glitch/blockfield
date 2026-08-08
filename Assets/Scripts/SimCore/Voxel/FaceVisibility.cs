namespace BlockField.SimCore.Voxel
{
    /// <summary>
    /// 可視面判定 (Demo 1 B1)。「どの面を出すか」のロジックは SimCore に置き、
    /// メッシュ構築 (ChunkMesher) は Runtime 側でこの判定を使う。
    /// </summary>
    public static class FaceVisibility
    {
        /// <summary>面の数。</summary>
        public const int FaceCount = 6;

        /// <summary>
        /// 面インデックス→隣接方向。順序は +X, -X, +Y, -Y, +Z, -Z（ChunkMesher の面テーブルと対応）。
        /// </summary>
        public static readonly Int3[] Directions =
        {
            new Int3(1, 0, 0),
            new Int3(-1, 0, 0),
            new Int3(0, 1, 0),
            new Int3(0, -1, 0),
            new Int3(0, 0, 1),
            new Int3(0, 0, -1),
        };

        /// <summary>
        /// セルの指定面が可視（=隣接セルが Air）か。
        /// 隣接判定は VoxelGrid 経由なのでチャンク境界をまたぐ隣接も正しく評価される。
        /// </summary>
        public static bool IsFaceVisible(VoxelGrid grid, Int3 cell, int faceIndex)
        {
            return grid.Get(cell + Directions[faceIndex]) == BlockId.Air;
        }

        /// <summary>グリッド全体の可視面数（テスト・統計用）。</summary>
        public static int CountVisibleFaces(VoxelGrid grid)
        {
            int count = 0;
            foreach (var pair in grid.Chunks)
            {
                var baseCell = new Int3(
                    pair.Key.x * Chunk.Size,
                    pair.Key.y * Chunk.Size,
                    pair.Key.z * Chunk.Size);

                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        for (int x = 0; x < Chunk.Size; x++)
                        {
                            if (pair.Value.Get(x, y, z) == BlockId.Air)
                            {
                                continue;
                            }

                            var cell = baseCell + new Int3(x, y, z);
                            for (int f = 0; f < FaceCount; f++)
                            {
                                if (IsFaceVisible(grid, cell, f))
                                {
                                    count++;
                                }
                            }
                        }
                    }
                }
            }

            return count;
        }
    }
}
