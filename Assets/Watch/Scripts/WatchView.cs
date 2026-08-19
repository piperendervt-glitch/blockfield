using System.Collections.Generic;
using BlockField.SimCore.Watch;
using UnityEngine;

namespace BlockField.Watch
{
    /// <summary>
    /// L0 を実機で見る表示。**描画はすべて <see cref="WatchSpaceRenderer"/> を通す。**
    ///
    /// 見せるものは3つだけ:
    /// - 自分の頭位置が部屋座標のどこにあるか（1セル、明るい）
    /// - **走査済み領域と走査外領域の境界**（走査済みの外周セルを描く）
    /// - トラッキングを失った瞬間に**部屋全体が欠測側へ落ちる**こと（全部が暗くなる）
    ///
    /// 【半透明を使わない】MR ではアルファ&lt;1 がパススルーと合成される。
    /// 区別は**明度とスケール**で付ける（CLAUDE.md）。
    /// </summary>
    public sealed class WatchView : MonoBehaviour
    {
        public enum Mode { 頭位置のみ = 0, 走査境界 = 1, カバレッジ全体 = 2 }

        public static readonly string[] ModeNames = { "頭位置のみ", "走査境界", "カバレッジ全体" };

        [SerializeField] WatchField m_Field;
        [SerializeField] WatchSpaceRenderer m_Space;
        [SerializeField] Material m_HeadMaterial;
        [SerializeField] Material m_CoveredMaterial;
        [SerializeField] Material m_MissingMaterial;
        [SerializeField] Mode m_Mode = Mode.走査境界;

        public WatchField field { get => m_Field; set => m_Field = value; }
        public WatchSpaceRenderer space { get => m_Space; set => m_Space = value; }
        public Material headMaterial { get => m_HeadMaterial; set => m_HeadMaterial = value; }
        public Material coveredMaterial { get => m_CoveredMaterial; set => m_CoveredMaterial = value; }
        public Material missingMaterial { get => m_MissingMaterial; set => m_MissingMaterial = value; }

        public Mode Current => m_Mode;
        public string CurrentName => ModeNames[(int)m_Mode];
        public int DrawnCells { get; private set; }

        public void CycleMode() => m_Mode = (Mode)(((int)m_Mode + 1) % ModeNames.Length);

        Mesh m_Cube;
        readonly List<Vector3> m_Boundary = new List<Vector3>();
        Vector3[] m_Buffer = new Vector3[1023];
        Matrix4x4[] m_Scratch = new Matrix4x4[1023];
        int m_BoundaryTick = -1;

        void Update()
        {
            DrawnCells = 0;
            var f = m_Field != null ? m_Field.Field : null;
            if (f == null || m_Space == null || !m_Space.IsReady) return;
            if (m_Cube == null) m_Cube = PrimitiveMeshFactory.CreateCube();

            float cell = f.CellSize;

            // 頭のセル。カバレッジが空なら **描かない**（居場所は分からない）
            if (f.OccupiedIndex >= 0 && m_HeadMaterial != null)
            {
                if (m_Space.DrawCube(m_Cube, m_HeadMaterial,
                    m_Field.CellCenter(f.OccupiedIndex), cell * 0.9f)) DrawnCells++;
            }

            if (m_Mode == Mode.頭位置のみ) return;

            // 走査済み領域の境界。**走査外との境目が目で見えること**が要件
            if (m_BoundaryTick != f.Tick / 200) RebuildBoundary(f);

            // 欠測側は暗いマテリアル。トラッキングを失うと全部こちらへ落ちる
            var mat = f.Coverage == L0Coverage.None ? m_MissingMaterial : m_CoveredMaterial;
            if (mat == null) return;

            int count = Mathf.Min(m_Boundary.Count, m_Buffer.Length);
            for (int i = 0; i < count; i++) m_Buffer[i] = m_Boundary[i];
            DrawnCells += m_Space.DrawCubes(m_Cube, mat, m_Buffer, count, cell * 0.35f, m_Scratch);
        }

        /// <summary>
        /// 走査済みセルのうち、走査外と隣り合うものを集める。
        /// 全セル描くと視界が埋まるので、**境界だけ**にする。
        /// </summary>
        void RebuildBoundary(PresenceField f)
        {
            m_BoundaryTick = f.Tick / 200;
            m_Boundary.Clear();

            for (int z = 0; z < f.Depth; z++)
                for (int y = 0; y < f.Height; y++)
                    for (int x = 0; x < f.Width; x++)
                    {
                        int i = f.Index(x, y, z);
                        if (!f.IsScanned(i)) continue;
                        if (m_Mode == Mode.カバレッジ全体) { m_Boundary.Add(m_Field.CellCenter(i)); continue; }

                        if (IsScannedAt(f, x - 1, y, z) && IsScannedAt(f, x + 1, y, z)
                            && IsScannedAt(f, x, y - 1, z) && IsScannedAt(f, x, y + 1, z)
                            && IsScannedAt(f, x, y, z - 1) && IsScannedAt(f, x, y, z + 1))
                            continue;

                        m_Boundary.Add(m_Field.CellCenter(i));
                    }
        }

        /// <summary>格子の外は「走査されていない」とみなす（境界として立つ）。</summary>
        static bool IsScannedAt(PresenceField f, int x, int y, int z) =>
            f.InRange(x, y, z) && f.IsScanned(f.Index(x, y, z));
    }
}
