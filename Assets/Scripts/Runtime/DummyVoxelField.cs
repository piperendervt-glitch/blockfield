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

        InputAction m_AButtonAction;
        bool m_Built;
        int m_CountIndex;
        Mesh m_CubeMesh;
        bool m_SwitchRequested;
        float m_LastSwitchTime = float.NegativeInfinity;
        readonly List<GameObject> m_Chunks = new();
        readonly List<Mesh> m_GeneratedMeshes = new();

        /// <summary>現在表示中のボクセル数（デバッグパネル表示用）。</summary>
        public int CurrentCount { get; private set; }

        void Awake()
        {
            m_AButtonAction = new InputAction("RightHandAButton", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            // WasPressedThisFrame はヒッチ時に多重発火したため performed コールバックで検出する
            m_AButtonAction.performed += OnAButtonPerformed;

            m_CubeMesh = PrimitiveMeshFactory.CreateCube();
        }

        void OnDestroy() => m_AButtonAction.performed -= OnAButtonPerformed;

        void OnEnable() => m_AButtonAction.Enable();
        void OnDisable() => m_AButtonAction.Disable();

        void OnAButtonPerformed(InputAction.CallbackContext _)
        {
            m_SwitchRequested = true;
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

            float half = (k_Side - 1) * 0.5f;
            var scale = Vector3.one * k_BlockSize;
            for (int y = 0; y < layers; y++)
            {
                for (int z = 0; z < k_Side; z++)
                {
                    for (int x = 0; x < k_Side; x++)
                    {
                        var localPos = new Vector3(
                            (x - half) * k_BlockSize,
                            (y + 0.5f) * k_BlockSize,
                            (z - half) * k_BlockSize);
                        int materialIndex = (x + z + y) % materialCount;
                        combinesPerMaterial[materialIndex].Add(new CombineInstance
                        {
                            mesh = m_CubeMesh,
                            transform = Matrix4x4.TRS(localPos, Quaternion.identity, scale),
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
                m_Chunks.Add(go);
            }

            CurrentCount = layers * k_Side * k_Side;
            Debug.Log($"[DummyVoxelField] ボクセル表示: {CurrentCount} 個 ({k_Side}x{k_Side}x{layers}層, メッシュ{materialCount}分割)");
            DebugPanel.Notify($"voxels: {CurrentCount}");
        }

        /// <summary>M3用: 原点から前方(+Z)へ張り出す 1×10×1 の橋（机の縁を越えて空中に伸びる想定）。</summary>
        void BuildBridge()
        {
            var parent = m_Origin.OriginTransform;
            var bridgeRoot = new GameObject("Occlusion Bridge");
            bridgeRoot.transform.SetParent(parent, false);

            var combines = new List<CombineInstance>();
            var scale = Vector3.one * k_BlockSize;
            for (int i = 1; i <= 10; i++)
            {
                var localPos = new Vector3(0f, 0.5f * k_BlockSize, i * k_BlockSize);
                combines.Add(new CombineInstance
                {
                    mesh = m_CubeMesh,
                    transform = Matrix4x4.TRS(localPos, Quaternion.identity, scale),
                });
            }

            var mesh = new Mesh { name = "OcclusionBridge" };
            mesh.CombineMeshes(combines.ToArray(), true, true);
            m_GeneratedMeshes.Add(mesh);

            bridgeRoot.AddComponent<MeshFilter>().sharedMesh = mesh;
            bridgeRoot.AddComponent<MeshRenderer>().sharedMaterial = m_BridgeMaterial;

            Debug.Log("[DummyVoxelField] オクルージョン判定用の橋 (1x10x1) を原点前方に表示した。");
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
