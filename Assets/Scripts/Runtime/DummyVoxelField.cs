using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace BlockField
{
    /// <summary>
    /// ダミーボクセル表示 (Demo 0 T3)。
    /// 原点確定後に 4cm 箱の集合を表示し、右コントローラのAボタンで個数を巡回切替する (M2)。
    /// 原点から前方に張り出す1×10×1の「橋」も表示する (M3 オクルージョン判定用)。
    /// </summary>
    public sealed class DummyVoxelField : MonoBehaviour
    {
        const float k_BlockSize = 0.04f;
        const int k_Side = 100; // 1層 = 100×100 = 1万個
        static readonly int[] k_Counts = { 10_000, 20_000, 40_000, 80_000 };

        [SerializeField] DioramaOrigin m_Origin;
        [SerializeField] Material[] m_VoxelMaterials;
        [SerializeField] Material m_BridgeMaterial;

        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }
        /// <summary>ブロック色バリエーション用の共有マテリアル群（色ごとにメッシュを結合する）。</summary>
        public Material[] voxelMaterials { get => m_VoxelMaterials; set => m_VoxelMaterials = value; }
        public Material bridgeMaterial { get => m_BridgeMaterial; set => m_BridgeMaterial = value; }

        const float k_SwitchCooldown = 1f;
        const int k_BridgeLength = 10;
        const int k_BridgeCellY = 1; // 平原1層目 (y=0) の上に乗せて共面を避ける

        InputAction m_AButtonAction;
        InputAction m_BButtonAction;
        bool m_Built;
        int m_CountIndex;
        Mesh m_CubeMesh;
        bool m_SwitchRequested;
        bool m_ToggleRequested;
        bool m_FieldVisible = true;
        float m_LastSwitchTime = float.NegativeInfinity;
        float m_LastToggleTime = float.NegativeInfinity;
        readonly List<GameObject> m_Chunks = new();
        readonly List<Mesh> m_GeneratedMeshes = new();
        HashSet<Vector3Int> m_BridgeCells;

        /// <summary>セル座標→原点ローカル座標（ブロック中心）。全生成物はこの整数セル系に載せる。</summary>
        static Vector3 CellToLocal(Vector3Int cell)
        {
            return new Vector3(cell.x * k_BlockSize, (cell.y + 0.5f) * k_BlockSize, cell.z * k_BlockSize);
        }

        /// <summary>十字の橋が占有するセル集合。原則「同一セルに2つ生成しない」の予約に使う。</summary>
        static HashSet<Vector3Int> BuildBridgeCells()
        {
            var cells = new HashSet<Vector3Int>();
            var directions = new[] { Vector3Int.forward, Vector3Int.back, Vector3Int.right, Vector3Int.left };
            foreach (var dir in directions)
            {
                for (int i = 1; i <= k_BridgeLength; i++)
                {
                    cells.Add(new Vector3Int(dir.x * i, k_BridgeCellY, dir.z * i));
                }
            }
            return cells;
        }

        /// <summary>現在表示中のボクセル数（デバッグパネル表示用）。</summary>
        public int CurrentCount { get; private set; }

        /// <summary>平原の表示状態（M3モード時は false。デバッグパネル表示用）。</summary>
        public bool FieldVisible => m_FieldVisible;

        void Awake()
        {
            m_AButtonAction = new InputAction("RightHandAButton", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            // WasPressedThisFrame はヒッチ時に多重発火したため performed コールバックで検出する
            m_AButtonAction.performed += OnAButtonPerformed;

            // Bボタン: M3モード（平原の表示/非表示トグル。赤箱と橋のみ残す）
            m_BButtonAction = new InputAction("RightHandBButton", InputActionType.Button,
                "<XRController>{RightHand}/secondaryButton");
            m_BButtonAction.performed += OnBButtonPerformed;

            m_CubeMesh = PrimitiveMeshFactory.CreateCube();
            m_BridgeCells = BuildBridgeCells();
        }

        void OnDestroy()
        {
            m_AButtonAction.performed -= OnAButtonPerformed;
            m_BButtonAction.performed -= OnBButtonPerformed;
        }

        void OnEnable()
        {
            m_AButtonAction.Enable();
            m_BButtonAction.Enable();
        }

        void OnDisable()
        {
            m_AButtonAction.Disable();
            m_BButtonAction.Disable();
        }

        void OnAButtonPerformed(InputAction.CallbackContext _)
        {
            m_SwitchRequested = true;
        }

        void OnBButtonPerformed(InputAction.CallbackContext _)
        {
            m_ToggleRequested = true;
        }

        void Update()
        {
            if (m_Origin == null || m_Origin.OriginTransform == null)
            {
                return;
            }

            if (!m_Built)
            {
                m_Built = true;
                BuildField(k_Counts[m_CountIndex]);
                BuildBridge();
                return;
            }

            bool requested = m_SwitchRequested;
            m_SwitchRequested = false;
            if (requested && Time.unscaledTime - m_LastSwitchTime >= k_SwitchCooldown)
            {
                m_LastSwitchTime = Time.unscaledTime;
                m_CountIndex = (m_CountIndex + 1) % k_Counts.Length;
                BuildField(k_Counts[m_CountIndex]);
            }

            bool toggleRequested = m_ToggleRequested;
            m_ToggleRequested = false;
            if (toggleRequested && Time.unscaledTime - m_LastToggleTime >= k_SwitchCooldown)
            {
                m_LastToggleTime = Time.unscaledTime;
                m_FieldVisible = !m_FieldVisible;
                foreach (var chunk in m_Chunks)
                {
                    chunk.SetActive(m_FieldVisible);
                }
                Debug.Log($"[DummyVoxelField] 平原表示: {(m_FieldVisible ? "ON" : "OFF (M3モード: 赤箱と橋のみ)")}");
                DebugPanel.Notify($"field {(m_FieldVisible ? "ON" : "OFF")}");
            }
        }

        /// <summary>色ごとに1メッシュへ結合して表示する（共有マテリアル、静的な塊）。</summary>
        void BuildField(int count)
        {
            ClearChunks();

            int materialCount = (m_VoxelMaterials != null && m_VoxelMaterials.Length > 0) ? m_VoxelMaterials.Length : 1;
            int layers = Mathf.Max(1, count / (k_Side * k_Side));

            // マテリアル(色)ごとの CombineInstance リスト
            var combinesPerMaterial = new List<CombineInstance>[materialCount];
            for (int i = 0; i < materialCount; i++)
            {
                combinesPerMaterial[i] = new List<CombineInstance>();
            }

            // 整数セル座標で生成し、橋の予約セルはスキップする（同一セルに2つ生成しない原則）
            int half = k_Side / 2;
            int skipped = 0;
            var scale = Vector3.one * k_BlockSize;
            for (int y = 0; y < layers; y++)
            {
                for (int z = 0; z < k_Side; z++)
                {
                    for (int x = 0; x < k_Side; x++)
                    {
                        var cell = new Vector3Int(x - half, y, z - half);
                        if (m_BridgeCells.Contains(cell))
                        {
                            skipped++;
                            continue;
                        }

                        int materialIndex = (x + z + y) % materialCount;
                        combinesPerMaterial[materialIndex].Add(new CombineInstance
                        {
                            mesh = m_CubeMesh,
                            transform = Matrix4x4.TRS(CellToLocal(cell), Quaternion.identity, scale),
                        });
                    }
                }
            }

            var parent = m_Origin.OriginTransform;
            for (int i = 0; i < materialCount; i++)
            {
                var mesh = new Mesh
                {
                    name = $"VoxelChunk_{i}",
                    indexFormat = IndexFormat.UInt32,
                };
                mesh.CombineMeshes(combinesPerMaterial[i].ToArray(), true, true);
                m_GeneratedMeshes.Add(mesh);

                var go = new GameObject($"Voxel Chunk {i}");
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = (m_VoxelMaterials != null && m_VoxelMaterials.Length > 0)
                    ? m_VoxelMaterials[i]
                    : null;
                go.SetActive(m_FieldVisible); // M3モード中の再構築でも非表示を維持
                m_Chunks.Add(go);
            }

            CurrentCount = layers * k_Side * k_Side - skipped;
            Debug.Log($"[DummyVoxelField] ボクセル表示: {CurrentCount} 個 ({k_Side}x{k_Side}x{layers}層, メッシュ{materialCount}分割, 橋との重複セル{skipped}個スキップ)");
            DebugPanel.Notify($"voxels: {CurrentCount}");
        }

        /// <summary>
        /// M3用: 原点から十字4方向へ各10個張り出す橋。
        /// 机の角に原点を置けばどれかの方向が必ず縁を越えるため、向き調整が不要になる。
        /// </summary>
        void BuildBridge()
        {
            var parent = m_Origin.OriginTransform;
            var bridgeRoot = new GameObject("Occlusion Bridge");
            bridgeRoot.transform.SetParent(parent, false);

            // 平原と同じ整数セル系の y=+1 セル（平原1層目の上）に生成。共面にならない
            var combines = new List<CombineInstance>();
            var scale = Vector3.one * k_BlockSize;
            foreach (var cell in m_BridgeCells)
            {
                combines.Add(new CombineInstance
                {
                    mesh = m_CubeMesh,
                    transform = Matrix4x4.TRS(CellToLocal(cell), Quaternion.identity, scale),
                });
            }

            var mesh = new Mesh { name = "OcclusionBridge" };
            mesh.CombineMeshes(combines.ToArray(), true, true);
            m_GeneratedMeshes.Add(mesh);

            bridgeRoot.AddComponent<MeshFilter>().sharedMesh = mesh;
            bridgeRoot.AddComponent<MeshRenderer>().sharedMaterial = m_BridgeMaterial;

            Debug.Log("[DummyVoxelField] オクルージョン判定用の橋 (十字4方向×10個) を原点周囲に表示した。");
            DebugPanel.Notify("bridge built (cross)");
        }

        void ClearChunks()
        {
            foreach (var chunk in m_Chunks)
            {
                Destroy(chunk);
            }
            m_Chunks.Clear();

            // 橋のメッシュ (index 0 に混ざらないよう、フィールド分のみ破棄したいが
            // 簡潔さ優先で: 橋は初回のみ生成し ClearChunks の対象に含めない)
            for (int i = m_GeneratedMeshes.Count - 1; i >= 0; i--)
            {
                if (m_GeneratedMeshes[i].name.StartsWith("VoxelChunk"))
                {
                    Destroy(m_GeneratedMeshes[i]);
                    m_GeneratedMeshes.RemoveAt(i);
                }
            }
        }
    }
}
