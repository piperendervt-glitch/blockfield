using System.Collections.Generic;
using System.Diagnostics;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BlockField
{
    /// <summary>
    /// 部屋の外殻（壁・天井・床下）の表示 (Demo 4.5b V2)。
    ///
    /// 地形と同じ <see cref="TerrainField.TerrainRoot"/> の配下に置くので、
    /// アンカー基準の座標系をそのまま共有する（HMD 着脱でずれない）。
    /// 既定は非表示で、VRモードのときだけ見せる（MRモードで描くと現実の部屋が隠れる）。
    /// </summary>
    public sealed class RoomShellView : MonoBehaviour
    {
        const float k_BlockSize = 0.04f;

        [SerializeField] RoomTerrainBuilder m_Builder;
        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] Material m_Material;

        public RoomTerrainBuilder builder { get => m_Builder; set => m_Builder = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public Material material { get => m_Material; set => m_Material = value; }

        /// <summary>外殻を作り終えたか。</summary>
        public bool IsBuilt => m_Root != null;

        /// <summary>外殻のブロック数（パネル・ログ用）。</summary>
        public int BlockCount { get; private set; }

        /// <summary>天井のセルY（ログ用）。</summary>
        public int CeilingCellY { get; private set; }

        GameObject m_Root;
        bool m_Visible;
        Transform m_TrackedParent;
        readonly List<Mesh> m_Meshes = new();

        void Update()
        {
            var observation = m_Builder != null ? m_Builder.Observation : null;
            var parent = m_TerrainField != null ? m_TerrainField.TerrainRoot : null;
            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;

            if (observation == null || parent == null || world == null)
            {
                return;
            }

            // 地形が作り直された（シード巡回）ら外殻も作り直す。
            // 地形グリッドの埋まり方が変わるため、重なり回避の結果も変わる
            if (m_Root == null || m_TrackedParent != parent)
            {
                Build(observation, parent, world.Grid);
            }
        }

        void Build(RoomObservation observation, Transform parent, VoxelGrid terrainGrid)
        {
            Clear();

            var stopwatch = Stopwatch.StartNew();

            // スキャンした部屋メッシュ全体もボクセル化する（家具・棚の側面・机の脚）。
            // 積もり面（真下レイキャスト）は上向きの面しか拾わないため、これが無いと
            // VRモードで縦の面が丸ごと欠けて部屋の形が分からない
            var scan = m_Builder.Scan;
            var result = RoomShellComposer.Compose(
                observation, terrainGrid, RoomShellParams.Default,
                scan?.Vertices, scan?.Triangles);
            long composeMs = stopwatch.ElapsedMilliseconds;

            m_Root = new GameObject("Room Shell");
            m_Root.transform.SetParent(parent, false);
            m_TrackedParent = parent;

            int chunkCount = 0;
            foreach (var pair in result.Grid.Chunks)
            {
                var mesh = ChunkMesher.BuildChunkMesh(result.Grid, pair.Key, pair.Value, k_BlockSize);
                if (mesh == null)
                {
                    continue;
                }

                m_Meshes.Add(mesh);
                var go = new GameObject($"Shell {pair.Key}");
                go.transform.SetParent(m_Root.transform, false);
                go.transform.localPosition = new Vector3(
                    pair.Key.x * Chunk.Size * k_BlockSize,
                    pair.Key.y * Chunk.Size * k_BlockSize,
                    pair.Key.z * Chunk.Size * k_BlockSize);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = m_Material;
                chunkCount++;
            }

            stopwatch.Stop();
            BlockCount = result.TotalBlocks;
            CeilingCellY = result.CeilingCellY;

            m_Root.SetActive(m_Visible);

            Debug.Log($"[RoomShell] 外殻を生成: 合成{composeMs}ms メッシュ化{stopwatch.ElapsedMilliseconds - composeMs}ms " +
                $"(計{stopwatch.ElapsedMilliseconds}ms) " +
                $"メッシュ由来={result.MeshBlocks} 壁={result.WallBlocks} 天井={result.CeilingBlocks} " +
                $"床下={result.UnderFloorBlocks} 計={result.TotalBlocks} チャンク={chunkCount} " +
                $"三角形={(scan?.Triangles != null ? scan.Triangles.Length / 3 : 0)} " +
                $"床cellY={result.FloorCellY} 天井cellY={result.CeilingCellY} " +
                $"({(result.CeilingCellY - result.FloorCellY) * k_BlockSize:F2}m) " +
                $"表示={(m_Visible ? "ON" : "OFF")}");
        }

        /// <summary>外殻の表示を切り替える。VrModeController から呼ばれる。</summary>
        public void SetVisible(bool visible)
        {
            m_Visible = visible;
            if (m_Root != null)
            {
                m_Root.SetActive(visible);
            }
        }

        void Clear()
        {
            if (m_Root != null)
            {
                Destroy(m_Root);
                m_Root = null;
            }
            foreach (var mesh in m_Meshes)
            {
                Destroy(mesh);
            }
            m_Meshes.Clear();
        }

        void OnDestroy() => Clear();
    }
}
