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
        public Field Suitability { get; }
        public VegetationField Vegetation { get; }
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

        readonly int[] m_KindCounts = new int[5];
        readonly int[] m_SurfaceHeights;
        readonly List<Entity> m_Entities = new List<Entity>();
        readonly Dictionary<Int3, int> m_OccupiedCells = new Dictionary<Int3, int>();
        readonly Dictionary<int, int> m_IdToIndex = new Dictionary<int, int>();
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

            for (int i = 0; i < Suitability.Length; i++)
            {
                hash = FoldUInt(hash, (uint)BitConverter.SingleToInt32Bits(Suitability.GetByIndex(i)), prime);
            }

            for (int i = 0; i < Vegetation.Values.Length; i++)
            {
                hash = FoldUInt(hash, (uint)BitConverter.SingleToInt32Bits(Vegetation.Values.GetByIndex(i)), prime);
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
        static Field ComputeSuitability(TerrainParams p, int[] heights, VoxelGrid grid)
        {
            var field = new Field(p.width, p.depth);
            for (int z = 0; z < p.depth; z++)
            {
                for (int x = 0; x < p.width; x++)
                {
                    int h = heights[x + p.width * z];
                    var surface = grid.Get(new Int3(x, h - 1, z));
                    if (surface != BlockId.Grass)
                    {
                        field.Set(x, z, 0f);
                        continue;
                    }

                    bool flat = true;
                    Check(x + 1, z);
                    Check(x - 1, z);
                    Check(x, z + 1);
                    Check(x, z - 1);

                    field.Set(x, z, flat ? 1f : 0.5f);

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
