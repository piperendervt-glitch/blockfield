using System;

namespace BlockField.SimCore.Watch
{
    /// <summary>
    /// 滞在の場（L1 の最小形）。
    ///
    /// 【この場が主張すること】**この場は「この人（装着者）の滞在」を主張する。**
    /// 「誰かの滞在」ではない。プロデューサが Quest 3 の頭位置1つだけであり、
    /// 装着者以外は原理的に観測できないためである。
    /// **カバレッジの定義がこの主張に依存している** — 「台所に居ない」と言えるのは
    /// 装着者についてだけで、他人については何も言っていない。
    ///
    /// 【値を推定で埋めない】直前値保持もゼロ埋めもしない。
    /// カバレッジの外は**欠測**であって 0 ではない。
    /// 「測定された 0」と「欠測」を混ぜた時点で、この場は「居ない」を主張できなくなる。
    ///
    /// 【最終検証ティック】毎ティックのカバレッジ集合は保存しない。
    /// **セルごとに整数1個**を持ち、カバレッジ内のティックで更新する。
    /// L2 の3値はこの経過時間の関数になるが、**閾値は L2 で決める**。
    /// ここは最終検証ティックを持つところまで。
    /// </summary>
    public sealed class PresenceField
    {
        /// <summary>一度も検証されていないセルの最終検証ティック。</summary>
        public const int NeverVerified = -1;

        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public float CellSize { get; }
        public float OriginX { get; }
        public float OriginY { get; }
        public float OriginZ { get; }

        readonly bool[] m_Scanned;
        readonly int[] m_LastVerified;
        readonly float[] m_Value;

        /// <summary>最後に取り込んだティック。</summary>
        public int Tick { get; private set; } = -1;

        /// <summary>直近のティックのカバレッジ。</summary>
        public L0Coverage Coverage { get; private set; } = L0Coverage.None;

        /// <summary>直近のティックのラベル。</summary>
        public L0Label Label { get; private set; } = L0Label.NotWorn;

        /// <summary>走査済みのセル数（カバレッジが ScannedRoom のときのカバレッジ内セル数）。</summary>
        public int ScannedCells { get; }

        public int CellCount => m_Value.Length;

        /// <summary>直近のティックで欠測だったセル数。</summary>
        public int MissingCells =>
            Coverage == L0Coverage.ScannedRoom ? CellCount - ScannedCells : CellCount;

        /// <summary>直近のティックでカバレッジ内だったセル数。</summary>
        public int CoveredCells => CellCount - MissingCells;

        /// <summary>直近のティックで値 1 が立ったセルの添字。無ければ -1。</summary>
        public int OccupiedIndex { get; private set; } = -1;

        /// <param name="scanned">走査済みか（scene mesh のある領域）。長さは w*h*d。</param>
        public PresenceField(int width, int height, int depth, float cellSize,
            float originX, float originY, float originZ, bool[] scanned)
        {
            if (width <= 0 || height <= 0 || depth <= 0)
                throw new ArgumentException("格子の大きさが不正");
            if (cellSize <= 0f) throw new ArgumentException("セルサイズが不正");
            if (scanned == null || scanned.Length != width * height * depth)
                throw new ArgumentException("走査マスクの長さが格子と合わない");

            Width = width; Height = height; Depth = depth;
            CellSize = cellSize;
            OriginX = originX; OriginY = originY; OriginZ = originZ;
            m_Scanned = scanned;

            m_LastVerified = new int[scanned.Length];
            m_Value = new float[scanned.Length];
            for (int i = 0; i < m_LastVerified.Length; i++) m_LastVerified[i] = NeverVerified;

            int n = 0;
            for (int i = 0; i < scanned.Length; i++) if (scanned[i]) n++;
            ScannedCells = n;
        }

        public int Index(int x, int y, int z) => (z * Height + y) * Width + x;

        public bool InRange(int x, int y, int z) =>
            x >= 0 && y >= 0 && z >= 0 && x < Width && y < Height && z < Depth;

        /// <summary>そのセルが走査済みか。走査外は**常に欠測**である。</summary>
        public bool IsScanned(int index) => m_Scanned[index];

        /// <summary>最終検証ティック。<see cref="NeverVerified"/> なら一度も検証されていない。</summary>
        public int LastVerified(int index) => m_LastVerified[index];

        /// <summary>測定された値。**カバレッジ外のセルの値は意味を持たない**（欠測）。</summary>
        public float Value(int index) => m_Value[index];

        public bool TryCellOf(float x, float y, float z, out int index)
        {
            int gx = (int)Math.Floor((x - OriginX) / CellSize);
            int gy = (int)Math.Floor((y - OriginY) / CellSize);
            int gz = (int)Math.Floor((z - OriginZ) / CellSize);
            if (!InRange(gx, gy, gz)) { index = -1; return false; }
            index = Index(gx, gy, gz);
            return true;
        }

        /// <summary>
        /// レコードを1件取り込む。
        ///
        /// - カバレッジが <see cref="L0Coverage.ScannedRoom"/>: 走査済みセル**全部**の
        ///   最終検証ティックを更新し、頭のセルに値 1、他は**測定された 0** を書く
        /// - カバレッジが <see cref="L0Coverage.None"/>: **何も更新しない**。
        ///   全セルが欠測になる（最終検証ティックはそのまま古くなる）
        /// - 走査外セル: **どちらの場合も更新しない**
        /// </summary>
        public void Ingest(in L0Sample sample)
        {
            Tick = sample.Tick;
            Coverage = sample.Coverage;
            Label = sample.Label;
            OccupiedIndex = -1;

            if (sample.Coverage == L0Coverage.None) return;

            int head = -1;
            if (TryCellOf(sample.X, sample.Y, sample.Z, out int idx) && m_Scanned[idx]) head = idx;

            for (int i = 0; i < m_Value.Length; i++)
            {
                if (!m_Scanned[i]) continue;          // 走査外は常に欠測
                m_LastVerified[i] = sample.Tick;
                m_Value[i] = i == head ? sample.Value : 0f;   // 他は「測定された 0」
            }
            OccupiedIndex = head;
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
