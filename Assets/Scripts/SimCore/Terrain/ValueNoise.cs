using BlockField.SimCore.Rng;

namespace BlockField.SimCore.Terrain
{
    /// <summary>
    /// Mulberry32 ベースの2D値ノイズ。格子点乱数＋スムーズ補間＋オクターブ合成。
    /// シードから完全決定論（同一シード・同一座標→同一値）。
    /// </summary>
    public sealed class ValueNoise
    {
        readonly uint m_Seed;

        public ValueNoise(uint seed)
        {
            m_Seed = seed;
        }

        /// <summary>1オクターブの値ノイズ。値域 [0, 1)。</summary>
        public float Sample(float x, float z)
        {
            int x0 = FloorToInt(x);
            int z0 = FloorToInt(z);
            float tx = SmoothStep01(x - x0);
            float tz = SmoothStep01(z - z0);

            float v00 = LatticeValue(x0, z0);
            float v10 = LatticeValue(x0 + 1, z0);
            float v01 = LatticeValue(x0, z0 + 1);
            float v11 = LatticeValue(x0 + 1, z0 + 1);

            float a = v00 + (v10 - v00) * tx;
            float b = v01 + (v11 - v01) * tx;
            return a + (b - a) * tz;
        }

        /// <summary>オクターブ合成 (persistence 0.5)。値域 [0, 1)。</summary>
        public float Fbm(float x, float z, int octaves)
        {
            float sum = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float totalAmplitude = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Sample(x * frequency, z * frequency) * amplitude;
                totalAmplitude += amplitude;
                amplitude *= 0.5f;
                frequency *= 2f;
            }

            return sum / totalAmplitude;
        }

        /// <summary>格子点 (xi, zi) の決定論的乱数値 [0, 1)。</summary>
        float LatticeValue(int xi, int zi)
        {
            unchecked
            {
                // 座標を素数系の定数で撹拌してシードに畳み込み、Mulberry32 で1値取り出す
                uint s = m_Seed;
                s ^= (uint)xi * 0x8DA6B343u;
                s ^= (uint)zi * 0xD8163841u;
                var rng = new Mulberry32(s);
                return rng.NextFloat01();
            }
        }

        static int FloorToInt(float v)
        {
            int i = (int)v;
            return (v < i) ? i - 1 : i;
        }

        static float SmoothStep01(float t)
        {
            return t * t * (3f - 2f * t);
        }
    }
}
