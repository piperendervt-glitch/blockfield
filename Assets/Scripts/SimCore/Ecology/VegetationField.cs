using System;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 植生場 (Demo 3 E1)。Field を利用した動的な場（τ小＝速い層）。
    /// 毎ティック: 植物存在セルへの書き込み（Simulation 側）→ Diffuse → Decay。
    /// 「場が繁殖の主体」— 植物スポーン確率はこの場から読み出される。
    /// </summary>
    public sealed class VegetationField
    {
        public Field Values { get; }

        readonly float[] m_Scratch;

        public VegetationField(int width, int depth)
        {
            Values = new Field(width, depth);
            m_Scratch = new float[width * depth];
        }

        /// <summary>植物存在セルへの書き込み（上限 1.0）。</summary>
        public void Deposit(int x, int z, float amount)
        {
            float v = Values.Get(x, z) + amount;
            Values.Set(x, z, v > 1f ? 1f : v);
        }

        /// <summary>拡散＋減衰。拡散は4近傍平均への lerp（ダブルバッファで順序非依存）。</summary>
        public void Update(float diffuseRate, float decayRate)
        {
            int width = Values.Width;
            int depth = Values.Depth;

            // 拡散: scratch に新値を書いてから書き戻す
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    if (x + 1 < width) { sum += Values.Get(x + 1, z); count++; }
                    if (x - 1 >= 0) { sum += Values.Get(x - 1, z); count++; }
                    if (z + 1 < depth) { sum += Values.Get(x, z + 1); count++; }
                    if (z - 1 >= 0) { sum += Values.Get(x, z - 1); count++; }

                    float v = Values.Get(x, z);
                    float avg = count > 0 ? sum / count : v;
                    m_Scratch[x + width * z] = v + (avg - v) * diffuseRate;
                }
            }

            // 減衰しつつ書き戻し
            float keep = 1f - decayRate;
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    Values.Set(x, z, m_Scratch[x + width * z] * keep);
                }
            }
        }
    }
}
