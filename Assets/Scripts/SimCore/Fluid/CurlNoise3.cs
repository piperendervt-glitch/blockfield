using System;

namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// 決定論的な3D値ノイズと fBm (系列2 Phase B)。
    ///
    /// 既存の <c>SimCore.Terrain.ValueNoise</c> は 2D（地形のハイトマップ用）で、
    /// 3D の流れ関数には使えない。整数格子のハッシュから作るので、
    /// 同じ座標からは常に同じ値が出る（テーブルも状態も持たない）。
    ///
    /// 【なぜ状態を持たないか】ψ は毎ティック全セルを走査して作り直すのではなく、
    /// **必要なセルだけを任意の順序で評価できる**必要がある
    /// （固定ティックで縞状に更新するため）。評価順に依存する実装だと
    /// 更新の分割方法で結果が変わってしまう。
    /// </summary>
    public static class CurlNoise3
    {
        /// <summary>整数格子点のハッシュ値 [0, 1)。</summary>
        public static float Hash(int x, int y, int z, uint seed)
        {
            unchecked
            {
                uint h = seed;
                h ^= (uint)x * 374761393u; h = (h << 13) | (h >> 19); h *= 1274126177u;
                h ^= (uint)y * 668265263u; h = (h << 11) | (h >> 21); h *= 2246822519u;
                h ^= (uint)z * 2166136261u; h = (h << 7) | (h >> 25); h *= 3266489917u;
                h ^= h >> 15;
                return (h & 0xFFFFFF) * (1f / 16777216f);
            }
        }

        /// <summary>三線形補間の値ノイズ [0, 1)。smoothstep で滑らかにする。</summary>
        public static float Sample(float x, float y, float z, uint seed)
        {
            int xi = FloorToInt(x), yi = FloorToInt(y), zi = FloorToInt(z);
            float fx = Smooth(x - xi), fy = Smooth(y - yi), fz = Smooth(z - zi);

            float c000 = Hash(xi, yi, zi, seed);
            float c100 = Hash(xi + 1, yi, zi, seed);
            float c010 = Hash(xi, yi + 1, zi, seed);
            float c110 = Hash(xi + 1, yi + 1, zi, seed);
            float c001 = Hash(xi, yi, zi + 1, seed);
            float c101 = Hash(xi + 1, yi, zi + 1, seed);
            float c011 = Hash(xi, yi + 1, zi + 1, seed);
            float c111 = Hash(xi + 1, yi + 1, zi + 1, seed);

            float x00 = c000 + (c100 - c000) * fx;
            float x10 = c010 + (c110 - c010) * fx;
            float x01 = c001 + (c101 - c001) * fx;
            float x11 = c011 + (c111 - c011) * fx;
            float y0 = x00 + (x10 - x00) * fy;
            float y1 = x01 + (x11 - x01) * fy;
            return y0 + (y1 - y0) * fz;
        }

        /// <summary>
        /// 多オクターブ（fBm）。値域は [-1, 1] へ寄せてある
        /// （ψ の成分として使うので符号が要る）。
        /// </summary>
        public static float Fbm(float x, float y, float z, uint seed, int octaves)
        {
            float sum = 0f, amplitude = 1f, frequency = 1f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += (Sample(x * frequency, y * frequency, z * frequency,
                               seed + (uint)o * 7919u) * 2f - 1f) * amplitude;
                norm += amplitude;
                amplitude *= 0.5f;
                frequency *= 2f;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        static int FloorToInt(float v)
        {
            int i = (int)v;
            return v < i ? i - 1 : i;
        }
    }
}
