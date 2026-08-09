using System;
using System.Collections.Generic;
using BlockField.SimCore.Rng;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// ワールド状態 (Demo 2 D1 / Demo 3 E1-E5):
    /// VoxelGrid ＋ エンティティ ＋ 場（適性・植生）＋ ティックカウンタ を束ねる。
    /// ContentHash は状態全て（地形・場・エンティティのhunger/breedCooldown含む）を対象とし、
    /// 決定論 f(シード) を M3 テストで検証する（将来 f(シード, イベントログ) へ拡張）。
    /// PopulationLog / EventLog は「入力・観測の記録」であり ContentHash には含めない。
    /// </summary>
    public sealed class World
    {
        /// <summary>シムRNGのシード派生用撹拌定数（地形シードと独立な乱数列にする）。</summary>
        const uint k_SimSeedSalt = 0xB5297A4Du;

        public VoxelGrid Grid { get; }
        public SuitabilityField Suitability { get; }
        public VegetationField Vegetation { get; }

        /// <summary>
        /// 場の一元管理 (Demo 4.5 作業1)。ContentHash 計算と更新ループが場の種類を
        /// 知らずに回るための辞書。決定論のため名前昇順で走査する（m_FieldOrder）。
        /// </summary>
        public IReadOnlyDictionary<string, IField> Fields => m_Fields;
        public TerrainParams Params { get; }
        public Mulberry32 Rng { get; }
        public PopulationLog PopulationLog { get; }
        public EventLog EventLog { get; }

        /// <summary>経過シムティック数（Simulation.Tick が加算する）。</summary>
        public long TickCount { get; internal set; }

        public int Width => Params.width;
        public int Depth => Params.depth;

        // 統計（表示用の累計。導出値なので ContentHash には含めない）
        public int StarvationCount { get; internal set; }
        public int PredationCount { get; internal set; }
        public int BirthCount { get; internal set; }

        readonly struct PendingAction
        {
            public readonly SimEventType type;
            public readonly Int3 cell;
            public readonly BlockId blockId;

            public PendingAction(SimEventType type, Int3 cell, BlockId blockId)
            {
                this.type = type;
                this.cell = cell;
                this.blockId = blockId;
            }
        }

        readonly int[] m_KindCounts = new int[5];
        readonly int[] m_SurfaceHeights;
        readonly List<Entity> m_Entities = new List<Entity>();
        readonly Dictionary<Int3, int> m_OccupiedCells = new Dictionary<Int3, int>();
        readonly Dictionary<int, int> m_IdToIndex = new Dictionary<int, int>();
        readonly Dictionary<string, IField> m_Fields = new Dictionary<string, IField>();
        readonly List<string> m_FieldOrder = new List<string>();
        readonly List<PendingAction> m_PendingActions = new List<PendingAction>();
        readonly HashSet<Int3> m_DirtyChunks = new HashSet<Int3>();
        readonly HashSet<int> m_FeedbackDeadScratch = new HashSet<int>();
        int m_NextEntityId;

        /// <summary>エンティティ列（id 昇順を維持）。</summary>
        public IReadOnlyList<Entity> Entities => m_Entities;

        public int PlantCount => m_KindCounts[(int)EntityKind.GrassTuft] + m_KindCounts[(int)EntityKind.Flower];
        public int SheepCount => m_KindCounts[(int)EntityKind.Sheep];
        public int PigCount => m_KindCounts[(int)EntityKind.Pig];
        public int WolfCount => m_KindCounts[(int)EntityKind.Wolf];
        public int AnimalCount => SheepCount + PigCount + WolfCount;

        World(TerrainParams p)
        {
            Params = p;
            Grid = TerrainGenerator.Generate(p);
            Rng = new Mulberry32(p.seed ^ k_SimSeedSalt);
            PopulationLog = new PopulationLog();
            EventLog = new EventLog();

            m_SurfaceHeights = ComputeSurfaceHeights(Grid, p);
            Suitability = ComputeSuitability(p, m_SurfaceHeights, Grid);
            Vegetation = new VegetationField(p.width, p.depth);

            RegisterField(Suitability);
            RegisterField(Vegetation);
        }

        /// <summary>
        /// 場の登録。決定論のため名前昇順に並べ替えて保持する
        /// （辞書の列挙順は不定なので、ContentHash と更新は m_FieldOrder を使う）。
        /// </summary>
        void RegisterField(ScalarField field)
        {
            m_Fields.Add(field.Name, field);
            m_FieldOrder.Add(field.Name);
            m_FieldOrder.Sort(StringComparer.Ordinal);

            // 表面場の前提検証をデバッグビルドで有効化する
            field.SurfaceHeightProvider = GetSurfaceHeight;
        }

        /// <summary>全ての場の毎ティック更新（種類を知らずに回る）。</summary>
        internal void UpdateFields(SimParams p)
        {
            foreach (var name in m_FieldOrder)
            {
                m_Fields[name].Update(p);
            }
        }

        public static World Create(TerrainParams terrainParams)
        {
            return new World(terrainParams);
        }

        public bool InBounds(int x, int z) => x >= 0 && x < Width && z >= 0 && z < Depth;

        /// <summary>柱の表層高さ（= 表層の上の空セルの y）。</summary>
        public int GetSurfaceHeight(int x, int z) => m_SurfaceHeights[x + Width * z];

        public bool IsCellOccupied(Int3 cell) => m_OccupiedCells.ContainsKey(cell);

        /// <summary>セル上のエンティティのリストインデックスを取得。</summary>
        public bool TryGetEntityIndexAt(Int3 cell, out int index)
        {
            if (m_OccupiedCells.TryGetValue(cell, out int id))
            {
                index = m_IdToIndex[id];
                return true;
            }
            index = -1;
            return false;
        }

        /// <summary>
        /// (x, z) 柱の表層上セルへスポーンを試みる（同一セルに2つ生成しない原則）。
        /// 成功時は新しいエンティティの id、失敗時は -1 を返す。
        /// </summary>
        public int TrySpawn(EntityKind kind, int x, int z, int facing)
        {
            if (!InBounds(x, z))
            {
                return -1;
            }

            var cell = new Int3(x, GetSurfaceHeight(x, z), z);
            if (m_OccupiedCells.ContainsKey(cell))
            {
                return -1;
            }

            var entity = new Entity
            {
                id = m_NextEntityId++,
                kind = kind,
                cell = cell,
                facing = facing,
                hunger = 0f,
                breedCooldown = 0,
            };
            m_Entities.Add(entity);
            m_OccupiedCells.Add(cell, entity.id);
            m_IdToIndex.Add(entity.id, m_Entities.Count - 1);
            m_KindCounts[(int)kind]++;
            return entity.id;
        }

        /// <summary>
        /// プレイヤー操作の投入 (Demo 4 F2)。次の Tick 先頭で tick順・投入順に適用される。
        /// </summary>
        public void EnqueuePlayerAction(SimEventType type, Int3 cell, BlockId blockId)
        {
            m_PendingActions.Add(new PendingAction(type, cell, blockId));
        }

        /// <summary>
        /// キューされた操作の適用（Simulation.Tick の先頭から呼ばれる）。
        /// - Place: 対象セルが Air の場合のみ設置（出所 Player）
        /// - Break: 非 Air の場合のみ Air 化（origin は Terrain に正規化）。
        ///   破壊時点の blockId を監査用に記録
        /// - 無効操作は状態を変えず applied=false で EventLog に残す
        /// 適用は RNG を消費しないため、リプレイ決定論 f(シード, イベントログ) が成立する。
        /// </summary>
        internal void ApplyPendingActions()
        {
            if (m_PendingActions.Count == 0)
            {
                return;
            }

            foreach (var action in m_PendingActions)
            {
                bool applied = false;
                byte recordedBlock = (byte)action.blockId;

                if (action.type == SimEventType.PlayerPlace)
                {
                    if (Grid.Get(action.cell) == BlockId.Air && action.blockId != BlockId.Air)
                    {
                        Grid.SetBlock(action.cell, action.blockId, BlockOrigin.Player);
                        applied = true;
                    }
                }
                else if (action.type == SimEventType.PlayerBreak)
                {
                    var current = Grid.Get(action.cell);
                    if (current != BlockId.Air)
                    {
                        recordedBlock = (byte)current;
                        Grid.SetBlock(action.cell, BlockId.Air, BlockOrigin.Terrain);
                        applied = true;
                    }
                }
                else if (action.type == SimEventType.PlayerBreakPlant)
                {
                    // 植物の独立破壊: 地形は不変更、植物のみ消滅＋植生場×0.5
                    if (TryGetEntityIndexAt(action.cell, out int index) && m_Entities[index].IsPlant)
                    {
                        recordedBlock = (byte)m_Entities[index].kind; // 監査用: 破壊した植物種
                        m_FeedbackDeadScratch.Clear();
                        m_FeedbackDeadScratch.Add(m_Entities[index].id);
                        RemoveEntities(m_FeedbackDeadScratch);

                        if (InBounds(action.cell.x, action.cell.z))
                        {
                            // 柱単位の操作（表面場のエスケープハッチ。IField のコメント参照）
                            float v = Vegetation.GetAtColumn(action.cell.x, action.cell.z);
                            Vegetation.SetAtColumn(action.cell.x, action.cell.z, v * 0.5f);
                        }
                        applied = true;
                    }
                }

                EventLog.Append(new SimEvent(TickCount, action.type, action.cell, recordedBlock, applied));

                if (applied && action.type != SimEventType.PlayerBreakPlant)
                {
                    ApplyBlockChangeFeedback(action.cell, action.type == SimEventType.PlayerBreak);
                }
            }

            m_PendingActions.Clear();
        }

        /// <summary>
        /// 場フィードバック (Demo 4 F4):
        /// Break 時は当該セル/直上の植物を消滅させ植生場を×0.5。
        /// 表層高さを更新し、自セル＋4近傍の suitability を局所再計算。
        /// 変更チャンク（境界の場合は隣接チャンクも）を DirtyChunks に積む。
        /// </summary>
        void ApplyBlockChangeFeedback(Int3 cell, bool isBreak)
        {
            if (isBreak)
            {
                m_FeedbackDeadScratch.Clear();
                CollectPlantAt(cell, m_FeedbackDeadScratch);
                CollectPlantAt(new Int3(cell.x, cell.y + 1, cell.z), m_FeedbackDeadScratch);
                if (m_FeedbackDeadScratch.Count > 0)
                {
                    RemoveEntities(m_FeedbackDeadScratch);
                }

                if (InBounds(cell.x, cell.z))
                {
                    // 柱単位の操作: 破壊されたブロックの y は表層高さと一致しないため
                    // Int3 API ではなくエスケープハッチを使う（IField のコメント参照）
                    float v = Vegetation.GetAtColumn(cell.x, cell.z);
                    Vegetation.SetAtColumn(cell.x, cell.z, v * 0.5f);
                }
            }

            if (InBounds(cell.x, cell.z))
            {
                UpdateColumnHeight(cell.x, cell.z);
                RecomputeSuitabilityAt(cell.x, cell.z);
                RecomputeSuitabilityAt(cell.x + 1, cell.z);
                RecomputeSuitabilityAt(cell.x - 1, cell.z);
                RecomputeSuitabilityAt(cell.x, cell.z + 1);
                RecomputeSuitabilityAt(cell.x, cell.z - 1);
            }

            MarkChunkDirty(cell);
        }

        void CollectPlantAt(Int3 cell, HashSet<int> result)
        {
            if (TryGetEntityIndexAt(cell, out int index) && m_Entities[index].IsPlant)
            {
                result.Add(m_Entities[index].id);
            }
        }

        /// <summary>指定列の表層高さキャッシュを再計算する。</summary>
        void UpdateColumnHeight(int x, int z)
        {
            int h = 0;
            for (int y = Params.maxHeight - 1; y >= 0; y--)
            {
                if (Grid.Get(new Int3(x, y, z)) != BlockId.Air)
                {
                    h = y + 1;
                    break;
                }
            }
            m_SurfaceHeights[x + Width * z] = h;
        }

        /// <summary>
        /// suitability の局所再計算。生成時のルールに加え、
        /// Player 出所ブロックが表層のセルは 0（設置物の上には湧かない）。
        /// </summary>
        void RecomputeSuitabilityAt(int x, int z)
        {
            if (!InBounds(x, z))
            {
                return;
            }

            int h = GetSurfaceHeight(x, z);
            if (h <= 0)
            {
                Suitability.SetAtColumn(x, z, 0f);
                return;
            }

            var surfaceCell = new Int3(x, h - 1, z);
            if (Grid.Get(surfaceCell) != BlockId.Grass || Grid.GetOrigin(surfaceCell) == BlockOrigin.Player)
            {
                Suitability.SetAtColumn(x, z, 0f);
                return;
            }

            bool flat = true;
            CheckNeighbor(x + 1, z);
            CheckNeighbor(x - 1, z);
            CheckNeighbor(x, z + 1);
            CheckNeighbor(x, z - 1);
            Suitability.SetAtColumn(x, z, flat ? 1f : 0.5f);

            void CheckNeighbor(int nx, int nz)
            {
                if (!InBounds(nx, nz))
                {
                    return;
                }
                if (Math.Abs(GetSurfaceHeight(nx, nz) - h) > 1)
                {
                    flat = false;
                }
            }
        }

        /// <summary>変更セルのチャンク（境界の場合は隣接チャンクも）を再メッシュ対象に積む。</summary>
        void MarkChunkDirty(Int3 cell)
        {
            var chunkCoord = VoxelGrid.WorldToChunk(cell);
            m_DirtyChunks.Add(chunkCoord);

            var local = VoxelGrid.WorldToLocal(cell);
            if (local.x == 0) m_DirtyChunks.Add(new Int3(chunkCoord.x - 1, chunkCoord.y, chunkCoord.z));
            if (local.x == Chunk.Size - 1) m_DirtyChunks.Add(new Int3(chunkCoord.x + 1, chunkCoord.y, chunkCoord.z));
            if (local.y == 0) m_DirtyChunks.Add(new Int3(chunkCoord.x, chunkCoord.y - 1, chunkCoord.z));
            if (local.y == Chunk.Size - 1) m_DirtyChunks.Add(new Int3(chunkCoord.x, chunkCoord.y + 1, chunkCoord.z));
            if (local.z == 0) m_DirtyChunks.Add(new Int3(chunkCoord.x, chunkCoord.y, chunkCoord.z - 1));
            if (local.z == Chunk.Size - 1) m_DirtyChunks.Add(new Int3(chunkCoord.x, chunkCoord.y, chunkCoord.z + 1));
        }

        /// <summary>
        /// 再メッシュ対象チャンクの取り出し (Runtime 用)。buffer に書き出してクリアする。
        /// </summary>
        public bool ConsumeDirtyChunks(List<Int3> buffer)
        {
            buffer.Clear();
            if (m_DirtyChunks.Count == 0)
            {
                return false;
            }
            foreach (var c in m_DirtyChunks)
            {
                buffer.Add(c);
            }
            m_DirtyChunks.Clear();
            return true;
        }

        /// <summary>
        /// リプレイ (Demo 4 F2 / M3): f(シード, イベントログ)。
        /// events（tick昇順）を各ティックの先頭で注入しながら ticks 回 Tick する。
        /// シードは terrainParams.seed。
        /// </summary>
        public static World Replay(TerrainParams terrainParams, SimParams simParams, IReadOnlyList<SimEvent> events, long ticks)
        {
            var world = Create(terrainParams);
            int next = 0;
            for (long t = 0; t < ticks; t++)
            {
                while (next < events.Count && events[next].tick == world.TickCount)
                {
                    var e = events[next++];
                    if (e.type == SimEventType.Observation)
                    {
                        continue; // Demo 4.5 で実装
                    }
                    world.EnqueuePlayerAction(e.type, e.cell, (BlockId)e.blockId);
                }
                Simulation.Tick(world, world.Rng, simParams);
            }
            return world;
        }

        /// <summary>エンティティ更新（セル変更時は占有索引も更新）。Simulation から使用。</summary>
        internal void UpdateEntity(int index, Entity updated)
        {
            var old = m_Entities[index];
            if (old.id != updated.id)
            {
                throw new InvalidOperationException("id の変更は不可");
            }

            if (old.cell != updated.cell)
            {
                m_OccupiedCells.Remove(old.cell);
                m_OccupiedCells.Add(updated.cell, updated.id);
            }
            m_Entities[index] = updated;
        }

        /// <summary>
        /// 指定 id 群のエンティティを削除する（摂食・捕食・餓死）。
        /// リストの id 昇順は維持され、id→index 索引は再構築される。
        /// </summary>
        internal void RemoveEntities(HashSet<int> ids)
        {
            if (ids.Count == 0)
            {
                return;
            }

            for (int i = m_Entities.Count - 1; i >= 0; i--)
            {
                var e = m_Entities[i];
                if (!ids.Contains(e.id))
                {
                    continue;
                }
                m_OccupiedCells.Remove(e.cell);
                m_KindCounts[(int)e.kind]--;
                m_Entities.RemoveAt(i);
            }

            m_IdToIndex.Clear();
            for (int i = 0; i < m_Entities.Count; i++)
            {
                m_IdToIndex.Add(m_Entities[i].id, i);
            }
        }

        /// <summary>
        /// ワールド全体の決定論的コンテンツハッシュ。
        /// 地形 → 適性場 → 植生場 → エンティティ（id順、hunger/breedCooldown含む）→ ティック数。
        /// </summary>
        public ulong ComputeContentHash()
        {
            const ulong prime = 1099511628211UL;

            ulong hash = Grid.ComputeContentHash();

            // 場は名前昇順で畳み込む（辞書の列挙順は不定なため）。
            // 現行の順序は suitability → vegetation で、辞書化前と同一。
            foreach (var name in m_FieldOrder)
            {
                hash = m_Fields[name].AccumulateHash(hash, prime);
            }

            foreach (var e in m_Entities)
            {
                hash = FoldUInt(hash, (uint)e.id, prime);
                hash = (hash ^ (byte)e.kind) * prime;
                hash = FoldUInt(hash, (uint)e.cell.x, prime);
                hash = FoldUInt(hash, (uint)e.cell.y, prime);
                hash = FoldUInt(hash, (uint)e.cell.z, prime);
                hash = (hash ^ (uint)e.facing) * prime;
                hash = FoldUInt(hash, (uint)BitConverter.SingleToInt32Bits(e.hunger), prime);
                hash = FoldUInt(hash, (uint)e.breedCooldown, prime);
            }

            hash = FoldUInt(hash, (uint)TickCount, prime);
            hash = FoldUInt(hash, (uint)(TickCount >> 32), prime);
            return hash;
        }

        static ulong FoldUInt(ulong hash, uint value, ulong prime)
        {
            unchecked
            {
                hash = (hash ^ (value & 0xFF)) * prime;
                hash = (hash ^ ((value >> 8) & 0xFF)) * prime;
                hash = (hash ^ ((value >> 16) & 0xFF)) * prime;
                hash = (hash ^ ((value >> 24) & 0xFF)) * prime;
                return hash;
            }
        }

        static int[] ComputeSurfaceHeights(VoxelGrid grid, TerrainParams p)
        {
            var heights = new int[p.width * p.depth];
            for (int z = 0; z < p.depth; z++)
            {
                for (int x = 0; x < p.width; x++)
                {
                    int h = 0;
                    for (int y = p.maxHeight - 1; y >= 0; y--)
                    {
                        if (grid.Get(new Int3(x, y, z)) != BlockId.Air)
                        {
                            h = y + 1;
                            break;
                        }
                    }
                    heights[x + p.width * z] = h;
                }
            }
            return heights;
        }

        /// <summary>
        /// 適性場 (D3)。地形から一度だけ計算する静的な場:
        /// 表層 Grass かつ 4近傍との高低差1以下 → 1.0 / Grass だが起伏あり → 0.5 / それ以外 → 0.0。
        /// </summary>
        static SuitabilityField ComputeSuitability(TerrainParams p, int[] heights, VoxelGrid grid)
        {
            var field = new SuitabilityField(p.width, p.depth);
            for (int z = 0; z < p.depth; z++)
            {
                for (int x = 0; x < p.width; x++)
                {
                    int h = heights[x + p.width * z];
                    var surface = grid.Get(new Int3(x, h - 1, z));
                    if (surface != BlockId.Grass)
                    {
                        field.SetAtColumn(x, z, 0f);
                        continue;
                    }

                    bool flat = true;
                    Check(x + 1, z);
                    Check(x - 1, z);
                    Check(x, z + 1);
                    Check(x, z - 1);

                    field.SetAtColumn(x, z, flat ? 1f : 0.5f);

                    void Check(int nx, int nz)
                    {
                        if (nx < 0 || nx >= p.width || nz < 0 || nz >= p.depth)
                        {
                            return;
                        }
                        if (Math.Abs(heights[nx + p.width * nz] - h) > 1)
                        {
                            flat = false;
                        }
                    }
                }
            }
            return field;
        }
    }
}
