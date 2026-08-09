using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 植生場 (Demo 3 E1)。動的な表面場（τ小＝速い層）。
    /// 毎ティック: 植物存在セルへの書き込み（Simulation 側）→ 拡散 → 減衰。
    /// 「場が繁殖の主体」— 植物スポーン確率はこの場から読み出される。
    /// 意味論（表面場）は <see cref="IField"/> のコメントを参照。
    /// </summary>
    public sealed class VegetationField : ScalarField
    {
        /// <summary>World.Fields のキー。ContentHash の畳み込み順は名前昇順。</summary>
        public const string FieldName = "vegetation";

        readonly float[] m_Scratch;

        public VegetationField(int width, int depth)
            : base(FieldName, width, depth)
        {
            m_Scratch = new float[width * depth];
        }

        /// <summary>植物存在セルへの書き込み（上限 1.0）。</summary>
        public override void Deposit(Int3 cell, float amount)
        {
            base.Deposit(cell, amount);
            float v = GetAtColumn(cell.x, cell.z);
            if (v > 1f)
            {
                SetAtColumn(cell.x, cell.z, 1f);
            }
        }

        /// <summary>拡散＋減衰。拡散は4近傍平均への lerp（ダブルバッファで順序非依存）。</summary>
        public override void Update(SimParams p)
        {
            Update(p.vegetationDiffuse, p.vegetationDecay);
        }

        /// <summary>拡散率・減衰率を直接指定する版（テスト・調整用）。</summary>
        public void Update(float diffuseRate, float decayRate)
        {
            int width = Width;
            int depth = Depth;

            // 拡散: scratch に新値を書いてから書き戻す
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    if (x + 1 < width) { sum += GetAtColumn(x + 1, z); count++; }
                    if (x - 1 >= 0) { sum += GetAtColumn(x - 1, z); count++; }
                    if (z + 1 < depth) { sum += GetAtColumn(x, z + 1); count++; }
                    if (z - 1 >= 0) { sum += GetAtColumn(x, z - 1); count++; }

                    float v = GetAtColumn(x, z);
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
                    SetAtColumn(x, z, m_Scratch[x + width * z] * keep);
                }
            }
        }
    }
}
