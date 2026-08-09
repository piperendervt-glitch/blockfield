using System.Collections.Generic;
using System.Diagnostics;
using BlockField.SimCore.Ecology;
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
    /// Aボタン: シード巡回 / 左手Xボタン: 地形表示トグル (M3モード互換)。
    ///
    /// 注: 表示トグルは Demo 4 まで右手Bボタンだったが、Demo 4.5 G3 で B を
    /// 部屋地形の表示モード切替 (RoomTerrainView) に譲り、左手Xへ移した。
    /// </summary>
    public sealed class TerrainField : MonoBehaviour
    {
        const float k_BlockSize = 0.04f;
        const float k_SwitchCooldown = 1f;
        const int k_Width = 50;
        const int k_Depth = 50;
        const int k_MaxHeight = 16;
        const uint k_DefaultSeed = 12345u;

        /// <summary>シムティック間隔（秒）。Time.deltaTime 積算で駆動しフレームレート非依存にする。</summary>
        const float k_TickInterval = 1f;

        /// <summary>PopulationLog の定期保存間隔（秒）。adb pull で回収可能にする (E7)。</summary>
        const float k_CsvSaveInterval = 60f;

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

        /// <summary>現在のワールド（シムの主体。D5 でエンティティ表示を載せる）。</summary>
        public World CurrentWorld => m_World;

        InputAction m_AButtonAction;
        InputAction m_ToggleAction;
        World m_World;
        float m_TickAccumulator;
        float m_CsvSaveTimer;
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
        readonly Dictionary<Int3, GameObject> m_ChunkMap = new();
        readonly List<Int3> m_DirtyBuffer = new();

        void Awake()
        {
            m_AButtonAction = new InputAction("RightHandAButton", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            m_AButtonAction.performed += OnAButtonPerformed;

            // 箱庭地形の表示トグルは左手X（右手Bは Demo 4.5 で部屋地形のモード切替に使う）
            m_ToggleAction = new InputAction("LeftHandXButton", InputActionType.Button,
                "<XRController>{LeftHand}/primaryButton");
            m_ToggleAction.performed += OnTogglePerformed;

            // シード巡回: 既定 12345 → ランダム3種 → 既定 に戻る
            // (ランダムは起動時に Mulberry32 で決める。CLAUDE.md: System.Random 禁止)
            var rng = new Mulberry32((uint)System.DateTime.Now.Ticks);
            m_Seeds = new[] { k_DefaultSeed, rng.NextUInt(), rng.NextUInt(), rng.NextUInt() };
        }

        void OnDestroy()
        {
            m_AButtonAction.performed -= OnAButtonPerformed;
            m_ToggleAction.performed -= OnTogglePerformed;
        }

        void OnEnable()
        {
            m_AButtonAction.Enable();
            m_ToggleAction.Enable();
        }

        void OnDisable()
        {
            m_AButtonAction.Disable();
            m_ToggleAction.Disable();
        }

        void OnAButtonPerformed(InputAction.CallbackContext _) => m_SwitchRequested = true;
        void OnTogglePerformed(InputAction.CallbackContext _) => m_ToggleRequested = true;

        /// <summary>
        /// 箱庭地形の表示を設定する。Demo 4.5 では部屋地形が観察対象になるため、
        /// RoomTerrainView が合成完了時にこれを呼んで箱庭を隠す。
        /// </summary>
        public void SetFieldVisible(bool visible)
        {
            m_FieldVisible = visible;
            foreach (var chunk in m_Chunks)
            {
                chunk.SetActive(m_FieldVisible);
            }
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

            // シムティック駆動 (1Hz, フレームレート非依存)。RNG はワールド保持のものを使う
            if (m_World != null)
            {
                m_TickAccumulator += Time.deltaTime;
                while (m_TickAccumulator >= k_TickInterval)
                {
                    m_TickAccumulator -= k_TickInterval;
                    Simulation.Tick(m_World, m_World.Rng);
                }

                // PopulationLog の定期保存 (E7)
                m_CsvSaveTimer += Time.deltaTime;
                if (m_CsvSaveTimer >= k_CsvSaveInterval)
                {
                    m_CsvSaveTimer = 0f;
                    SavePopulationCsv();
                }

                // 設置・破壊による変更チャンクの限定再メッシュ (Demo 4 F3)
                RemeshDirtyChunks();
            }

            bool toggleRequested = m_ToggleRequested;
            m_ToggleRequested = false;
            if (toggleRequested && Time.unscaledTime - m_LastToggleTime >= k_SwitchCooldown)
            {
                m_LastToggleTime = Time.unscaledTime;
                SetFieldVisible(!m_FieldVisible);
                Debug.Log($"[TerrainField] 箱庭地形の表示: {(m_FieldVisible ? "ON" : "OFF (M3モード)")}");
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

            m_World = World.Create(p);
            m_TickAccumulator = 0f;
            var grid = m_World.Grid;

            // 地形を原点中心に置くオフセット（セル単位）
            var parent = m_Origin.OriginTransform;
            float offsetX = k_Width * 0.5f * k_BlockSize;
            float offsetZ = k_Depth * 0.5f * k_BlockSize;

            int blockCount = 0;
            foreach (var pair in grid.Chunks)
            {
                var mesh = ChunkMesher.BuildChunkMesh(grid, pair.Key, pair.Value, k_BlockSize);
                if (mesh != null)
                {
                    CreateChunkObject(pair.Key, mesh, parent);
                }

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

        void CreateChunkObject(Int3 chunkCoord, Mesh mesh, Transform parent)
        {
            float offsetX = k_Width * 0.5f * k_BlockSize;
            float offsetZ = k_Depth * 0.5f * k_BlockSize;

            m_Meshes.Add(mesh);
            var go = new GameObject($"Chunk {chunkCoord}");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(
                chunkCoord.x * Chunk.Size * k_BlockSize - offsetX,
                chunkCoord.y * Chunk.Size * k_BlockSize,
                chunkCoord.z * Chunk.Size * k_BlockSize - offsetZ);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m_TerrainMaterial;
            go.SetActive(m_FieldVisible);
            m_Chunks.Add(go);
            m_ChunkMap[chunkCoord] = go;
        }

        /// <summary>変更のあったチャンクのみ再メッシュする (Demo 4 F3 / M5計測)。</summary>
        void RemeshDirtyChunks()
        {
            if (m_Origin.OriginTransform == null || !m_World.ConsumeDirtyChunks(m_DirtyBuffer))
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var parent = m_Origin.OriginTransform;
            int remeshed = 0;

            foreach (var chunkCoord in m_DirtyBuffer)
            {
                bool hasChunk = m_World.Grid.TryGetChunk(chunkCoord, out var chunk);
                Mesh newMesh = hasChunk
                    ? ChunkMesher.BuildChunkMesh(m_World.Grid, chunkCoord, chunk, k_BlockSize)
                    : null;

                if (m_ChunkMap.TryGetValue(chunkCoord, out var go))
                {
                    var filter = go.GetComponent<MeshFilter>();
                    var oldMesh = filter.sharedMesh;
                    if (newMesh == null)
                    {
                        m_Chunks.Remove(go);
                        m_ChunkMap.Remove(chunkCoord);
                        Destroy(go);
                    }
                    else
                    {
                        filter.sharedMesh = newMesh;
                        m_Meshes.Add(newMesh);
                    }
                    m_Meshes.Remove(oldMesh);
                    Destroy(oldMesh);
                    remeshed++;
                }
                else if (newMesh != null)
                {
                    CreateChunkObject(chunkCoord, newMesh, parent);
                    remeshed++;
                }
            }

            stopwatch.Stop();
            if (remeshed > 0)
            {
                Debug.Log($"[TerrainField] 再メッシュ: {remeshed}チャンク {stopwatch.ElapsedMilliseconds}ms");
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                SavePopulationCsv();
            }
        }

        /// <summary>persistentDataPath/population.csv へ保存（adb pull で回収可能）。</summary>
        void SavePopulationCsv()
        {
            if (m_World == null)
            {
                return;
            }

            try
            {
                string path = System.IO.Path.Combine(Application.persistentDataPath, "population.csv");
                System.IO.File.WriteAllText(path, m_World.PopulationLog.ToCsv());
                Debug.Log($"[TerrainField] population.csv 保存 ({m_World.PopulationLog.Count}行)");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[TerrainField] population.csv 保存失敗: {e.Message}");
            }
        }

        void ClearChunks()
        {
            foreach (var chunk in m_Chunks)
            {
                Destroy(chunk);
            }
            m_Chunks.Clear();
            m_ChunkMap.Clear();

            foreach (var mesh in m_Meshes)
            {
                Destroy(mesh);
            }
            m_Meshes.Clear();
        }
    }
}
