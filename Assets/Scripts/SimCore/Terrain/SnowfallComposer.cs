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

        /// <summary>
        /// 既定値。層数 1〜4（薄い起伏）、起伏スケール 10セル = 0.4m。
        /// 机の天板（40cm 程度）に 2〜3 の起伏の山が乗る粒度。
        /// </summary>
        public static SnowfallParams Default => new SnowfallParams
        {
            seed = 12345u,
            minLayers = 1,
            maxLayers = 4,
            reliefScale = 10f,
        };
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

            var grid = new VoxelGrid();
            var noise = new ValueNoise(p.seed);
            var histogram = new int[p.maxLayers];

            int surfaces = 0;
            int blocks = 0;
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
                    int layers = ComputeLayers(p, noise, x, z);

                    surfaces++;
                    histogram[layers - 1]++;

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
                MinCellY = surfaces > 0 ? minCellY : 0,
                MaxCellY = surfaces > 0 ? maxCellY : 0,
            };
        }

        /// <summary>セル (x,z) の積もり層数 (minLayers..maxLayers)。整数座標のみから決まる。</summary>
        public static int ComputeLayers(SnowfallParams p, ValueNoise noise, int x, int z)
        {
            float n = noise.Fbm(x / p.reliefScale, z / p.reliefScale, 2);
            float shaped = SmoothStep01((n - k_FbmLow) / (k_FbmHigh - k_FbmLow));

            int span = p.maxLayers - p.minLayers + 1;
            int layers = p.minLayers + (int)(shaped * span);
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
