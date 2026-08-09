using System;
using System.Collections.Generic;

namespace BlockField.SimCore.Terrain
{
    /// <summary>現実の平面ラベル（Meta OpenXR の semantic label を AR Foundation 経由で受けたもの）。</summary>
    public enum SurfaceLabel : byte
    {
        Unknown = 0,
        Floor = 1,
        Table = 2,
        Ceiling = 3,
        WallFace = 4,
        Couch = 5,
        /// <summary>棚などラベル表に存在しないもの。高さヒューリスティックで扱う（M3 の判定対象外）。</summary>
        Other = 6,
    }

    /// <summary>
    /// 観測された「積もり面」1枚 (Demo 4.5 G1)。
    ///
    /// 【M4 の保証範囲との関係】
    /// ヒット高さ <see cref="cellY"/> は**セル単位の整数**である。これは意図的な制約で、
    /// リプレイ経路から浮動小数点の幾何演算を構造的に排除するため
    /// （prereg demo45 の論点2 決定: (iii) 二本立て）。
    /// <see cref="worldY"/> は表示・デバッグ・フロア構造への移行用に保持する参考値であり、
    /// **地形合成の入力に使ってはならない**（使うと M4 の bit-exact 保証が壊れる）。
    /// </summary>
    public struct SurfaceHit
    {
        /// <summary>セル単位の整数高さ。地形合成の唯一の入力（M4 の保証対象）。</summary>
        public int cellY;

        /// <summary>
        /// ワールド高さ (m)。参考値であり地形合成には使わない。
        /// Step 2 確定事項①（各観測面のワールド高さを必ず含める）。
        /// </summary>
        public float worldY;

        /// <summary>
        /// フロアID（面ごとの安定した識別子）。Step 2 確定事項②。
        /// cellY だけでは「同じ机の面」という所属関係が失われるため別途保持する。
        /// 表面場（Demo 4.5）では未使用だが、フロア構造 (roadmap Demo 6 拡張点 (b)) で使う。
        /// </summary>
        public int floorId;

        /// <summary>この面のラベル（バイオーム対応 G5 の入力）。</summary>
        public SurfaceLabel label;

        public SurfaceHit(int cellY, float worldY, int floorId, SurfaceLabel label)
        {
            this.cellY = cellY;
            this.worldY = worldY;
            this.floorId = floorId;
            this.label = label;
        }
    }

    /// <summary>
    /// 部屋の観測結果 (Demo 4.5 G1)。UnityEngine 非依存のプレーンデータ。
    ///
    /// 【役割】
    /// 現実の部屋スキャン（ARMeshManager / ARPlaneManager）から得た情報のうち、
    /// **地形合成に必要な最小限**をセル単位の整数で保持する。これがリプレイ入力になる。
    ///
    /// 【M4 の保証範囲（prereg demo45 参照）】
    /// M4 が保証するのは「同一の RoomObservation から地形合成以降が同一 ContentHash を
    /// 生む」ことである。生メッシュのアーカイブ（room_mesh_archive_*.bin）は
    /// **反復用資材であり M4 の保証対象外** — アーカイブからリプレイしても
    /// レイキャスト（浮動小数点の幾何演算）が再実行されるため bit-exact は保証されない。
    /// </summary>
    public sealed class RoomObservation
    {
        /// <summary>XZ グリッドの幅（セル数）。</summary>
        public int Width { get; }

        /// <summary>XZ グリッドの奥行（セル数）。</summary>
        public int Depth { get; }

        /// <summary>セル1辺の長さ (m)。参考値（地形合成はセル単位で行う）。</summary>
        public float CellSize { get; }

        /// <summary>グリッド原点のワールド座標 (m)。参考値。</summary>
        public float OriginWorldX { get; }
        public float OriginWorldZ { get; }

        // セルごとの積もり面リスト（高さ昇順）。密配列だが、面を持たないセルは空。
        readonly List<SurfaceHit>[] m_Hits;

        // 通行不可セル (Demo 4.5 G4)。壁面平面をラスタライズした結果。
        readonly bool[] m_Blocked;

        public RoomObservation(int width, int depth, float cellSize, float originWorldX, float originWorldZ)
        {
            if (width <= 0 || depth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"サイズが不正: {width}x{depth}");
            }

            Width = width;
            Depth = depth;
            CellSize = cellSize;
            OriginWorldX = originWorldX;
            OriginWorldZ = originWorldZ;
            m_Hits = new List<SurfaceHit>[width * depth];
            m_Blocked = new bool[width * depth];
        }

