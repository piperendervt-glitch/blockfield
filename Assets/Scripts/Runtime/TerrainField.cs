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
    /// 地形とシムの実機接続 (Demo 1 B2 / Demo 4.5 G7)。
    ///
    /// 【2つのモード】
    /// - 箱庭モード (Demo 1-4): TerrainGenerator で 50x50 の地形を生成し、原点中心に置く
    /// - 部屋モード (Demo 4.5): <see cref="roomBuilder"/> がある場合。観測から部屋地形を
    ///   合成し、World をその上に作る（<see cref="World.CreateFromRoom"/>）。
    ///   箱庭グリッドは**生成しない**
    ///
    /// どちらのモードでもチャンクは <see cref="TerrainRoot"/> の配下に置き、
    /// EntityRenderer も同じ親と同じ <see cref="CellToLocal"/> を使う。
    /// 部屋モードのルートは観測時のアンカーポーズ基準に固定される（着脱で位置がずれない）。
    ///
    /// Aボタン: シード巡回（部屋モードでは積もり方のシード）。
    /// 表示モードの切替（右手B）は RoomTerrainView の責務。
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

        /// <summary>
        /// 部屋モードの入力 (Demo 4.5 G7)。設定されている場合、箱庭グリッドは生成せず
        /// 観測の到着を待って部屋地形の上に World を作る。
        /// </summary>
        [SerializeField] RoomTerrainBuilder m_RoomBuilder;

        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }
        public RoomTerrainBuilder roomBuilder { get => m_RoomBuilder; set => m_RoomBuilder = value; }
        /// <summary>頂点色対応 BlockField/OcclusionUnlit (_VERTEX_COLOR 有効) の共有マテリアル。</summary>
        public Material terrainMaterial { get => m_TerrainMaterial; set => m_TerrainMaterial = value; }

        /// <summary>現在のシード（デバッグパネル表示用）。</summary>
        public uint CurrentSeed => m_Seeds != null ? m_Seeds[m_SeedIndex] : k_DefaultSeed;

        /// <summary>表示中の非Airブロック数（デバッグパネル表示用）。</summary>
        public int BlockCount { get; private set; }

        /// <summary>直近の生成時間 (地形生成＋全チャンクメッシュ化, ms)。</summary>
        public long GenerationMs { get; private set; }

        /// <summary>地形の表示状態（診断モード時は false）。</summary>
        public bool FieldVisible => m_FieldVisible;

        /// <summary>現在のワールド（シムの主体）。</summary>
        public World CurrentWorld => m_World;

        /// <summary>部屋モードか (Demo 4.5 G7)。</summary>
        public bool IsRoomMode => m_RoomBuilder != null;

        /// <summary>部屋地形の合成結果（部屋モードのみ。未合成なら null）。</summary>
        public SnowfallResult RoomComposed { get; private set; }

        /// <summary>チャンクとエンティティの共通の親。未生成なら null。</summary>
        public Transform TerrainRoot => m_TerrainRoot != null ? m_TerrainRoot.transform : null;

        /// <summary>セル→ローカルの中心オフセット (m)。箱庭は原点中心、部屋は 0。</summary>
        public float OffsetX => m_OffsetX;
        public float OffsetZ => m_OffsetZ;

        InputAction m_AButtonAction;
        World m_World;
        GameObject m_TerrainRoot;
        float m_TickAccumulator;
        float m_CsvSaveTimer;
        uint[] m_Seeds;
        int m_SeedIndex;
        bool m_Built;
        bool m_SwitchRequested;
        bool m_FieldVisible = true;
        float m_LastSwitchTime = float.NegativeInfinity;
        float m_OffsetX;
        float m_OffsetZ;
        readonly List<GameObject> m_Chunks = new();
        readonly List<Mesh> m_Meshes = new();
        readonly Dictionary<Int3, GameObject> m_ChunkMap = new();
        readonly List<Int3> m_DirtyBuffer = new();

        void Awake()
        {
            m_AButtonAction = new InputAction("RightHandAButton", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            m_AButtonAction.performed += OnAButtonPerformed;

            // シード巡回: 既定 12345 → ランダム3種 → 既定 に戻る
            // (ランダムは起動時に Mulberry32 で決める。CLAUDE.md: System.Random 禁止)
            var rng = new Mulberry32((uint)System.DateTime.Now.Ticks);
            m_Seeds = new[] { k_DefaultSeed, rng.NextUInt(), rng.NextUInt(), rng.NextUInt() };
        }

        void OnDestroy()
        {
            m_AButtonAction.performed -= OnAButtonPerformed;
        }

        void OnEnable() => m_AButtonAction.Enable();
        void OnDisable() => m_AButtonAction.Disable();

        void OnAButtonPerformed(InputAction.CallbackContext _) => m_SwitchRequested = true;

        /// <summary>地形（とエンティティ）の表示を設定する。診断モード切替から呼ばれる。</summary>
        public void SetFieldVisible(bool visible)
        {
            m_FieldVisible = visible;
            foreach (var chunk in m_Chunks)
            {
                chunk.SetActive(m_FieldVisible);
            }
        }

        /// <summary>セル座標 → <see cref="TerrainRoot"/> ローカル位置。EntityRenderer と共用する。</summary>
        public Vector3 CellToLocal(Int3 cell)
        {
            return new Vector3(
                cell.x * k_BlockSize - m_OffsetX,
                (cell.y + 0.5f) * k_BlockSize,
                cell.z * k_BlockSize - m_OffsetZ);
        }

        void Update()
        {
            if (m_Origin == null || m_Origin.OriginTransform == null)
            {
                return;
            }

            if (!m_Built)
            {
                // 部屋モードは観測が届くまで何も作らない（箱庭グリッドは生成しない）
                if (IsRoomMode && m_RoomBuilder.Observation == null)
                {
                    return;
                }
                m_Built = true;
                Build();
                return;
            }

            bool switchRequested = m_SwitchRequested;
            m_SwitchRequested = false;
            if (switchRequested && Time.unscaledTime - m_LastSwitchTime >= k_SwitchCooldown)
            {
                m_LastSwitchTime = Time.unscaledTime;
                m_SeedIndex = (m_SeedIndex + 1) % m_Seeds.Length;
                Build();
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
        }

        void Build()
        {
            ClearChunks();

            var stopwatch = Stopwatch.StartNew();

            var p = TerrainParams.Default;
            p.seed = m_Seeds[m_SeedIndex];
            p.width = k_Width;
            p.depth = k_Depth;
            p.maxHeight = k_MaxHeight;

            if (IsRoomMode)
            {
                BuildRoomWorld(p);
            }
            else
            {
                m_World = World.Create(p);
                RoomComposed = null;
                CreateDioramaRoot();
            }

            m_TickAccumulator = 0f;

            int blockCount = 0;
            foreach (var pair in m_World.Grid.Chunks)
            {
                var mesh = ChunkMesher.BuildChunkMesh(m_World.Grid, pair.Key, pair.Value, k_BlockSize);
                if (mesh != null)
                {
                    CreateChunkObject(pair.Key, mesh);
                }

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

            if (IsRoomMode)
            {
                Debug.Log($"[TerrainField] 部屋地形の生成完了 (G7): seed={p.seed}, " +
                    $"{m_World.Width}x{m_World.Depth}, ブロック {blockCount} 個, " +
                    $"チャンク {m_Chunks.Count} 個, {GenerationMs} ms");
                Debug.Log($"[TerrainField] 合成内訳: 積もり面={RoomComposed.SurfaceCount} " +
                    $"層数[{RoomComposed.HistogramText()}] バイオーム[{RoomComposed.BiomeText()}] " +
                    $"壁ブロック={RoomComposed.WallCellCount} cellY範囲={RoomComposed.MinCellY}..{RoomComposed.MaxCellY} " +
                    $"ハッシュ={m_World.Grid.ComputeContentHash():X16}");
                LogSuitabilitySummary();
            }
            else
            {
                Debug.Log($"[TerrainField] 地形生成完了: seed={p.seed}, {k_Width}x{k_Depth}x{k_MaxHeight}, " +
                    $"ブロック {blockCount} 個, チャンク {m_Chunks.Count} 個, {GenerationMs} ms");
            }
            DebugPanel.Notify($"terrain seed={p.seed} ({GenerationMs}ms)");
        }

        /// <summary>部屋の観測から World を作り、表示ルートをアンカー相対に固定する (G7)。</summary>
        void BuildRoomWorld(TerrainParams p)
        {
            var observation = m_RoomBuilder.Observation;
            var snow = SnowfallParams.Default;
            snow.seed = p.seed;

            m_World = World.CreateFromRoom(observation, p, snow, out var composed);
            RoomComposed = composed;

            // リプレイ入力として観測を記録する (G1)。World がこの観測から作られるので、
            // 記録できるのは生成後のここだけ
            m_World.RecordObservation(observation);
            Debug.Log($"[TerrainField] 観測を EventLog へ記録 " +
                $"(payloadIndex={m_World.EventLog.Observations.Count - 1}, " +
                $"hash={observation.ComputeContentHash():X16})");

            // 観測セル (x,z) のレイはセル中心 min + (x+0.5)*cell を通る。
            // CellToLocal はセル (x,y,z) の中心を (x*cell, (y+0.5)*cell, z*cell) に置くので、
            // ルートを半セルずらすと観測時のワールド座標に一致する（オフセットは 0）。
            m_OffsetX = 0f;
            m_OffsetZ = 0f;

            float cell = observation.CellSize;
            var scanWorldPose = new Pose(
                new Vector3(
                    observation.OriginWorldX + cell * 0.5f,
                    0f,
                    observation.OriginWorldZ + cell * 0.5f),
                Quaternion.identity);

            m_TerrainRoot = new GameObject("Room Terrain");
            AttachToAnchor(m_TerrainRoot.transform, scanWorldPose);
        }

        void CreateDioramaRoot()
        {
            m_OffsetX = k_Width * 0.5f * k_BlockSize;
            m_OffsetZ = k_Depth * 0.5f * k_BlockSize;

            m_TerrainRoot = new GameObject("Diorama Terrain");
            m_TerrainRoot.transform.SetParent(m_Origin.OriginTransform, false);
        }

        /// <summary>
        /// 観測時のワールドポーズを保ったまま、アンカー原点の配下へ取り付ける (Demo 4.5)。
        ///
        /// ワールド座標は HMD の着脱による再ローカライズでずれるが、スパシャルアンカーは
        /// 現実の部屋に貼り付いたままである（Demo 0 T2）。観測**と同じ瞬間**のポーズで
        /// 換算するのは、スキャンから合成までの間の再ローカライズを取りこぼさないため。
        ///
        /// 変換するのは表示配置だけで、観測データ (cellY) は整数のまま。
        /// M4 のリプレイ入力とコンテンツハッシュには影響しない。
        /// </summary>
        void AttachToAnchor(Transform target, Pose scanWorldPose)
        {
            var originTransform = m_Origin.OriginTransform;
            var scan = m_RoomBuilder != null && m_RoomBuilder.scanner != null
                ? m_RoomBuilder.scanner.Result
                : null;

            Pose anchorPose;
            if (scan != null && scan.HasOriginPose)
            {
                anchorPose = scan.OriginPoseAtScan;
            }
            else
            {
                anchorPose = new Pose(originTransform.position, originTransform.rotation);
                Debug.LogWarning("[TerrainField] 観測時のアンカーポーズが無い（原点確定前にスキャンした）。" +
                    "現在のポーズで換算するため、その間に再ローカライズがあるとずれる。");
            }

            var inverseRotation = Quaternion.Inverse(anchorPose.rotation);
            target.SetParent(originTransform, false);
            target.localPosition = inverseRotation * (scanWorldPose.position - anchorPose.position);
            target.localRotation = inverseRotation * scanWorldPose.rotation;
        }

        /// <summary>
        /// 適性場の分布を高さ帯ごとに出す (G7 の確認用)。
        /// 表面場の意味論どおり、床・机上・棚上のそれぞれに湧ける場所があるかを実機ログで見る。
        /// </summary>
        void LogSuitabilitySummary()
        {
            int baseCellY = RoomComposed.BaseCellY;
            int lowCells = 0, midCells = 0, highCells = 0;   // 適性 > 0 のセル数
            int lowTotal = 0, midTotal = 0, highTotal = 0;

            for (int z = 0; z < m_World.Depth; z++)
            {
                for (int x = 0; x < m_World.Width; x++)
                {
                    int h = m_World.GetSurfaceHeight(x, z);
                    if (h == World.NoSurfaceHeight)
                    {
                        continue;
                    }
                    int rel = h - baseCellY;
                    bool ok = m_World.Suitability.GetAtColumn(x, z) > 0f;

                    // 床 (< 0.4m) / 机の高さ (0.4〜1.2m) / 棚の高さ (>= 1.2m)
                    if (rel < 10) { lowTotal++; if (ok) lowCells++; }
                    else if (rel < 30) { midTotal++; if (ok) midCells++; }
                    else { highTotal++; if (ok) highCells++; }
                }
            }

            Debug.Log($"[TerrainField] 適性場の分布 (G7): " +
                $"床帯 {lowCells}/{lowTotal} / 机帯 {midCells}/{midTotal} / 棚帯 {highCells}/{highTotal} " +
                $"(適性>0 のセル数 / 面のあるセル数)。基準セルY={baseCellY}");
        }

        void CreateChunkObject(Int3 chunkCoord, Mesh mesh)
        {
            m_Meshes.Add(mesh);
            var go = new GameObject($"Chunk {chunkCoord}");
            go.transform.SetParent(m_TerrainRoot.transform, false);
            go.transform.localPosition = new Vector3(
                chunkCoord.x * Chunk.Size * k_BlockSize - m_OffsetX,
                chunkCoord.y * Chunk.Size * k_BlockSize,
                chunkCoord.z * Chunk.Size * k_BlockSize - m_OffsetZ);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m_TerrainMaterial;
            go.SetActive(m_FieldVisible);
            m_Chunks.Add(go);
            m_ChunkMap[chunkCoord] = go;
        }

        /// <summary>変更のあったチャンクのみ再メッシュする (Demo 4 F3 / M5計測)。</summary>
        void RemeshDirtyChunks()
        {
            if (m_TerrainRoot == null || !m_World.ConsumeDirtyChunks(m_DirtyBuffer))
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
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
                    CreateChunkObject(chunkCoord, newMesh);
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

            if (m_TerrainRoot != null)
            {
                Destroy(m_TerrainRoot);
                m_TerrainRoot = null;
            }
        }
    }
}
