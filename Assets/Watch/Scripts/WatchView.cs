using System.Collections.Generic;
using BlockField.SimCore.Watch;
using UnityEngine;

namespace BlockField.Watch
{
    /// <summary>
    /// L0 を実機で見る表示。**描画はすべて <see cref="WatchSpaceRenderer"/> を通す。**
    ///
    /// 【2段だけにした】以前は3段だったが、追跡中は「走査境界」と「カバレッジ全体」が
    /// **同じ集合**なので区別できなかった。加えて描画バッファの上限 1023 で
    /// **両段とも先頭 1023 セルだけ**を描いており、見た目まで同一だった（2026-08-19）。
    ///
    /// - 段1 `足元`: 足元の印だけ。追従を見る
    /// - 段2 `カバレッジ`: 床面全体をカバレッジと欠測で塗り分ける。境界を見る
    ///
    /// 【上限で黙って切り捨てない】**全体を見せていない表示は、見えているものから
    /// 全体を推論できないので危険である。** 床セル全部を描ける大きさの配列を確保し、
    /// それでも足りないときは <see cref="Truncated"/> を立ててパネルとログに出す。
    ///
    /// 【半透明を使わない】MR ではアルファ&lt;1 がパススルーと合成される。
    /// 区別は**明度とスケール**で付ける（CLAUDE.md）。
    /// </summary>
    public sealed class WatchView : MonoBehaviour
    {
        public enum Mode { 足元 = 0, カバレッジ = 1 }

        public static readonly string[] ModeNames = { "足元", "カバレッジ" };

        /// <summary>1回の DrawMeshInstanced に渡せる上限（Unity の仕様）。</summary>
        public const int InstanceBatch = 1023;

        [SerializeField] WatchField m_Field;
        [SerializeField] WatchSpaceRenderer m_Space;
        [SerializeField] Material m_HeadMaterial;
        [SerializeField] Material m_CoveredMaterial;
        [SerializeField] Material m_MissingMaterial;
        [SerializeField] Mode m_Mode = Mode.カバレッジ;

        public WatchField field { get => m_Field; set => m_Field = value; }
        public WatchSpaceRenderer space { get => m_Space; set => m_Space = value; }
        public Material headMaterial { get => m_HeadMaterial; set => m_HeadMaterial = value; }
        public Material coveredMaterial { get => m_CoveredMaterial; set => m_CoveredMaterial = value; }
        public Material missingMaterial { get => m_MissingMaterial; set => m_MissingMaterial = value; }

        public Mode Current => m_Mode;
        public string CurrentName => ModeNames[(int)m_Mode];

        /// <summary>直近のフレームで描いたセル数。</summary>
        public int DrawnCells { get; private set; }

        /// <summary>描くつもりだったセル数。<see cref="DrawnCells"/> と食い違えば切り捨てである。</summary>
        public int WantedCells { get; private set; }

        /// <summary>**切り捨てが起きたか。** 黙って捨てないための旗。</summary>
        public bool Truncated => DrawnCells < WantedCells;

        public void CycleMode() => m_Mode = (Mode)(((int)m_Mode + 1) % ModeNames.Length);

        Mesh m_Quad;
        Vector3[] m_Covered = System.Array.Empty<Vector3>();
        Vector3[] m_Missing = System.Array.Empty<Vector3>();
        Matrix4x4[] m_Scratch = new Matrix4x4[InstanceBatch];
        int m_CoveredCount, m_MissingCount;
        int m_BuiltForTick = -2;
        L0Coverage m_BuiltForCoverage = L0Coverage.None;

        void Update()
        {
            DrawnCells = 0;
            WantedCells = 0;

            var f = m_Field != null ? m_Field.Field : null;
            if (f == null || m_Space == null || !m_Space.IsReady) return;
            if (m_Quad == null) m_Quad = PrimitiveMeshFactory.CreateCube();

            float cell = f.CellSize;

            // 足元の印。カバレッジが空なら **描かない**（居場所は分からない）
            if (f.OccupiedIndex >= 0 && m_HeadMaterial != null)
            {
                WantedCells++;
                if (m_Space.DrawCube(m_Quad, m_HeadMaterial,
                    m_Field.CellCenter(f.OccupiedIndex), cell * 0.8f)) DrawnCells++;
            }

            if (m_Mode == Mode.足元) return;

            // 【差分更新】床面の集合はカバレッジの状態が変わったときだけ組み直す。
            // 毎ティック組み直すとちらつく
            if (m_BuiltForTick == -2 || m_BuiltForCoverage != f.Coverage) Rebuild(f);

            WantedCells += m_CoveredCount + m_MissingCount;

            // 床は薄い板にする。立方体のままだと床から生えて見える
            float thickness = cell * 0.12f;
            if (m_CoveredMaterial != null)
                DrawnCells += DrawFlat(m_Covered, m_CoveredCount, cell * 0.85f, thickness, m_CoveredMaterial);
            if (m_MissingMaterial != null)
                DrawnCells += DrawFlat(m_Missing, m_MissingCount, cell * 0.5f, thickness, m_MissingMaterial);
        }

        /// <summary>床の板1枚ぶんの大きさ。立方体のままだと床から生えて見える。</summary>
        int DrawFlat(Vector3[] positions, int count, float size, float thickness, Material mat) =>
            m_Space.DrawBatched(m_Quad, mat, positions, count,
                new Vector3(size, thickness, size), m_Scratch);

        /// <summary>
        /// 床面をカバレッジ側と欠測側に分ける。**全セルを入れる**（上限で切らない）。
        /// トラッキングを失うとカバレッジ側が空になり、床全体が欠測側の見た目に落ちる。
        /// </summary>
        void Rebuild(PresenceField f)
        {
            m_BuiltForTick = f.Tick;
            m_BuiltForCoverage = f.Coverage;

            int cells = f.CellCount;
            if (m_Covered.Length < cells) m_Covered = new Vector3[cells];
            if (m_Missing.Length < cells) m_Missing = new Vector3[cells];
            m_CoveredCount = 0;
            m_MissingCount = 0;

            bool covered = f.Coverage != L0Coverage.None;
            for (int i = 0; i < cells; i++)
            {
                if (!f.IsScanned(i)) continue;      // 走査外は描かない（床が無い）
                var p = m_Field.CellCenter(i);
                if (covered) m_Covered[m_CoveredCount++] = p;
                else m_Missing[m_MissingCount++] = p;
            }
        }
    }
}