        /// <summary>
        /// 通行不可セル (G4) を立てる。壁面のラスタライズ結果であり、
        /// セル単位の bool なので ContentHash に含めても M4 の保証を壊さない。
        /// </summary>
        public void SetBlocked(int x, int z) => m_Blocked[ToIndex(x, z)] = true;

        /// <summary>通行不可セルか (G4)。</summary>
        public bool IsBlocked(int x, int z) => m_Blocked[ToIndex(x, z)];

        /// <summary>通行不可セルの総数（統計用）。</summary>
        public int CountBlocked()
        {
            int n = 0;
            for (int i = 0; i < m_Blocked.Length; i++)
            {
                if (m_Blocked[i]) n++;
            }
            return n;
        }

        /// <summary>セルの積もり面を追加する（高さ昇順を維持）。</summary>
        public void AddHit(int x, int z, SurfaceHit hit)
        {
            int index = ToIndex(x, z);
            var list = m_Hits[index];
            if (list == null)
            {
                list = new List<SurfaceHit>(2);
                m_Hits[index] = list;
            }

            // 高さ昇順で挿入（決定論のため順序を固定する）
            int insertAt = list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                if (hit.cellY < list[i].cellY)
                {
                    insertAt = i;
                    break;
                }
            }
            list.Insert(insertAt, hit);
        }

        /// <summary>セルの積もり面数。</summary>
        public int GetHitCount(int x, int z)
        {
            var list = m_Hits[ToIndex(x, z)];
            return list?.Count ?? 0;
        }

        /// <summary>セルの i 番目の積もり面（高さ昇順）。</summary>
        public SurfaceHit GetHit(int x, int z, int index)
        {
            var list = m_Hits[ToIndex(x, z)];
            if (list == null || index < 0 || index >= list.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"({x}, {z}) に面 {index} は無い");
            }
            return list[index];
        }

        /// <summary>面を持つセルの総数（統計用）。</summary>
        public int CountCellsWithHits()
        {
            int n = 0;
            for (int i = 0; i < m_Hits.Length; i++)
            {
                if (m_Hits[i] != null && m_Hits[i].Count > 0)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>面の総数（統計用）。</summary>
        public int CountHits()
        {
            int n = 0;
            for (int i = 0; i < m_Hits.Length; i++)
            {
                if (m_Hits[i] != null)
                {
                    n += m_Hits[i].Count;
                }
            }
            return n;
        }

        /// <summary>最も面数が多いセル（統計・ログ用）。面が無ければ (-1, -1)。</summary>
        public (int x, int z, int count) FindCellWithMostHits()
        {
            int bestX = -1, bestZ = -1, best = 0;
            for (int z = 0; z < Depth; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int c = GetHitCount(x, z);
                    if (c > best)
                    {
                        best = c;
                        bestX = x;
                        bestZ = z;
                    }
                }
            }
            return (bestX, bestZ, best);
        }

        /// <summary>
        /// 観測データの決定論的ハッシュ。地形合成の入力が同一かを検証するために使う
        /// （M4 の部品テスト）。<see cref="SurfaceHit.worldY"/> は入力ではないため含めない。
        /// </summary>
        public ulong ComputeContentHash()
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offset;
            hash = Fold(hash, (uint)Width, prime);
            hash = Fold(hash, (uint)Depth, prime);

            for (int z = 0; z < Depth; z++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int count = GetHitCount(x, z);
                    bool blocked = IsBlocked(x, z);
                    if (count == 0 && !blocked)
                    {
                        continue;
                    }
                    hash = Fold(hash, (uint)x, prime);
                    hash = Fold(hash, (uint)z, prime);
                    unchecked { hash = (hash ^ (blocked ? 1UL : 0UL)) * prime; }
                    for (int i = 0; i < count; i++)
                    {
                        var hit = GetHit(x, z, i);
                        hash = Fold(hash, (uint)hit.cellY, prime);
                        hash = Fold(hash, (uint)hit.floorId, prime);
                        unchecked { hash = (hash ^ (byte)hit.label) * prime; }
                    }
                }
            }
            return hash;
        }

        static ulong Fold(ulong hash, uint value, ulong prime)
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

        int ToIndex(int x, int z)
        {
            if ((uint)x >= Width || (uint)z >= Depth)
            {
                throw new ArgumentOutOfRangeException(nameof(x), $"観測グリッド外: ({x}, {z})");
            }
            return x + Width * z;
        }
    }
}
