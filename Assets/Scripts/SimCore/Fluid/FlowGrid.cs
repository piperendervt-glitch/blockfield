using System;

namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// 流体格子の形と境界 (系列2 Phase B)。
    ///
    /// 【立方体にしない】部屋は立方体ではない（実測 3.19 × 2.07 × 2.60 m）。
    /// N³ を被せると高さ方向のセルが大量に余る。**セルサイズを一様に固定し、
    /// 軸ごとのセル数を観測から決める**。
    ///
    /// 【原点は空間アンカー基準】Unity 側で `DioramaOrigin` のアンカーに貼る。
    /// 本クラスはアンカーローカルの座標系だけを持ち、ワールド変換は View の仕事。
    ///
    /// 【境界は量子化して持つ】固体マスクは1セル1ビット相当、境界までの距離は
    /// **byte（単位 1/32 セル、上限 7.97 セル）**。ψ のランプ（d₀ = 2〜3 セル）に
    /// 使うだけなので、この粒度で十分に足りる。
    /// 量子化した整数で持つ理由は <see cref="Distance"/> のコメントを参照。
    /// </summary>
    public sealed class FlowGrid
    {
        /// <summary>距離場の量子化単位。1 セル = この値。</summary>
        public const int DistanceUnitsPerCell = 32;

        /// <summary>距離場が表せる上限（byte の 255 = 7.97 セル）。これ以上は飽和させる。</summary>
        public const byte MaxQuantizedDistance = 255;

        readonly byte[] m_Solid;
        readonly byte[] m_Distance;

        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }

        /// <summary>1セルの一辺 (m)。全軸で同じ。</summary>
        public float CellSize { get; }

        /// <summary>格子の最小端（アンカーローカル、m）。</summary>
        public float OriginX { get; }
        public float OriginY { get; }
        public float OriginZ { get; }

        public int CellCount => Width * Height * Depth;

        public FlowGrid(int width, int height, int depth, float cellSize,
                        float originX, float originY, float originZ)
        {
            if (width <= 0 || height <= 0 || depth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "セル数は各軸1以上");
            }
            if (cellSize <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(cellSize), "セルサイズは正");
            }

            Width = width; Height = height; Depth = depth;
            CellSize = cellSize;
            OriginX = originX; OriginY = originY; OriginZ = originZ;

            m_Solid = new byte[CellCount];
            m_Distance = new byte[CellCount];
        }

        /// <summary>
        /// 部屋のバウンズ（アンカーローカル、m）とセルサイズから格子を作る。
        /// セル数は切り上げ。原点は最小端をセル境界へ丸める。
        /// </summary>
        public static FlowGrid FromBounds(
            float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ, float cellSize)
        {
            int w = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cellSize));
            int h = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cellSize));
            int d = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / cellSize));
            return new FlowGrid(w, h, d, cellSize, minX, minY, minZ);
        }

        public int Index(int x, int y, int z) => (z * Height + y) * Width + x;

        public bool InRange(int x, int y, int z) =>
            (uint)x < (uint)Width && (uint)y < (uint)Height && (uint)z < (uint)Depth;

        /// <summary>そのセルが固体（家具・壁）か。</summary>
        public bool IsSolid(int x, int y, int z) => m_Solid[Index(x, y, z)] != 0;

        public bool IsSolidAt(int index) => m_Solid[index] != 0;

        public void SetSolid(int x, int y, int z, bool solid) =>
            m_Solid[Index(x, y, z)] = solid ? (byte)1 : (byte)0;

        /// <summary>
        /// 境界までの距離（量子化、単位 1/32 セル）。
        ///
        /// 【なぜ整数で持つか】この値は ψ のランプに掛かる＝**力学の入力**である。
        /// 部屋の形は現実由来なので観測イベントとして記録され、リプレイで再生される
        /// （横断原則2）。そのとき浮動小数点の幾何演算が経路に入っていると、
        /// 環境差でビットが揺れて決定論が壊れる。整数で持って整数で記録すれば
        /// その経路が構造的に存在しなくなる
        /// （`SurfaceHit.cellY` を整数にしたのと同じ理屈）。
        /// </summary>
        public byte Distance(int x, int y, int z) => m_Distance[Index(x, y, z)];

        public byte DistanceAt(int index) => m_Distance[index];

        public void SetDistance(int index, byte value) => m_Distance[index] = value;

        /// <summary>境界までの距離をセル単位の実数で読む（ψ のランプ用）。</summary>
        public float DistanceInCells(int index) => m_Distance[index] / (float)DistanceUnitsPerCell;

        /// <summary>固体マスクを全消去する（再焼き込みの前段）。</summary>
        public void ClearSolid() => Array.Clear(m_Solid, 0, m_Solid.Length);

        /// <summary>
        /// 決定論の検証用ハッシュ (FNV-1a 64bit)。固体マスクと距離場を畳み込む。
        /// 格子の寸法も含める（同じ内容でも形が違えば別の世界）。
        /// </summary>
        public ulong ComputeContentHash()
        {
            const ulong prime = 1099511628211UL;
            ulong hash = 14695981039346656037UL;
            unchecked
            {
                hash = (hash ^ (uint)Width) * prime;
                hash = (hash ^ (uint)Height) * prime;
                hash = (hash ^ (uint)Depth) * prime;
                for (int i = 0; i < m_Solid.Length; i++)
                {
                    hash = (hash ^ m_Solid[i]) * prime;
                    hash = (hash ^ m_Distance[i]) * prime;
                }
            }
            return hash;
        }
    }
}
