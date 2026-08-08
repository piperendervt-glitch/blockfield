using System.Collections.Generic;
using System.Diagnostics;
using BlockField.SimCore.Rng;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace BlockField
{
    /// <summary>
    /// 実機の地形表示 (Demo 1 B2)。原点確定/復元後に TerrainGenerator で地形を生成し、
    /// チャンクごとに ChunkMesher でメッシュ化して原点配下に表示する。
    /// Aボタン: シード巡回 / Bボタン: 地形表示トグル (M3モード互換)。
    /// </summary>
    public sealed class TerrainField : MonoBehaviour
    {
        const float k_BlockSize = 0.04f;
        const float k_SwitchCooldown = 1f;
        const int k_Width = 50;
        const int k_Depth = 50;
        const int k_MaxHeight = 16;
        const uint k_DefaultSeed = 12345u;

        [SerializeField] DioramaOrigin m_Origin;
        [SerializeField] Material m_TerrainMaterial;

        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }
        /// <summary>頂点色対応 BlockField/OcclusionUnlit (_VERTEX_COLOR 有効) の共有マテリアル。</summary>
        public Material terrainMaterial { get => m_TerrainMaterial; set => m_TerrainMaterial = value; }

        /// <summary>現在のシード（デバッグパネル表示用）。</summary>
        public uint CurrentSeed => m_Seeds != null ? m_Seeds[m_SeedIndex] : k_DefaultSeed;

        /// <summary>表示中の非Airブロック数（デバッグパネル表示用）。</summary>
        public int BlockCount { get; private set; }

        /// <summary>直近の生成時間 (地形生成＋全チャンクメッシュ化, ms)。</summary>
        public long GenerationMs { get; private set; }

        /// <summary>地形の表示状態（M3モード時は false）。</summary>
        public bool FieldVisible => m_FieldVisible;

        InputAction m_AButtonAction;
        InputAction m_BButtonAction;
        uint[] m_Seeds;
        int m_SeedIndex;
        bool m_Built;
        bool m_SwitchRequested;
        bool m_ToggleRequested;
        bool m_FieldVisible = true;
        float m_LastSwitchTime = float.NegativeInfinity;
        float m_LastToggleTime = float.NegativeInfinity;
        readonly List<GameObject> m_Chunks = new();
        readonly List<Mesh> m_Meshes = new();

        void Awake()
        {
            m_AButtonAction = new InputAction("RightHandAButton", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            m_AButtonAction.performed += OnAButtonPerformed;

            m_BButtonAction = new InputAction("RightHandBButton", InputActionType.Button,
                "<XRController>{RightHand}/secondaryButton");
            m_BButtonAction.performed += OnBButtonPerformed;

            // シード巡回: 既定 12345 → ランダム3種 → 既定 に戻る
            // (ランダムは起動時に Mulberry32 で決める。CLAUDE.md: System.Random 禁止)
            var rng = new Mulberry32((uint)System.DateTime.Now.Ticks);
            m_Seeds = new[] { k_DefaultSeed, rng.NextUInt(), rng.NextUInt(), rng.NextUInt() };
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

        void OnAButtonPerformed(InputAction.CallbackContext _) => m_SwitchRequested = true;
        void OnBButtonPerformed(InputAction.CallbackContext _) => m_ToggleRequested = true;

        void Update()
        {
            if (m_Origin == null || m_Origin.OriginTransform == null)
            {
                return;
            }

            if (!m_Built)
            {
                m_Built = true;
                BuildTerrain();
                return;
            }

            bool switchRequested = m_SwitchRequested;
            m_SwitchRequested = false;
            if (switchRequested && Time.unscaledTime - m_LastSwitchTime >= k_SwitchCooldown)
            {
                m_LastSwitchTime = Time.unscaledTime;
                m_SeedIndex = (m_SeedIndex + 1) % m_Seeds.Length;
                BuildTerrain();
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
                Debug.Log($"[TerrainField] 地形表示: {(m_FieldVisible ? "ON" : "OFF (M3モード)")}");
                DebugPanel.Notify($"terrain {(m_FieldVisible ? "ON" : "OFF")}");
            }
        }

        void BuildTerrain()
        {
            ClearChunks();

            var stopwatch = Stopwatch.StartNew();

            var p = TerrainParams.Default;
            p.seed = m_Seeds[m_SeedIndex];
            p.width = k_Width;
            p.depth = k_Depth;
            p.maxHeight = k_MaxHeight;

            var grid = TerrainGenerator.Generate(p);

            // 地形を原点中心に置くオフセット（セル単位）
            var parent = m_Origin.OriginTransform;
            float offsetX = k_Width * 0.5f * k_BlockSize;
            float offsetZ = k_Depth * 0.5f * k_BlockSize;

            int blockCount = 0;
            foreach (var pair in grid.Chunks)
            {
                var mesh = ChunkMesher.BuildChunkMesh(grid, pair.Key, pair.Value, k_BlockSize);
                if (mesh == null)
                {
                    continue;
                }
                m_Meshes.Add(mesh);

                var go = new GameObject($"Chunk {pair.Key}");
                go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(
                    pair.Key.x * Chunk.Size * k_BlockSize - offsetX,
                    pair.Key.y * Chunk.Size * k_BlockSize,
                    pair.Key.z * Chunk.Size * k_BlockSize - offsetZ);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = m_TerrainMaterial;
                go.SetActive(m_FieldVisible);
                m_Chunks.Add(go);

                // 非Airブロック数
                for (int i = 0; i < Chunk.VolumeLength; i++)
                {
                    if (pair.Value.GetRaw(i) != 0)
                    {
                        blockCount++;
                    }
                }
            }

            stopwatch.Stop();
            BlockCount = blockCount;
            GenerationMs = stopwatch.ElapsedMilliseconds;

            Debug.Log($"[TerrainField] 地形生成完了: seed={p.seed}, {k_Width}x{k_Depth}x{k_MaxHeight}, " +
                $"ブロック {blockCount} 個, チャンク {m_Chunks.Count} 個, {GenerationMs} ms");
            DebugPanel.Notify($"terrain seed={p.seed} ({GenerationMs}ms)");
        }

        void ClearChunks()
        {
            foreach (var chunk in m_Chunks)
            {
                Destroy(chunk);
            }
            m_Chunks.Clear();

            foreach (var mesh in m_Meshes)
            {
                Destroy(mesh);
            }
            m_Meshes.Clear();
        }
    }
}
