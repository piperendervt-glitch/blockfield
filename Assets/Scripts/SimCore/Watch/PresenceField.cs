using System;

namespace BlockField.SimCore.Watch
{
    /// <summary>
    /// 滞在の場（**L1**）。**床面の 2D 格子**である。
    ///
    /// 【ラスタライズは L1 の仕事】L0 はカバレッジを**領域**（床の境界ポリゴン）で出す。
    /// 格子へ落とすのはここ。L0 で格子化すると、**セルサイズを変えたときに
    /// 生の記録が使えなくなる**（roadmap v14.1）。
    ///
    /// 【最終検証ティックはここが持つ】L0 ではなく L1 に置く。**L2 の基層**である。
    /// 段1a ではタグの導出はしない（保持先を移すだけ）。
    ///
    /// 【この場が主張すること】**この場は「この人（装着者）の滞在」を主張する。**
    /// 「誰かの滞在」ではない。プロデューサが Quest 3 の頭位置1つだけであり、
    /// 装着者以外は原理的に観測できない。
    /// **カバレッジの定義がこの主張に依存している** — 「台所に居ない」と言えるのは
    /// 装着者についてだけで、他人については何も言っていない。
    ///
    /// 【なぜ 3D ボクセルをやめたか】**滞在は本来床の量である。**
    /// 3D で持つと、自分の高さにあるセルは目の位置に描かれて見えない
    /// （2026-08-19 の実機で、頭位置のセルが視認できなかった）。
    /// 水槽（系列2）が 3D だったのは**水が体積だから**で、部屋の滞在は**面**である。
    ///
    /// 【高さは軸ではなく属性】座っている・倒れているを将来表すために、
    /// 高さは <see cref="HeightAt"/> として**セルの属性**で持つ。格子は 2 次元のまま。
    ///
    /// 【値を推定で埋めない】直前値保持もゼロ埋めもしない。
    /// カバレッジの外は**欠測**であって 0 ではない。
    /// 「測定された 0」と「欠測」を混ぜた時点で、この場は「居ない」を主張できなくなる。
    ///
    /// 【最終検証ティック】毎ティックのカバレッジ集合は保存しない。
    /// **セルごとに整数1個**を持ち、カバレッジ内のティックで更新する。
    /// L2 の3値はこの経過時間の関数になるが、**閾値は L2 で決める**。
    /// </summary>
    public sealed class PresenceField
    {
        /// <summary>一度も検証されていないセルの最終検証ティック。</summary>
        public const int NeverVerified = -1;

        /// <summary>高さの属性が無い（そのセルに居たことがない）ことを表す値。</summary>
        public const float NoHeight = float.NaN;

        public int Width { get; }
        public int Depth { get; }
        public float CellSize { get; }
        public float OriginX { get; }
        public float OriginZ { get; }

        readonly bool[] m_Scanned;
        readonly float[] m_FloorY;
        readonly int[] m_LastVerified;
        readonly float[] m_Value;
        readonly float[] m_Height;

        public int Tick { get; private set; } = -1;
        public L0Coverage Coverage { get; private set; } = L0Coverage.None;
        public L0Label Label { get; private set; } = L0Label.NotWorn;

        /// <summary>走査済みのセル数（＝カバレッジが ScannedRoom のときのカバレッジ内セル数）。</summary>
        public int ScannedCells { get; }

        public int CellCount => m_Value.Length;

        public int MissingCells =>
            Coverage == L0Coverage.ScannedRoom ? CellCount - ScannedCells : CellCount;

        public int CoveredCells => CellCount - MissingCells;

        /// <summary>直近のティックで値 1 が立ったセルの添字。無ければ -1。</summary>
        public int OccupiedIndex { get; private set; } = -1;

        /// <summary>
        /// **L0 の領域から作る**（ラスタライズはここで行う）。
        /// 格子は <see cref="RoomGridSpec"/> で与える（アンカー GUID に紐づいた固定値）。
        /// </summary>
        public PresenceField(in RoomGridSpec grid, L0Region region)
            : this(grid.Width, grid.Depth, grid.CellSize, grid.OriginX, grid.OriginZ,
                Rasterize(grid, region, out var fy), fy)
        {
        }

        static bool[] Rasterize(in RoomGridSpec grid, L0Region region, out float[] floorY)
        {
            PolygonMask.Build(region?.PolygonXZ, grid.Width, grid.Depth, grid.CellSize,
                grid.OriginX, grid.OriginZ, region?.FloorHeight ?? 0f,
                out var scanned, out floorY);
            return scanned;
        }

