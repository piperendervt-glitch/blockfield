namespace BlockField.SimCore.Voxel
{
    /// <summary>ブロック種別。byte 1個でチャンクに格納される。</summary>
    public enum BlockId : byte
    {
        Air = 0,
        Grass = 1,
        Dirt = 2,
        Stone = 3,
        Sand = 4,
    }
}
