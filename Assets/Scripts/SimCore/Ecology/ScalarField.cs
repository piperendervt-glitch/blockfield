using System;
using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// スカラー場の標準実装（旧 Field）。typed array 相当の float[] を平坦に保持する。
    /// 意味論・将来の拡張方針は <see cref="IField"/> のコメントを参照（表面場）。
    ///
    /// 現状は密な固定サイズだが、将来は susuwatari-mirror 方式
    /// （スパースシリアライズ＋スキーマバージョニング）へ拡張予定（roadmap 横断原則1）。
    /// 3D 化する場合はスパースチャンク方式を前提とする（IField のコメント参照）。
    /// </summary>
    public class ScalarField : IField
    {
        public string Name { get; }
        public int Width { get; }
        public int Depth { get; }

        readonly float[] m_Values;

        /// <summary>
        /// 表面場の前提を検証するための表層高さ提供関数（World が設定）。
        /// デバッグビルドでのみ使用され、リリースビルドでは参照されない。
        /// </summary>
        public Func<int, int, int> SurfaceHeightProvider { get; set; }

        public ScalarField(string name, int width, int depth)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("場の名前が空", nameof(name));
            }
            if (width <= 0 || depth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"サイズが不正: {width}x{depth}");
            }

            Name = name;
            Width = width;
            Depth = depth;
            m_Values = new float[width * depth];
        }

        /// <summary>平坦配列の要素数（決定論的な全体列挙・ハッシュ用）。</summary>
        public int Length => m_Values.Length;

        // --- 3D対応API（座標は Int3。現行実装は y を無視し、デバッグ時のみ検証する）---

        public float Get(Int3 cell)
        {
            AssertSurface(cell);
            return GetAtColumn(cell.x, cell.z);
        }

        public virtual void Deposit(Int3 cell, float amount)
        {
            AssertSurface(cell);
            SetAtColumn(cell.x, cell.z, GetAtColumn(cell.x, cell.z) + amount);
        }

        public void Set(Int3 cell, float value)
        {
            AssertSurface(cell);
            SetAtColumn(cell.x, cell.z, value);
        }

        // --- 表面場のエスケープハッチ ---
        // 柱 (x,z) 単位の操作。表面場では「その柱の場」と「最上面の場」が同一なので等価。
        // フロア構造 (b) / 3D (c) へ移行する際は、これらの呼び出し元が
        // 「どのフロアの場か」を明示する必要がある（移行時の要レビュー箇所）。

        public float GetAtColumn(int x, int z) => m_Values[ToIndex(x, z)];

        public void SetAtColumn(int x, int z, float value) => m_Values[ToIndex(x, z)] = value;

        /// <summary>平坦インデックス（x + Width*z）での読み出し。</summary>
        public float GetByIndex(int index) => m_Values[index];

        /// <summary>平坦インデックスでの書き込み（拡散などの一括処理用）。</summary>
        public void SetByIndex(int index, float value) => m_Values[index] = value;

        /// <summary>静的な場は毎ティックの更新を行わない。動的な場は override する。</summary>
        public virtual void Update(SimParams p)
        {
        }

        public ulong AccumulateHash(ulong hash, ulong prime)
        {
            for (int i = 0; i < m_Values.Length; i++)
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(m_Values[i]);
                unchecked
                {
                    hash = (hash ^ (bits & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 8) & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 16) & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 24) & 0xFF)) * prime;
                }
            }
            return hash;
        }

        int ToIndex(int x, int z)
        {
            if ((uint)x >= Width || (uint)z >= Depth)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"場の座標が範囲外: ({x}, {z})");
            }
            return x + Width * z;
        }

        /// <summary>
        /// 表面場の前提検証: 渡された y が表層高さと一致するか。
        /// デバッグビルドでのみ有効。表面場の前提が破れた（＝フロア構造が必要になった）
        /// ことを早期に検出するための仕掛けであり、リリース性能には影響しない。
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        [System.Diagnostics.Conditional("DEBUG")]
        void AssertSurface(Int3 cell)
        {
            var provider = SurfaceHeightProvider;
            if (provider == null)
            {
                return;
            }
            int surfaceY = provider(cell.x, cell.z);
            if (cell.y != surfaceY)
            {
                throw new InvalidOperationException(
                    $"表面場の前提違反: 場 '{Name}' のセル {cell} は表層高さ {surfaceY} と一致しない。" +
                    "フロア構造 (roadmap Demo 6 拡張点 (b)) が必要になった可能性がある。");
            }
        }
    }
}
