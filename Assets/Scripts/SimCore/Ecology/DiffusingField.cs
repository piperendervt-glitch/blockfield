using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 拡散と減衰を持つ動的な場の共通実装 (Demo 8 で植生場から抽出)。
    ///
    /// 「痕跡が置かれ、にじみ、時間とともに薄れる」という振る舞いは
    /// 植生場・恐怖場・獲物場に共通なので、拡散ループをここに集約する。
    /// 違うのは deposit 量と拡散率・減衰率＝**τ（時定数）だけ**であり、
    /// τ の違いがそのまま「その情報がどれくらい長く価値を持つか」を表す。
    /// </summary>
    public abstract class DiffusingField : ScalarField
    {
        readonly float[] m_Scratch;

        protected DiffusingField(string name, int width, int depth)
            : base(name, width, depth)
        {
            m_Scratch = new float[width * depth];
        }

        /// <summary>書き込み（上限 1.0 で飽和）。痕跡は無限には濃くならない。</summary>
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
        protected void UpdateDiffusion(float diffuseRate, float decayRate)
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
