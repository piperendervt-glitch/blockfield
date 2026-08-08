using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Terrain
{
    /// <summary>
    /// ハイトマップ地形生成 (Demo 1 A2+A3)。
    /// 高さノイズ＋低周波バイオームマスク（平地/山）で VoxelGrid を生成する。
    /// UnityEngine 非依存・シードから完全決定論。
    /// </summary>
    public static class TerrainGenerator
    {
        /// <summary>バイオームマスク用シード派生の撹拌定数（黄金比の32bit表現）。</summary>
        const uint k_BiomeSeedSalt = 0x9E3779B9u;

        /// <summary>バイオームノイズは起伏ノイズより低周波にする倍率（100×100内に山塊1〜数個になる粒度）。</summary>
        const float k_BiomeScaleMultiplier = 2f;

        /// <summary>
        /// 高さFbmのコントラスト正規化レンジ。Fbm(4oct)の実測分布は [0.13, 0.86]・平均0.52 に
        /// 集中し1.0に届かないため、実用レンジ [0.30, 0.75] を [0,1] へ再写像する（n≥0.75 は
        /// 全セルの約7%存在するため、山バイオーム内でピークが maxHeight へ安定到達する）。
        /// </summary>
        const float k_FbmLow = 0.30f;
        const float k_FbmHigh = 0.75f;

        /// <summary>バイオームマスクの二極化レンジ（m&lt;下限=完全平地、m&gt;上限=完全山岳）。</summary>
        const float k_BiomeMaskLow = 0.5f;
        const float k_BiomeMaskHigh = 0.75f;

        /// <summary>
        /// この高さ比を超える柱の表層を Stone にする（雪山風の見た目変化）。
        /// 0.75 (上位25%) だと山の弱いシードで一度も発現しないことがあるため 0.7 (上位30%) に設定。
        /// </summary>
        const float k_StoneTopHeightRatio = 0.7f;

        public static VoxelGrid Generate(TerrainParams p)
        {
            var grid = new VoxelGrid();
            var heightNoise = new ValueNoise(p.seed);
            var biomeNoise = new ValueNoise(p.seed ^ k_BiomeSeedSalt);

            int stoneTopThreshold = (int)(p.maxHeight * k_StoneTopHeightRatio);

            for (int z = 0; z < p.depth; z++)
            {
                for (int x = 0; x < p.width; x++)
                {
                    int h = ComputeHeight(p, heightNoise, biomeNoise, x, z);

                    for (int y = 0; y < h; y++)
                    {
                        BlockId id;
                        if (y == h - 1)
                        {
                            // 表層: 高所 (上位25%) は Stone、それ以外は Grass
                            id = (h > stoneTopThreshold) ? BlockId.Stone : BlockId.Grass;
                        }
                        else if (y >= h - 3)
                        {
                            id = BlockId.Dirt; // 表層直下2層
                        }
                        else
                        {
                            id = BlockId.Stone;
                        }

                        grid.Set(new Int3(x, y, z), id);
                    }
                }
            }

            return grid;
        }

        /// <summary>柱の高さ (1..maxHeight)。バイオームマスクで起伏振幅を変調する。</summary>
        public static int ComputeHeight(TerrainParams p, ValueNoise heightNoise, ValueNoise biomeNoise, int x, int z)
        {
            float nx = x / p.reliefScale;
            float nz = z / p.reliefScale;

            // バイオームマスク: 低周波ノイズを [k_BiomeMaskLow, k_BiomeMaskHigh] で二極化
            // （分布が0.2〜0.9に薄く広がるため、単純なsmoothstepでは平地/山が分離しない）
            float m = biomeNoise.Fbm(x / (p.reliefScale * k_BiomeScaleMultiplier), z / (p.reliefScale * k_BiomeScaleMultiplier), 2);
            float mask = SmoothStep01((m - k_BiomeMaskLow) / (k_BiomeMaskHigh - k_BiomeMaskLow));
            float amplitude = Lerp(p.plainsAmplitude, p.mountainAmplitude, mask);

            // 高さノイズをコントラスト正規化してから振幅を適用（振幅=maxHeight比率のセマンティクスを実現）
            float n = heightNoise.Fbm(nx, nz, 4);
            float shaped = SmoothStep01((n - k_FbmLow) / (k_FbmHigh - k_FbmLow));
            float hNorm = shaped * amplitude; // [0, amplitude]
            int h = 1 + (int)(hNorm * (p.maxHeight - 1));

            // 数値誤差の保険としてクランプ（仕様: 1..maxHeight）
            if (h < 1) h = 1;
            if (h > p.maxHeight) h = p.maxHeight;
            return h;
        }

        static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        static float SmoothStep01(float t)
        {
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return t * t * (3f - 2f * t);
        }
    }
}