        /// <param name="scanned">その床セルが走査済みか（床のメッシュがあるか）。長さ w*d。</param>
        /// <param name="floorY">その床セルの床面の高さ (m、部屋座標)。長さ w*d。</param>
        public PresenceField(int width, int depth, float cellSize,
            float originX, float originZ, bool[] scanned, float[] floorY)
        {
            if (width <= 0 || depth <= 0) throw new ArgumentException("格子の大きさが不正");
            if (cellSize <= 0f) throw new ArgumentException("セルサイズが不正");
            if (scanned == null || scanned.Length != width * depth)
                throw new ArgumentException("走査マスクの長さが格子と合わない");
            if (floorY == null || floorY.Length != scanned.Length)
                throw new ArgumentException("床の高さの長さが格子と合わない");

            Width = width; Depth = depth;
            CellSize = cellSize;
            OriginX = originX; OriginZ = originZ;
            m_Scanned = scanned;
            m_FloorY = floorY;

            m_LastVerified = new int[scanned.Length];
            m_Value = new float[scanned.Length];
            m_Height = new float[scanned.Length];
            for (int i = 0; i < scanned.Length; i++)
            {
                m_LastVerified[i] = NeverVerified;
                m_Height[i] = NoHeight;
            }

            int n = 0;
            for (int i = 0; i < scanned.Length; i++) if (scanned[i]) n++;
            ScannedCells = n;
        }

        public int Index(int x, int z) => z * Width + x;

        public bool InRange(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth;

        /// <summary>そのセルが走査済みか。走査外は**常に欠測**である。</summary>
        public bool IsScanned(int index) => m_Scanned[index];

        /// <summary>その床セルの床面の高さ (m、部屋座標)。描画に使う。</summary>
        public float FloorY(int index) => m_FloorY[index];

        public int LastVerified(int index) => m_LastVerified[index];

        /// <summary>測定された値。**カバレッジ外のセルの値は意味を持たない**（欠測）。</summary>
        public float Value(int index) => m_Value[index];

        /// <summary>
        /// **高さの属性**（そのセルに最後に居たときの、床からの頭の高さ m）。
        /// 一度も居たことがなければ <see cref="NoHeight"/>（NaN）。
        /// 座っている・倒れているを将来ここで表す。**軸ではなく属性である。**
        /// </summary>
        public float HeightAt(int index) => m_Height[index];

        /// <summary>床面へ投影してセルを引く。**高さは捨てる**（属性として別に持つ）。</summary>
        public bool TryCellOf(float x, float z, out int index)
        {
            int gx = (int)Math.Floor((x - OriginX) / CellSize);
            int gz = (int)Math.Floor((z - OriginZ) / CellSize);
            if (!InRange(gx, gz)) { index = -1; return false; }
            index = Index(gx, gz);
            return true;
        }

        /// <summary>
        /// レコードを1件取り込む。
        ///
        /// - カバレッジが <see cref="L0Coverage.ScannedRoom"/>: 走査済みセル**全部**の
        ///   最終検証ティックを更新し、**足元のセル**に値 1、他は**測定された 0**
        /// - カバレッジが <see cref="L0Coverage.None"/>: **何も更新しない**（全セル欠測）
        /// - 走査外セル: **どちらの場合も更新しない**
        /// </summary>
        public void Ingest(in L0Sample sample)
        {
            Tick = sample.Tick;
            Coverage = sample.Coverage;
            Label = sample.Label;
            OccupiedIndex = -1;

            if (sample.Coverage == L0Coverage.None) return;

            int foot = -1;
            if (TryCellOf(sample.X, sample.Z, out int idx) && m_Scanned[idx]) foot = idx;

            for (int i = 0; i < m_Value.Length; i++)
            {
                if (!m_Scanned[i]) continue;          // 走査外は常に欠測
                m_LastVerified[i] = sample.Tick;
                m_Value[i] = i == foot ? sample.Value : 0f;   // 他は「測定された 0」
            }

            if (foot >= 0) m_Height[foot] = sample.Y - m_FloorY[foot];
            OccupiedIndex = foot;
        }

        /// <summary>
        /// そのセルが最後に検証されてからの経過ティック。
        /// 一度も検証されていなければ <see cref="int.MaxValue"/>。
        /// **L2 の3値はこの関数だが、閾値は L2 で決める。**
        /// </summary>
        public int StalenessAt(int index)
        {
            int t = m_LastVerified[index];
            return t == NeverVerified ? int.MaxValue : Tick - t;
        }
    }
}
