using System.Collections.Generic;

namespace BlockField.SimCore.Voxel
{
    /// <summary>
    /// チャンク辞書による疎なボクセルグリッド。ワールドセル座標（負も可）でアクセスする。
    /// </summary>
    public sealed class VoxelGrid
    {
        // Chunk.Size = 16 = 2^4 前提のシフト/マスク。
        // 負座標の検証例: -1>>4 = -1 (チャンク-1), -1&15 = 15 / -16>>4 = -1, -16&15 = 0 / 16>>4 = 1, 16&15 = 0
        const int k_Shift = 4;
        const int k_Mask = Chunk.Size - 1;

        readonly Dictionary<Int3, Chunk> m_Chunks = new Dictionary<Int3, Chunk>();

        /// <summary>生成済みチャンク数。</summary>
        public int ChunkCount => m_Chunks.Count;

        public static Int3 WorldToChunk(Int3 world)
        {
            return new Int3(world.x >> k_Shift, world.y >> k_Shift, world.z >> k_Shift);
        }

        public static Int3 WorldToLocal(Int3 world)
        {
            return new Int3(world.x & k_Mask, world.y & k_Mask, world.z & k_Mask);
        }

        /// <summary>ワールドセル座標の取得。未生成チャンクは Air。</summary>
        public BlockId Get(Int3 world)
        {
            if (!m_Chunks.TryGetValue(WorldToChunk(world), out var chunk))
            {
                return BlockId.Air;
            }

            var local = WorldToLocal(world);
            return chunk.Get(local.x, local.y, local.z);
        }

        /// <summary>ワールドセル座標の設定（出所 Terrain）。互換用エイリアス。</summary>
        public void Set(Int3 world, BlockId id)
        {
            SetBlock(world, id, BlockOrigin.Terrain);
        }

        /// <summary>
        /// ブロックと出所属性の設定 (Demo 4 F1)。必要ならチャンクを生成する
        /// （Air の空チャンク生成は避ける）。
        /// </summary>
        public void SetBlock(Int3 world, BlockId id, BlockOrigin origin)
        {
            var chunkCoord = WorldToChunk(world);
            if (!m_Chunks.TryGetValue(chunkCoord, out var chunk))
            {
                if (id == BlockId.Air)
                {
                    return; // 未生成チャンクへの Air 書き込みは no-op
                }
                chunk = new Chunk();
                m_Chunks.Add(chunkCoord, chunk);
            }

            var local = WorldToLocal(world);
            chunk.Set(local.x, local.y, local.z, id, origin);
        }

        /// <summary>出所属性の取得。未生成チャンクは Terrain。</summary>
        public BlockOrigin GetOrigin(Int3 world)
        {
            if (!m_Chunks.TryGetValue(WorldToChunk(world), out var chunk))
            {
                return BlockOrigin.Terrain;
            }
            var local = WorldToLocal(world);
            return chunk.GetOrigin(local.x, local.y, local.z);
        }

        /// <summary>
        /// 生態系文脈からの書き込み口（固定レイヤー原則のAPI強制）。
        /// 対象セルの出所が Player の場合は書き込まず false を返す。
        /// 生態系から地形を変更するコード（木の成長等）は必ずこの口を使うこと。
        /// </summary>
        public bool TrySetBlockEcology(Int3 world, BlockId id)
        {
            if (GetOrigin(world) == BlockOrigin.Player)
            {
                return false;
            }
            SetBlock(world, id, BlockOrigin.Ecology);
            return true;
        }

        /// <summary>生成済みチャンクの列挙（順序は不定。決定論が必要な処理は座標でソートすること）。</summary>
        public IEnumerable<KeyValuePair<Int3, Chunk>> Chunks => m_Chunks;

        /// <summary>
        /// グリッド全体の決定論的コンテンツハッシュ (FNV-1a 64bit)。
        /// チャンクを座標順にソートしてから、座標とブロック列を順に畳み込む。M3 判定に使用。
        /// </summary>
        public ulong ComputeContentHash()
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;

            var keys = new List<Int3>(m_Chunks.Keys);
            keys.Sort((a, b) =>
            {
                int c = a.x.CompareTo(b.x);
                if (c != 0) return c;
                c = a.y.CompareTo(b.y);
                if (c != 0) return c;
                return a.z.CompareTo(b.z);
            });

            ulong hash = fnvOffset;
            foreach (var key in keys)
            {
                hash = FnvStepInt(hash, key.x, fnvPrime);
                hash = FnvStepInt(hash, key.y, fnvPrime);
                hash = FnvStepInt(hash, key.z, fnvPrime);

                var chunk = m_Chunks[key];
                for (int i = 0; i < Chunk.VolumeLength; i++)
                {
                    hash = (hash ^ chunk.GetRaw(i)) * fnvPrime;
                }
                // 出所属性 (Demo 4 F1) も状態の一部としてハッシュに含める
                for (int i = 0; i < Chunk.VolumeLength; i++)
                {
                    hash = (hash ^ chunk.GetRawOrigin(i)) * fnvPrime;
                }
            }

            return hash;
        }

        static ulong FnvStepInt(ulong hash, int value, ulong prime)
        {
            unchecked
            {
                uint v = (uint)value;
                hash = (hash ^ (v & 0xFF)) * prime;
                hash = (hash ^ ((v >> 8) & 0xFF)) * prime;
                hash = (hash ^ ((v >> 16) & 0xFF)) * prime;
                hash = (hash ^ ((v >> 24) & 0xFF)) * prime;
                return hash;
            }
        }
    }
}
