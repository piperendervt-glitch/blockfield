using System;
using System.Collections.Generic;
using BlockField.SimCore.Rng;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
// RoomObservation は SimCore.Terrain 名前空間（Demo 4.5 G1）

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

        /// <summary>恐怖場 (Demo 8 第1段)。狼が書き、草食獣が避ける。</summary>
        public FearField Fear { get; }

        /// <summary>獲物場 (Demo 8 第1段)。草食獣が書き、狼が追う。</summary>
        public PreyField Prey { get; }

        /// <summary>死の場 (Demo 8 第2段)。死んだ場所に残り、養分として植生を高める。</summary>
        public DeathField Death { get; }

        /// <summary>踏み荒らし場 (Demo 8 第3段)。動物の通行で植生を抑える。死の場と対になる。</summary>
        public TrampleField Trample { get; }

        /// <summary>
        /// 場の一元管理 (Demo 4.5 作業1)。ContentHash 計算と更新ループが場の種類を
        /// 知らずに回るための辞書。決定論のため名前昇順で走査する（m_FieldOrder）。
        /// </summary>
        public IReadOnlyDictionary<string, IField> Fields => m_Fields;

        /// <summary>
        /// 識別子から場を引く (Demo 8 第4段 K2)。結合の適用は毎ティック全セルを
        /// 回る経路にあるため、<see cref="Fields"/> の辞書ではなく分岐で解決する。
        /// </summary>
        public ScalarField GetField(FieldId id) => id switch
        {
            FieldId.Death => Death,
            FieldId.Fear => Fear,
            FieldId.Prey => Prey,
            FieldId.Suitability => Suitability,
            FieldId.Trample => Trample,
            FieldId.Vegetation => Vegetation,
            _ => throw new ArgumentOutOfRangeException(nameof(id), $"未知の場: {id}"),
        };
        public TerrainParams Params { get; }
        public Mulberry32 Rng { get; }
        public PopulationLog PopulationLog { get; }
        public EventLog EventLog { get; }

        /// <summary>経過シムティック数（Simulation.Tick が加算する）。</summary>
        public long TickCount { get; internal set; }

        public int Width => Params.width;
        public int Depth => Params.depth;

        /// <summary>
        /// 生成時点で適性 &gt; 0 だったセル数 (Demo 5a)。個体数の上限・頻度を
        /// このスケールに比例させる（<see cref="SimParams.Resolve"/>）。
        ///
        /// **生成時に1回だけ数えて固定する。** 設置・破壊で適性が局所再計算されても
        /// 追随しない — これは「このワールドの広さ」を表す基準値であり、
        /// ブロック1個の増減で上限が揺れると個体数が不安定になるため。
        /// </summary>
        public int SuitableCellCount { get; }

        /// <summary>
        /// 適性 &gt; 0 のセルの平坦インデックス（(z, x) の走査順）(Demo 8.5 段階3)。
        ///
        /// 場が草そのものになったことで、毎ティック全セルを走査する処理が増えた。
        /// 適性セルは生成時に固定なので、対象セルの列を一度作っておけば
        /// 毎ティックの「適性を引いて0なら飛ばす」を省ける。
        ///
        /// **順序は走査順そのまま**にしてある。順序を変えると
        /// 浮動小数の演算順が変わり決定論が壊れるため。
        ///
        /// 注: <see cref="ApplyPendingActions"/> による設置・破壊で適性は
        /// 局所的に変わりうるが、この列は追随しない。
        /// <see cref="SuitableCellCount"/> と同じ扱い（「このワールドの広さ」を
        /// 表す生成時の基準）であり、ブロック1個の増減で草の成長対象が
        /// 揺れるほうが不自然なため。
        /// </summary>
        public int[] SuitableCellIndices { get; }

        // 統計（表示用の累計。導出値なので ContentHash には含めない）
        public int StarvationCount { get; internal set; }
        public int PredationCount { get; internal set; }
        public int BirthCount { get; internal set; }

        /// <summary>
        /// 摂食モードに入った回数 (Demo 5a の診断表示)。空腹が閾値を超えて
        /// 「食べ物を探した」回数であり、成否は問わない。
        /// </summary>
        public int FeedAttemptCount { get; internal set; }

        /// <summary>
        /// 摂食に成功した回数 (Demo 5a の診断表示)。
        /// <see cref="FeedAttemptCount"/> との比が「餓死する前に食べられているか」の直接指標になる。
        /// </summary>
        public int FeedSuccessCount { get; internal set; }

        /// <summary>
        /// 狼が実際に移動したセル数 (Demo 8 M5 の指標)。
        /// 「何歩歩いて1匹捕らえたか」を出すための分母。
        /// </summary>
        public int WolfStepCount { get; internal set; }

        /// <summary>
        /// 草食獣が実際に移動したうち、恐怖場の**低い**セルへ動いた回数 (Demo 8 第2段 M3)。
        /// </summary>
        public int HerbivoreMovesAwayFromFear { get; internal set; }

        /// <summary>
        /// 草食獣が実際に移動したうち、恐怖場の**高い**セルへ動いた回数 (Demo 8 第2段 M3)。
        /// この2つの比が迂回行動の直接的な指標になる。移動が起きた瞬間だけを見るので、
        /// 動かなかったティックや、危険地帯で捕食されて消えた個体の影響を受けない。
        /// </summary>
        public int HerbivoreMovesTowardFear { get; internal set; }

        /// <summary>踏み潰された植物の累計 (Demo 8 第3段)。表示用の導出値。</summary>
        public int TrampleCrushCount { get; internal set; }

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

        readonly int[] m_KindCounts = new int[3];
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

        /// <summary>
        /// 草の総量 (Demo 8.5)。植物は Entity でなくなったので「本数」は存在しない。
        ///
        /// **これは「草のあるセル数」ではなく「植生場の全セルの値の合計」である。**
        /// 適性0のセル（壁・穴）も含む全セルの和で、1セルの上限が 1.0 なので
        /// 理論上の最大は セル数（50x50 なら 2,500）。
        /// 実測では 300 前後になる（適性2,225セル × 平均0.135）。
        /// 移行前の PlantCount（本数、実測155前後）の置き換えだが、
        /// **数え方が違うので直接比較してはいけない**。
        ///
        /// 値は毎ティックの場の更新時に副産物として集計されたもので、
        /// その後の摂食・踏み潰しは反映されていない（表示用の導出値であり、
        /// 1ティック遅れても意味が変わらないため）。
        /// </summary>
        public float VegetationTotal => Vegetation.LastSum;

        public int SheepCount => m_KindCounts[(int)EntityKind.Sheep];
        public int PigCount => m_KindCounts[(int)EntityKind.Pig];
        public int WolfCount => m_KindCounts[(int)EntityKind.Wolf];
        public int AnimalCount => SheepCount + PigCount + WolfCount;

        /// <summary>
        /// 表層高さ探索の下限・上限（セルY）と、面が無い柱に入れる値。
        /// 箱庭 (Demo 1-4) は 0..maxHeight-1 で「面なし=0」。
        /// 部屋地形 (Demo 4.5) はセルYが負にも 50 超にもなり、0 も正当な高さなので
        /// 「面なし」に極端な負値を入れて、隣接判定 (|Δh| &lt;= 1) が必ず失敗するようにする。
        /// </summary>
        readonly int m_ScanMinY;
        readonly int m_ScanMaxY;
        readonly int m_NoSurfaceHeight;

        /// <summary>面を持たない柱の表層高さ（部屋地形用の番兵）。減算しても溢れない大きさにする。</summary>
        public const int NoSurfaceHeight = int.MinValue / 4;

        World(TerrainParams p)
        {
            Params = p;
            Grid = TerrainGenerator.Generate(p);
            Rng = new Mulberry32(p.seed ^ k_SimSeedSalt);
            PopulationLog = new PopulationLog();
            EventLog = new EventLog();

            m_ScanMinY = 0;
            m_ScanMaxY = p.maxHeight - 1;
            m_NoSurfaceHeight = 0;

            m_SurfaceHeights = ComputeSurfaceHeights(Grid, p.width, p.depth, m_ScanMinY, m_ScanMaxY, m_NoSurfaceHeight);
            Suitability = ComputeSuitability(p.width, p.depth, m_SurfaceHeights, Grid, m_NoSurfaceHeight);
            Vegetation = new VegetationField(p.width, p.depth);
            Fear = new FearField(p.width, p.depth);
            Prey = new PreyField(p.width, p.depth);
            Death = new DeathField(p.width, p.depth);
            Trample = new TrampleField(p.width, p.depth);
            SuitableCellCount = CountSuitableCells(Suitability, p.width, p.depth);
            SuitableCellIndices = CollectSuitableCells(Suitability, p.width, p.depth);

            RegisterField(Suitability);
            RegisterField(Vegetation);
            RegisterField(Fear);
            RegisterField(Prey);
            RegisterField(Death);
            RegisterField(Trample);
        }

        /// <summary>
        /// 部屋地形の上にワールドを作る (Demo 4.5 G7 の下ごしらえ)。
        /// 合成済みの部屋グリッドをそのままシムの舞台にする。
        ///
        /// 箱庭 (Demo 1-4) との違いは3点だけで、シムのルール自体は共通である:
        /// - グリッドは生成せず受け取る（TerrainGenerator は使わない）
        /// - XZ の範囲は観測グリッドの寸法
        /// - セルYが負にもなるため、表層高さの探索範囲と「面なし」の表現が異なる
        /// </summary>
        World(TerrainParams p, VoxelGrid grid, int scanMinY, int scanMaxY)
        {
            Params = p;
            Grid = grid;
            Rng = new Mulberry32(p.seed ^ k_SimSeedSalt);
            PopulationLog = new PopulationLog();
            EventLog = new EventLog();

            m_ScanMinY = scanMinY;
            m_ScanMaxY = scanMaxY;
            m_NoSurfaceHeight = NoSurfaceHeight;

            m_SurfaceHeights = ComputeSurfaceHeights(Grid, p.width, p.depth, m_ScanMinY, m_ScanMaxY, m_NoSurfaceHeight);
            Suitability = ComputeSuitability(p.width, p.depth, m_SurfaceHeights, Grid, m_NoSurfaceHeight);
            Vegetation = new VegetationField(p.width, p.depth);
            Fear = new FearField(p.width, p.depth);
            Prey = new PreyField(p.width, p.depth);
            Death = new DeathField(p.width, p.depth);
            Trample = new TrampleField(p.width, p.depth);
            SuitableCellCount = CountSuitableCells(Suitability, p.width, p.depth);
            SuitableCellIndices = CollectSuitableCells(Suitability, p.width, p.depth);

            RegisterField(Suitability);
            RegisterField(Vegetation);
            RegisterField(Fear);
            RegisterField(Prey);
            RegisterField(Death);
            RegisterField(Trample);
        }

        /// <summary>
        /// 適性 &gt; 0 のセルの平坦インデックスを走査順に集める。
        /// 順序は (z, x) の二重ループそのままで、変えてはいけない
        /// （浮動小数の演算順が変わり決定論が壊れる）。
        /// </summary>
        static int[] CollectSuitableCells(SuitabilityField field, int width, int depth)
        {
            var list = new List<int>();
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (field.GetAtColumn(x, z) > 0f)
                    {
                        list.Add(x + width * z);
                    }
                }
            }
            return list.ToArray();
        }

        /// <summary>適性 &gt; 0 のセル数（生成時の基準スケール）。</summary>
        static int CountSuitableCells(SuitabilityField field, int width, int depth)
        {
            int n = 0;
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (field.GetAtColumn(x, z) > 0f)
                    {
                        n++;
                    }
                }
            }
            return n;
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

        /// <summary>
        /// 観測から部屋地形を合成し、その上にワールドを作る (Demo 4.5 G4/G5/G7)。
        /// 入力は整数の観測データとシードのみなので、同一観測からは同一ワールドになる。
        /// </summary>
        public static World CreateFromRoom(
            RoomObservation observation, TerrainParams terrainParams, SnowfallParams snowParams,
            out SnowfallResult composed)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            composed = SnowfallComposer.Compose(observation, snowParams);

            // XZ の範囲は観測グリッドに合わせる（maxHeight は表層探索に使わない）
            var p = terrainParams;
            p.width = observation.Width;
            p.depth = observation.Depth;

            // 探索範囲は合成結果の実レンジ。面が1つも無ければ空のワールドになる
            int minY = composed.BlockCount > 0 ? composed.MinCellY : 0;
            int maxY = composed.BlockCount > 0 ? composed.MaxCellY : 0;
            return new World(p, composed.Grid, minY, maxY);
        }

        public bool InBounds(int x, int z) => x >= 0 && x < Width && z >= 0 && z < Depth;

        /// <summary>
        /// 生き物が湧ける表層ブロックか (D3)。
        /// Grass に加えて Snow を含める — 部屋地形 (Demo 4.5 G5) では山岳バイオームの
        /// 表層が Snow になるため、Grass だけにすると棚の上など高所に一切湧かなくなる。
        /// 「積もった地表」であることが条件であり、Stone（厚い柱の頂上・壁）は対象外。
        /// 箱庭 (Demo 1-4) には Snow が存在しないので従来の挙動は変わらない。
        /// </summary>
        static bool IsFertileSurface(BlockId id) => id == BlockId.Grass || id == BlockId.Snow;

        /// <summary>柱の表層高さ（= 表層の上の空セルの y）。</summary>
        public int GetSurfaceHeight(int x, int z) => m_SurfaceHeights[x + Width * z];

        public bool IsCellOccupied(Int3 cell) => m_OccupiedCells.ContainsKey(cell);

        /// <summary>
        /// 動物が塞いでいるセルか (Demo 8.5 段階3)。
        ///
        /// 移動の判定はこちらを使う。植物が場になると通行不可の主体でなくなり、
        /// **動物は草の上を歩けるようになる**。段階3の前にこの1点だけを
        /// 先に入れて影響を測れるよう、占有索引そのものとは別のメソッドにした。
        /// </summary>
        public bool IsCellBlockedByAnimal(Int3 cell) =>
            m_OccupiedCells.TryGetValue(cell, out int id) &&
            m_IdToIndex.TryGetValue(id, out int index) &&
            m_Entities[index].IsAnimal;

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
        /// <summary>
        /// 重みを既定値で初期化するスポーン。テストや設置系の呼び出し用。
        /// 個体の重みは <see cref="SimParams.Default"/> から作る。
        /// </summary>
        public int TrySpawn(EntityKind kind, int x, int z, int facing) =>
            TrySpawn(kind, x, z, facing, SimParams.Default);

        /// <summary>
        /// パラメータから重みを初期化してスポーンする (Demo 8 第3段 J2)。
        /// 野生スポーンはこちらを使い、その時点の <see cref="SimParams"/> の
        /// 重みを個体へ写す。
        /// </summary>
        public int TrySpawn(EntityKind kind, int x, int z, int facing, SimParams p) =>
            TrySpawn(kind, x, z, facing,
                EntityWeights.ForagingFor(kind, p), EntityWeights.WanderingFor(kind, p));

        /// <summary>
        /// 重みを明示してスポーンする。繁殖は親の重みをここに渡す
        /// （将来、変異を入れる場所もここ）。
        /// </summary>
        public int TrySpawn(
            EntityKind kind, int x, int z, int facing,
            in EntityWeights forageWeights, in EntityWeights wanderWeights)
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
                forageWeights = forageWeights,
                wanderWeights = wanderWeights,
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
                    // 【廃止された操作】Demo 4 M1d の「植物の独立破壊」は
                    // Demo 8.5（植物の場化）で廃止した。草が場になった以上
                    // 「1本を狙って壊す」は概念的に成立しない。
                    // イベント種別だけは残してある — 過去のイベントログを
                    // リプレイしたときに未知の種別で落ちないようにするため。
                    // 適用時は当該セルの草を半分にするだけになる
                    if (InBounds(action.cell.x, action.cell.z))
                    {
                        // 柱単位の操作（表面場のエスケープハッチ。IField のコメント参照）
                        float v = Vegetation.GetAtColumn(action.cell.x, action.cell.z);
                        Vegetation.SetAtColumn(action.cell.x, action.cell.z, v * 0.5f);
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
                // 移行前はここで当該セル・直上の植物 Entity を消していた。
                // 草が場になったので、消すのは場の値だけになった (Demo 8.5)
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

        /// <summary>指定列の表層高さキャッシュを再計算する。</summary>
        void UpdateColumnHeight(int x, int z)
        {
            int h = m_NoSurfaceHeight;
            for (int y = m_ScanMaxY; y >= m_ScanMinY; y--)
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
            if (h == m_NoSurfaceHeight || h <= m_ScanMinY)
            {
                Suitability.SetAtColumn(x, z, 0f);
                return;
            }

            var surfaceCell = new Int3(x, h - 1, z);
            if (!IsFertileSurface(Grid.Get(surfaceCell)) || Grid.GetOrigin(surfaceCell) == BlockOrigin.Player)
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
        /// 現実観測の記録 (Demo 4.5 G1)。観測データを EventLog の付随テーブルへ入れ、
        /// payloadIndex を持つ Observation イベントを記録する。
        /// 地形合成そのものは G3 で実装する（本メソッドは記録のみ）。
        /// </summary>
        public void RecordObservation(RoomObservation observation)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }
            int payloadIndex = EventLog.AddObservation(observation);
            EventLog.Append(new SimEvent(TickCount, SimEventType.Observation, new Int3(0, 0, 0), 0, true, payloadIndex));
        }

        /// <summary>
        /// リプレイ (Demo 4 F2 / M3, Demo 4.5 G1): f(シード, イベントログ)。
        /// イベント列（tick昇順）を各ティックの先頭で注入しながら ticks 回 Tick する。
        /// シードは terrainParams.seed。
        ///
        /// Observation イベントは付随テーブルごと再構築後のワールドへ引き継ぐ。
        /// 観測からの地形合成は G3 で実装する（現時点では記録の引き継ぎのみ）。
        /// </summary>
        public static World Replay(TerrainParams terrainParams, SimParams simParams, EventLog log, long ticks)
        {
            var world = Create(terrainParams);
            var events = log.Events;
            int next = 0;
            for (long t = 0; t < ticks; t++)
            {
                while (next < events.Count && events[next].tick == world.TickCount)
                {
                    var e = events[next++];
                    if (e.type == SimEventType.Observation)
                    {
                        // 付随テーブルの観測データを引き継ぐ（地形合成は G3）
                        var obs = log.GetObservation(e.payloadIndex);
                        if (obs != null)
                        {
                            world.RecordObservation(obs);
                        }
                        continue;
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

                // 個体の重み (Demo 8 第3段 J2)。進化が入ると個体差そのものが
                // 世界の状態になるので、最初からハッシュ対象にしておく。
                // 場の名前昇順で畳み込む（EntityWeights のメンバ順と一致）
                for (int w = 0; w < EntityWeights.FieldCount; w++)
                {
                    hash = FoldUInt(hash, (uint)BitConverter.SingleToInt32Bits(e.forageWeights[w]), prime);
                    hash = FoldUInt(hash, (uint)BitConverter.SingleToInt32Bits(e.wanderWeights[w]), prime);
                }
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

        static int[] ComputeSurfaceHeights(
            VoxelGrid grid, int width, int depth, int scanMinY, int scanMaxY, int noSurfaceHeight)
        {
            var heights = new int[width * depth];
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int h = noSurfaceHeight;
                    for (int y = scanMaxY; y >= scanMinY; y--)
                    {
                        if (grid.Get(new Int3(x, y, z)) != BlockId.Air)
                        {
                            h = y + 1;
                            break;
                        }
                    }
                    heights[x + width * z] = h;
                }
            }
            return heights;
        }

        /// <summary>
        /// 適性場 (D3)。地形から一度だけ計算する静的な場:
        /// 表層 Grass かつ 4近傍との高低差1以下 → 1.0 / Grass だが起伏あり → 0.5 / それ以外 → 0.0。
        /// </summary>
        static SuitabilityField ComputeSuitability(
            int width, int depth, int[] heights, VoxelGrid grid, int noSurfaceHeight)
        {
            var field = new SuitabilityField(width, depth);
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int h = heights[x + width * z];
                    if (h == noSurfaceHeight)
                    {
                        // 面が無い柱（部屋地形の穴）。適性0
                        field.SetAtColumn(x, z, 0f);
                        continue;
                    }

                    // 表面場: 適性は「その柱の最上面」に付随する。h-1 が表層セル
                    if (!IsFertileSurface(grid.Get(new Int3(x, h - 1, z))))
                    {
                        // 壁 (Stone/Reality)・岩 (Stone) の上には湧かない
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
                        if (nx < 0 || nx >= width || nz < 0 || nz >= depth)
                        {
                            return;
                        }
                        int nh = heights[nx + width * nz];
                        if (nh == noSurfaceHeight || Math.Abs(nh - h) > 1)
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
