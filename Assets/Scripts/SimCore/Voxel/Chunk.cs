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
            ValidateLocal(x, y, z);
            m_Blocks[ToIndex(x, y, z)] = (byte)id;
        }

        /// <summary>ハッシュ計算用の生バイトアクセス（インデックスは ToIndex 準拠）。</summary>
        public byte GetRaw(int index) => m_Blocks[index];

        static void ValidateLocal(int x, int y, int z)
        {
            if ((uint)x >= Size || (uint)y >= Size || (uint)z >= Size)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"ローカル座標が範囲外: ({x}, {y}, {z})");
            }
        }
    }
}
