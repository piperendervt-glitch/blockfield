namespace BlockField.SimCore.Voxel
{
    /// <summary>
    /// ブロックの出所属性 (Demo 4 F1)。固定レイヤー原則 (vision §5) を構造で強制する:
    /// Player 出所のブロックは生態系から変更できない（VoxelGrid.TrySetBlockEcology 参照）。
    /// Reality は Demo 4.5 (Room Terrain) の Boundary 用に予約。
    /// </summary>
    public enum BlockOrigin : byte
    {
        Terrain = 0,
        Player = 1,
        Ecology = 2,
        Reality = 3,
    }
}
