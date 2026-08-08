using System;

namespace BlockField.SimCore.Voxel
{
    /// <summary>
    /// 16×16×16 のブロック塊。ローカル座標 (0..15) でアクセスする。
    /// </summary>
    public sealed class Chunk
    {
        /// <summary>1辺のセル数。2のべき乗であること（VoxelGrid がシフト演算で座標変換するため）。</summary>
        public const int Size = 16;

        /// <summary>フラット配列の総セル数。</summary>
        public const int VolumeLength = Size * Size * Size;

        readonly byte[] m_Blocks = new byte[VolumeLength];
        readonly byte[] m_Origins = new byte[VolumeLength]; // BlockOrigin (block と同インデックス)

        /// <summary>ローカル座標→フラット配列インデックス（x + Size*(y + Size*z)）。</summary>
        public static int ToIndex(int x, int y, int z)
        {
            return x + Size * (y + Size * z);
        }

        public BlockId Get(int x, int y, int z)
        {
            ValidateLocal(x, y, z);
            return (BlockId)m_Blocks[ToIndex(x, y, z)];
        }

        public void Set(int x, int y, int z, BlockId id)
        {
            Set(x, y, z, id, BlockOrigin.Terrain);
        }

        /// <summary>
        /// ブロックと出所属性の設定。Air セルの origin は Terrain(0) に正規化し、
        /// ハッシュの安定性を保つ（同じ形状なら破壊経緯によらず同ハッシュ）。
        /// </summary>
        public void Set(int x, int y, int z, BlockId id, BlockOrigin origin)
        {
            ValidateLocal(x, y, z);
            int index = ToIndex(x, y, z);
            m_Blocks[index] = (byte)id;
            m_Origins[index] = id == BlockId.Air ? (byte)BlockOrigin.Terrain : (byte)origin;
        }

        public BlockOrigin GetOrigin(int x, int y, int z)
        {
            ValidateLocal(x, y, z);
            return (BlockOrigin)m_Origins[ToIndex(x, y, z)];
        }

        /// <summary>ハッシュ計算用の生バイトアクセス（インデックスは ToIndex 準拠）。</summary>
        public byte GetRaw(int index) => m_Blocks[index];

        /// <summary>ハッシュ計算用の出所属性生バイトアクセス。</summary>
        public byte GetRawOrigin(int index) => m_Origins[index];

        static void ValidateLocal(int x, int y, int z)
        {
            if ((uint)x >= Size || (uint)y >= Size || (uint)z >= Size)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"ローカル座標が範囲外: ({x}, {y}, {z})");
            }
        }
    }
}
