using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Terrain
{
    /// <summary>部屋の外殻の合成パラメータ (Demo 4.5b V2)。</summary>
    public struct RoomShellParams
    {
        /// <summary>床下に敷く層数（地面の厚みを見せる）。</summary>
        public int underFloorLayers;

        /// <summary>Ceiling 平面が無い場合に、最上面から何セル上を天井とみなすか。</summary>
        public int fallbackCeilingMargin;

        public static RoomShellParams Default => new RoomShellParams
        {
            underFloorLayers = 3,
            fallbackCeilingMargin = 12, // 0.48m
        };
    }

    /// <summary>部屋の外殻の合成結果。</summary>
    public sealed class RoomShellResult
    {
        /// <summary>外殻のグリッド。地形グリッドとは**別**に持つ（下記の理由）。</summary>
        public VoxelGrid Grid;

        public int WallBlocks;
        public int CeilingBlocks;
        public int UnderFloorBlocks;

        /// <summary>スキャンした部屋メッシュ全体から埋めたセル数（家具・棚の側面など）。</summary>
        public int MeshBlocks;

        public int CeilingCellY;
        public int FloorCellY;

        public int TotalBlocks => WallBlocks + CeilingBlocks + UnderFloorBlocks + MeshBlocks;
    }

    /// <summary>
    /// 部屋の外殻（壁・天井・床下）のボクセル化 (Demo 4.5b V2)。
    ///
    /// 【なぜ地形グリッドと分けるか】
    /// 1. 天井を地形グリッドに入れると、World の表層高さ探索が「最上面＝天井」を拾い、
    ///    生態系が全滅する（湧き場所も移動判定も壊れる）。
    /// 2. MRモードでは外殻を描いてはいけない（現実の部屋が見えなくなる）。
    ///    別グリッド＝別 GameObject にすることで表示の ON/OFF が素直にできる。
    ///
    /// 【面の向き】ChunkMesher の可視面判定は「隣接が Air なら面を出す」なので、
    /// 壁の**室内側**の面は自動的に出る。法線は外向き（＝室内向き）になるため、
    /// 部屋の内側から見て正しく描かれる。
    ///
    /// 【地形との重なり】G4 の壁ブロック（地形グリッド側）と同じセルを埋めると
    /// 同一平面の面が2枚できて Z ファイトするため、地形グリッドで埋まっているセルは
    /// 飛ばす（<paramref name="terrainGrid"/>）。
    /// </summary>
    public static class RoomShellComposer
    {
        /// <summary>
        /// 部屋の外殻を合成する。
        /// </summary>
        /// <param name="meshVertices">
        /// スキャンした部屋メッシュ（ワールド座標）。渡すと家具・棚の側面・机の脚まで
        /// ボクセル化する。**表示専用**であり M4 の保証対象外（生メッシュのアーカイブと同じ扱い）。
        /// null なら壁・天井・床下だけを作る。
        /// </param>
        public static RoomShellResult Compose(
            RoomObservation observation, VoxelGrid terrainGrid, RoomShellParams p,
            float[] meshVertices = null, int[] meshTriangles = null)
        {
            if (observation == null)
            {
                throw new System.ArgumentNullException(nameof(observation));
            }
            if (p.underFloorLayers < 0) p.underFloorLayers = 0;

            var grid = new VoxelGrid();
            var result = new RoomShellResult { Grid = grid };

            int floorCellY = SnowfallComposer.FindBaseCellY(observation);
            int ceilingCellY = ResolveCeilingCellY(observation, p, floorCellY);
            result.FloorCellY = floorCellY;
            result.CeilingCellY = ceilingCellY;

            // 部屋メッシュ全体の表面（家具・棚の側面・机の脚・荷物）。
            // 壁や天井より先に埋めておくと、後段の TrySet が二重に数えない
            if (meshVertices != null && meshTriangles != null)
            {
                result.MeshBlocks = RoomMeshVoxelizer.Voxelize(
                    meshVertices, meshTriangles,
                    observation.CellSize, observation.OriginWorldX, observation.OriginWorldZ,
                    observation.Width, observation.Depth,
                    grid, terrainGrid, BlockId.RoomShell);
            }

            for (int z = 0; z < observation.Depth; z++)
            {
                for (int x = 0; x < observation.Width; x++)
                {
                    bool blocked = observation.IsBlocked(x, z);
                    bool hasSurface = observation.GetHitCount(x, z) > 0;

                    // 壁: 通行不可セル（WallFace 平面 ＋ 観測バウンズ外周）を床から天井直下まで。
                    // 厚さは1ブロック＝ラスタライズされた列そのもの
                    if (blocked)
                    {
                        for (int y = floorCellY; y < ceilingCellY; y++)
                        {
                            if (TrySet(grid, terrainGrid, x, y, z))
                            {
                                result.WallBlocks++;
                            }
                        }
                    }

                    // 天井: 部屋全体に1層（壁の上端と重ならないよう ceilingCellY ちょうどに置く）
                    if (TrySet(grid, terrainGrid, x, ceilingCellY, z))
                    {
                        result.CeilingBlocks++;
                    }

                    // 床下: 最下面の下に数層。面を持たない柱（観測の穴）も埋めて床を閉じる
                    int underTop = hasSurface ? observation.GetHit(x, z, 0).cellY : floorCellY;
                    for (int i = 0; i < p.underFloorLayers; i++)
                    {
                        if (TrySet(grid, terrainGrid, x, underTop - i, z))
                        {
                            result.UnderFloorBlocks++;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>天井のセルY。Ceiling 平面が無ければ最上面から一定マージン上に置く。</summary>
        public static int ResolveCeilingCellY(RoomObservation observation, RoomShellParams p, int floorCellY)
        {
            if (observation.HasCeiling)
            {
                return observation.CeilingCellY;
            }

            int top = floorCellY;
            for (int z = 0; z < observation.Depth; z++)
            {
                for (int x = 0; x < observation.Width; x++)
                {
                    int count = observation.GetHitCount(x, z);
                    if (count == 0)
                    {
                        continue;
                    }
                    int y = observation.GetHit(x, z, count - 1).cellY;
                    if (y > top) top = y;
                }
            }
            return top + p.fallbackCeilingMargin;
        }

        /// <summary>
        /// 地形グリッドが既に埋めているセルは飛ばす（Z ファイト防止）。
        /// 外殻グリッド側で既に埋まっているセル（メッシュボクセル化の結果）も二重に数えない。
        /// </summary>
        static bool TrySet(VoxelGrid grid, VoxelGrid terrainGrid, int x, int y, int z)
        {
            var cell = new Int3(x, y, z);
            if (terrainGrid != null && terrainGrid.Get(cell) != BlockId.Air)
            {
                return false;
            }
            if (grid.Get(cell) != BlockId.Air)
            {
                return false;
            }
            grid.SetBlock(cell, BlockId.RoomShell, BlockOrigin.Reality);
            return true;
        }
    }
}
