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

        /// <summary>バイオームノイズは起伏ノイズより低周波にする倍率。</summary>
        const float k_BiomeScaleMultiplier = 4f;

        /// <summary>この高さ比を超える柱の表層を Stone にする（雪山風の見た目変化）。</summary>
        const float k_StoneTopHeightRatio = 0.75f;

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

            // バイオームマスク m∈[0,1): 低周波ノイズ→smoothstepで平地/山の遷移を急峻に
            float m = biomeNoise.Fbm(x / (p.reliefScale * k_BiomeScaleMultiplier), z / (p.reliefScale * k_BiomeScaleMultiplier), 2);
            float mask = SmoothStep01(m);
            float amplitude = Lerp(p.plainsAmplitude, p.mountainAmplitude, mask);

            float hNorm = heightNoise.Fbm(nx, nz, 4) * amplitude; // [0, amplitude)
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
