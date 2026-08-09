using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Terrain
{
    /// <summary>雪積もり合成のパラメータ (Demo 4.5 G3)。</summary>
    public struct SnowfallParams
    {
        /// <summary>生成シード。同一の観測＋同一シードからは常に同一の地形になる。</summary>
        public uint seed;

        /// <summary>1面あたりの最小積もり層数。</summary>
        public int minLayers;

        /// <summary>1面あたりの最大積もり層数。</summary>
        public int maxLayers;

        /// <summary>起伏の空間スケール（セル数）。大きいほどなだらか。</summary>
        public float reliefScale;

        /// <summary>平原バイオーム（Floor）の起伏振幅 (0..1)。</summary>
        public float plainsAmplitude;

        /// <summary>丘陵バイオーム（Table / 低所の Other・Unknown）の起伏振幅 (0..1)。</summary>
        public float hillsAmplitude;

        /// <summary>山岳バイオーム（高所の Other・Unknown）の起伏振幅 (0..1)。</summary>
        public float mountainAmplitude;

        /// <summary>
        /// 山岳と判定する床からの高さ（セル数）。既定 30セル = 1.2m
        /// （0.04m/セル）。基準は観測内の最低セルY。
        /// </summary>
        public int mountainCellHeight;

        /// <summary>壁セルに積む高さ（セル数）。徘徊AIの「高低差2以上は移動しない」を確実に満たす。</summary>
        public int wallLayers;

        /// <summary>
        /// 既定値。層数 1〜4（薄い起伏）、起伏スケール 10セル = 0.4m。
        /// 机の天板（40cm 程度）に 2〜3 の起伏の山が乗る粒度。
        /// 振幅は TerrainGenerator と同じ「maxHeight に対する比率」の意味論。
        /// </summary>
        public static SnowfallParams Default => new SnowfallParams
        {
            seed = 12345u,
            minLayers = 1,
            maxLayers = 4,
            reliefScale = 10f,
            plainsAmplitude = 0.35f,
            hillsAmplitude = 0.65f,
            mountainAmplitude = 1f,
            mountainCellHeight = 30,
            wallLayers = 6,
        };
    }

    /// <summary>積もり面のバイオーム (Demo 4.5 G5)。起伏振幅の切り替えに使う。</summary>
    public enum SurfaceBiome : byte
    {
        /// <summary>平原（起伏小）。Floor。</summary>
        Plains = 0,

        /// <summary>丘陵（起伏中）。Table、および床から 1.2m 未満の Other/Unknown。</summary>
        Hills = 1,

        /// <summary>山岳（起伏大）。床から 1.2m 以上の Other/Unknown（棚の上など）。</summary>
        Mountains = 2,
    }

    /// <summary>雪積もり合成の結果（統計付き）。</summary>
    public sealed class SnowfallResult
    {
        /// <summary>合成された地形。セル座標は観測グリッドの (x, cellY, z) と一致する。</summary>
        public VoxelGrid Grid;

        /// <summary>積もらせた面数（= 面を持つセル数。各セルの最上面1枚のみ使う）。</summary>
        public int SurfaceCount;

        /// <summary>設置した非Airブロック数。</summary>
        public int BlockCount;

        /// <summary>層数のヒストグラム。index 0 が「1層」に対応する（長さ = maxLayers）。</summary>
        public int[] LayerHistogram;

        /// <summary>バイオーム別の面数 (G5)。index は <see cref="SurfaceBiome"/>。</summary>
        public int[] BiomeHistogram;

        /// <summary>壁として積んだセル数 (G4)。</summary>
        public int WallCellCount;

        /// <summary>バイオーム判定の基準になった最低セルY（= 床の高さ）。</summary>
        public int BaseCellY;

        /// <summary>積もらせたセルYの範囲（表示範囲の確認用）。面が無ければ (0, 0)。</summary>
        public int MinCellY;
        public int MaxCellY;

        public string HistogramText()
        {
            if (LayerHistogram == null || LayerHistogram.Length == 0)
            {
                return "なし";
            }
            var parts = new string[LayerHistogram.Length];
            for (int i = 0; i < LayerHistogram.Length; i++)
            {
                parts[i] = $"{i + 1}層={LayerHistogram[i]}";
            }
            return string.Join(" ", parts);
        }

        public string BiomeText()
        {
            if (BiomeHistogram == null || BiomeHistogram.Length < 3)
            {
                return "なし";
            }
            return $"平原={BiomeHistogram[0]} 丘陵={BiomeHistogram[1]} 山岳={BiomeHistogram[2]}";
        }
    }

    /// <summary>
    /// 雪積もり合成 (Demo 4.5 G3)。UnityEngine 非依存。
    ///
    /// 観測された各セルの**最上面にのみ**薄い起伏地形を積もらせる。
    /// これは prereg demo45 の論点1 決定 (d)「表面場」に対応する:
    /// 場は各 (x,z) の最上面に付随し、机の下の床面は場を持たない。
    /// 2面目以降（机の下の床など）は診断表示の対象ではあるが、積もらせない。
    ///
    /// 【M4 の保証】
    /// 入力は <see cref="SurfaceHit.cellY"/>（整数）と (x, z)、シードのみ。
    /// <see cref="SurfaceHit.worldY"/> は**読まない** — 読むとリプレイ経路に
    /// 浮動小数点の幾何演算の結果が混入し、bit-exact 保証が壊れる。
    /// </summary>
    public static class SnowfallComposer
    {
        /// <summary>
        /// 高さFbmのコントラスト正規化レンジ。Fbm の実測分布は [0.13, 0.86]・平均0.52 に
        /// 集中するため、実用レンジ [0.30, 0.75] を [0,1] へ再写像する
        /// （TerrainGenerator と同じ扱い）。
        /// </summary>
        const float k_FbmLow = 0.30f;
        const float k_FbmHigh = 0.75f;

        /// <summary>この層数以上の柱は表層を Stone にする（起伏の視認性）。</summary>
        const int k_StoneTopLayers = 4;

        public static SnowfallResult Compose(RoomObservation observation, SnowfallParams p)
        {
            if (observation == null)
            {
                throw new System.ArgumentNullException(nameof(observation));
            }
            if (p.minLayers < 1) p.minLayers = 1;
            if (p.maxLayers < p.minLayers) p.maxLayers = p.minLayers;
            if (p.reliefScale <= 0f) p.reliefScale = 1f;
            if (p.wallLayers < 1) p.wallLayers = 1;

            var grid = new VoxelGrid();
            var noise = new ValueNoise(p.seed);
            var histogram = new int[p.maxLayers];
            var biomes = new int[3];

            int baseCellY = FindBaseCellY(observation);

            int surfaces = 0;
            int blocks = 0;
            int wallCells = 0;
            int minCellY = int.MaxValue;
            int maxCellY = int.MinValue;

            for (int z = 0; z < observation.Depth; z++)
            {
                for (int x = 0; x < observation.Width; x++)
                {
                    int count = observation.GetHitCount(x, z);
                    if (count == 0)
                    {
                        continue;
                    }

                    // 表面場: 最上面のみ（リストは cellY 昇順なので末尾が最上面）
                    var top = observation.GetHit(x, z, count - 1);

                    // G4: 壁セルは積もり面ではなく通行不可の壁として積む。
                    // 徘徊AIは「高低差2以上は移動しない」ので、周囲より確実に高くすれば
                    // 追加のルール無しに壁を避ける（Simulation.TryMove）。
                    if (observation.IsBlocked(x, z))
                    {
                        for (int i = 1; i <= p.wallLayers; i++)
                        {
                            int wy = top.cellY + i;
                            grid.SetBlock(new Int3(x, wy, z), BlockId.Stone, BlockOrigin.Reality);
                            blocks++;
                            wallCells++;
                            if (wy < minCellY) minCellY = wy;
                            if (wy > maxCellY) maxCellY = wy;
                        }
                        continue;
                    }

                    var biome = ClassifyBiome(p, top, baseCellY);
                    int layers = ComputeLayers(p, noise, x, z, biome);

                    surfaces++;
                    histogram[layers - 1]++;
                    biomes[(int)biome]++;

                    for (int i = 1; i <= layers; i++)
                    {
                        int y = top.cellY + i;
                        BlockId id;
                        if (i == layers)
                        {
                            id = layers >= k_StoneTopLayers ? BlockId.Stone : BlockId.Grass;
                        }
                        else
                        {
                            id = BlockId.Dirt;
                        }
                        grid.SetBlock(new Int3(x, y, z), id, BlockOrigin.Terrain);
                        blocks++;

                        if (y < minCellY) minCellY = y;
                        if (y > maxCellY) maxCellY = y;
                    }
                }
            }

            return new SnowfallResult
            {
                Grid = grid,
                SurfaceCount = surfaces,
                BlockCount = blocks,
                LayerHistogram = histogram,
                BiomeHistogram = biomes,
                WallCellCount = wallCells,
                BaseCellY = baseCellY,
                MinCellY = blocks > 0 ? minCellY : 0,
                MaxCellY = blocks > 0 ? maxCellY : 0,
            };
        }

        /// <summary>
        /// バイオーム判定の基準になる床の高さ（観測内の最低セルY）。
        /// 整数のみから決まるので M4 の保証を壊さない。面が無ければ 0。
        /// </summary>
        public static int FindBaseCellY(RoomObservation observation)
        {
            int min = int.MaxValue;
            for (int z = 0; z < observation.Depth; z++)
            {
                for (int x = 0; x < observation.Width; x++)
                {
                    int count = observation.GetHitCount(x, z);
                    if (count == 0)
                    {
                        continue;
                    }
                    // リストは cellY 昇順。先頭が最下面
                    int y = observation.GetHit(x, z, 0).cellY;
                    if (y < min) min = y;
                }
            }
            return min == int.MaxValue ? 0 : min;
        }

        /// <summary>
        /// 積もり面のバイオーム (G5)。
        /// Floor→平原 / Table→丘陵 / Other・Unknown→高さヒューリスティック
        /// （床から mountainCellHeight 以上なら山岳、未満なら丘陵）。
        /// Ceiling・WallFace は積もり面にならない（G2 で除外済み）ため到達しない。
        /// </summary>
        public static SurfaceBiome ClassifyBiome(SnowfallParams p, SurfaceHit hit, int baseCellY)
        {
            switch (hit.label)
            {
                case SurfaceLabel.Floor:
                    return SurfaceBiome.Plains;
                case SurfaceLabel.Table:
                case SurfaceLabel.Couch:
                    return SurfaceBiome.Hills;
                default:
                    // Other / Unknown: 平面化されない小物・家具・荷物の上面。
                    // 実機目視で確認済み（prereg G5 の変更を参照）
                    return (hit.cellY - baseCellY) >= p.mountainCellHeight
                        ? SurfaceBiome.Mountains
                        : SurfaceBiome.Hills;
            }
        }

        /// <summary>バイオーム別の起伏振幅 (0..1)。TerrainGenerator の amplitude と同じ意味論。</summary>
        public static float GetAmplitude(SnowfallParams p, SurfaceBiome biome)
        {
            switch (biome)
            {
                case SurfaceBiome.Plains: return p.plainsAmplitude;
                case SurfaceBiome.Mountains: return p.mountainAmplitude;
                default: return p.hillsAmplitude;
            }
        }

        /// <summary>
        /// セル (x,z) の積もり層数 (minLayers..maxLayers)。整数座標とバイオームのみから決まる。
        /// 起伏ノイズは全域共通で、バイオームは**振幅**だけを変える
        /// （TerrainGenerator と同じ作り。バイオーム境界で地形が不連続にならない）。
        /// </summary>
        public static int ComputeLayers(SnowfallParams p, ValueNoise noise, int x, int z, SurfaceBiome biome)
        {
            float n = noise.Fbm(x / p.reliefScale, z / p.reliefScale, 2);
            float shaped = SmoothStep01((n - k_FbmLow) / (k_FbmHigh - k_FbmLow));

            int span = p.maxLayers - p.minLayers + 1;
            int layers = p.minLayers + (int)(shaped * GetAmplitude(p, biome) * span);
            if (layers < p.minLayers) layers = p.minLayers;
            if (layers > p.maxLayers) layers = p.maxLayers;
            return layers;
        }

        static float SmoothStep01(float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return t * t * (3f - 2f * t);
        }
    }
}
