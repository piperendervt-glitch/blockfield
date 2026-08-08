using System;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 2次元 float グリッドの「場」(Demo 2 D3)。typed array 相当の float[] を平坦に保持する。
    /// 現状は密な固定サイズだが、将来は susuwatari-mirror 方式
    /// （スパースシリアライズ＋スキーマバージョニング）へ拡張予定（roadmap.md 横断原則1）。
    /// </summary>
    public sealed class Field
    {
        public int Width { get; }
        public int Depth { get; }

        readonly float[] m_Values;

        public Field(int width, int depth)
        {
            if (width <= 0 || depth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"サイズが不正: {width}x{depth}");
            }

            Width = width;
            Depth = depth;
            m_Values = new float[width * depth];
        }

        /// <summary>平坦配列の要素数（決定論的な全体列挙・ハッシュ用）。</summary>
        public int Length => m_Values.Length;

        public float Get(int x, int z) => m_Values[ToIndex(x, z)];

        public void Set(int x, int z, float value) => m_Values[ToIndex(x, z)] = value;

        /// <summary>平坦インデックス（x + Width*z）での読み出し。</summary>
        public float GetByIndex(int index) => m_Values[index];

        int ToIndex(int x, int z)
        {
            if ((uint)x >= Width || (uint)z >= Depth)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"場の座標が範囲外: ({x}, {z})");
            }
            return x + Width * z;
        }
    }
}
