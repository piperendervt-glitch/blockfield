using System;
using System.Collections.Generic;

namespace BlockField.SimCore.Excitable
{
    /// <summary>
    /// 興奮性媒質の汎用グラフ (jelly_1 J1)。
    ///
    /// 【なぜグラフか】リングは「近傍＝左右2セル」の特殊例にすぎない。
    /// 2次元シート（Pallasdies 型の傘の網）、鎖（魚の CPG 振動子列）、
    /// 局所叢（蟹の脚ごとの神経叢）は、いずれも**近傍リストが違うだけ**の
    /// 同一クラスの別インスタンスとして持てる。座標を持たないので、
    /// 生態系の表面場（<c>IField</c>）とは別系統である
    /// （あちらは <c>Deposit(Int3)</c> と <c>SimParams</c> に依存する）。
    ///
    /// 【状態は E・R・A の3つ】
    /// - E: 興奮度。発火すると <see cref="ExcitableParams.ExcitedLevel"/> になる
    /// - R: 不応期の残り。0 より大きい間は興奮できない
    /// - A: **振幅**。波が運ぶ状態であって、刺激からの幾何距離ではない（prereg 修正4）。
    ///   発火セルは「発火した近傍の A の最大値 × g」を受け取り、刺激は A=1 を注入する。
    ///   ホップ数 <c>g^hops</c> を毎回計算する旧モデルは、単一刺激では正しく見えるが
    ///   多重刺激で破綻する（158.5° 対 225.0°）。**距離を計算するコードを書かないこと。**
    ///
    /// 【同期更新（ダブルバッファ）は必須】(prereg 修正1)
    /// インプレースの逐次更新は +index 方向への瞬時伝播という非対称を生む。
    /// セル i を更新した直後にセル i+1 がその結果を読むため、1ステップで
    /// 複数セルを波が進んでしまう。本実装は**旧バッファのみを読み、新バッファへ書く**。
    /// 近傍リストの走査順にも依存しない（入力は加算、振幅は最大値で、
    /// どちらも順序に依存しない。テストで反転して固定してある）。
    /// </summary>
    public sealed class ExcitableField
    {
        readonly int[][] m_Neighbors;

        double[] m_E;
        int[] m_R;
        double[] m_A;

        double[] m_NextE;
        int[] m_NextR;
        double[] m_NextA;

        readonly List<int> m_Fired = new List<int>();

        /// <summary>セル数。</summary>
        public int CellCount => m_E.Length;

        /// <summary>
        /// 直近の <see cref="Step"/> で発火したセルの添字（昇順）。
        ///
        /// プロトタイプとの照合はこの列で行う（prereg 追記3）。
        /// ハッシュの一致より優れている点は、**ズレたときにどこでズレたかが分かる**こと。
        /// </summary>
        public IReadOnlyList<int> LastFired => m_Fired;

        /// <summary>これまでに進めたステップ数。刺激だけを与えた時点では 0。</summary>
        public long StepCount { get; private set; }

        /// <param name="neighbors">
        /// セルごとの近傍リスト。<c>neighbors[i]</c> が セル i の近傍の添字。
        /// 双方向である必要はない（片方向伝導も表現できる）。
        /// </param>
        public ExcitableField(int[][] neighbors)
        {
            if (neighbors == null)
            {
                throw new ArgumentNullException(nameof(neighbors));
            }
            int n = neighbors.Length;
            if (n <= 0)
            {
                throw new ArgumentException("セル数は1以上でなければならない", nameof(neighbors));
            }
            for (int i = 0; i < n; i++)
            {
                if (neighbors[i] == null)
                {
                    throw new ArgumentException($"セル {i} の近傍リストが null", nameof(neighbors));
                }
                foreach (int j in neighbors[i])
                {
                    if ((uint)j >= (uint)n)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(neighbors), $"セル {i} の近傍 {j} が範囲外（セル数 {n}）");
                    }
                }
            }

            m_Neighbors = neighbors;
            m_E = new double[n];
            m_R = new int[n];
            m_A = new double[n];
            m_NextE = new double[n];
            m_NextR = new int[n];
            m_NextA = new double[n];
        }

        public double Excitation(int cell) => m_E[cell];

        public int Refractory(int cell) => m_R[cell];

        public double Amplitude(int cell) => m_A[cell];

        /// <summary>
        /// セルへ刺激を注入する。興奮度を最大に、振幅を 1 に、不応期を R₀ に置く。
        /// プロトタイプの <c>E[s]=1.0; A[s]=1.0; R[s]=r0</c> と同じ。
        /// </summary>
        public void Stimulate(int cell, ExcitableParams p)
        {
            m_E[cell] = p.ExcitedLevel;
            m_A[cell] = 1.0;
            m_R[cell] = p.RefractoryTicks;
        }

        /// <summary>不応期だけを直接置く（一方向波の作成に使う。M-J1b の後方遮断）。</summary>
        public void SetRefractory(int cell, int ticks) => m_R[cell] = ticks;

        /// <summary>
        /// 1ステップ進める。旧バッファのみを読み、新バッファへ書く。
        /// </summary>
        public void Step(ExcitableParams p)
        {
            int n = m_E.Length;
            m_Fired.Clear();

            for (int i = 0; i < n; i++)
            {
                if (m_R[i] > 0)
                {
                    // 不応期中は興奮できない。E も A も 0 に落ちる
                    m_NextR[i] = m_R[i] - 1;
                    m_NextE[i] = 0.0;
                    m_NextA[i] = 0.0;
                    continue;
                }

                double input = 0.0;
                double sourceAmplitude = 0.0;
                var neighbors = m_Neighbors[i];
                for (int ni = 0; ni < neighbors.Length; ni++)
                {
                    int j = neighbors[ni];
                    if (m_E[j] >= p.ExcitedLevel)   // 近傍が直前のステップで発火した
                    {
                        input += p.Coupling;
                        if (m_A[j] > sourceAmplitude)
                        {
                            sourceAmplitude = m_A[j];
                        }
                    }
                }

                double e = m_E[i] * p.Decay + input;
                if (e >= p.Threshold)
                {
                    m_NextE[i] = p.ExcitedLevel;
                    m_NextR[i] = p.RefractoryTicks;
                    m_NextA[i] = sourceAmplitude * p.Attenuation;
                    m_Fired.Add(i);
                }
                else
                {
                    m_NextE[i] = e;
                    m_NextR[i] = 0;
                    m_NextA[i] = 0.0;
                }
            }

            Swap(ref m_E, ref m_NextE);
            Swap(ref m_R, ref m_NextR);
            Swap(ref m_A, ref m_NextA);
            StepCount++;
        }

        static void Swap<T>(ref T a, ref T b)
        {
            var t = a; a = b; b = t;
        }

        /// <summary>
        /// 波が消えたか。プロトタイプの終了判定
        /// 「発火なし かつ 全 E &lt; epsilon かつ 全 R == 0」と同じ。
        ///
        /// **全 R == 0 まで待つ**ので、最後の発火から R₀ ステップ後になる。
        /// 対蹠での対消滅は R₀ に依らず t=8（伝播は1ステップ1セル）だが、
        /// 「消滅」時刻は 8 + R₀ である（prereg 追記3）。
        /// </summary>
        public bool IsQuiescent(double epsilon = 0.01)
        {
            if (m_Fired.Count > 0)
            {
                return false;
            }
            for (int i = 0; i < m_E.Length; i++)
            {
                if (m_E[i] >= epsilon || m_R[i] != 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 決定論の検証用ハッシュ (FNV-1a 64bit)。World.ComputeContentHash と同じ方式で、
        /// E・R・A を添字順に畳み込む。
        ///
        /// **Python プロトタイプのハッシュ（<c>8328cc8b66b40f71</c>）とは一致しない。**
        /// あちらは Python のリスト repr の SHA-256 であり、
        /// 文字列表現を再現しても移植の正しさの証明にはならない。
        /// 移植の照合は <see cref="LastFired"/> の列で行う（prereg 追記3）。
        /// </summary>
        public ulong ComputeContentHash()
        {
            const ulong prime = 1099511628211UL;
            ulong hash = 14695981039346656037UL;

            unchecked
            {
                for (int i = 0; i < m_E.Length; i++)
                {
                    hash = FoldUInt64(hash, (ulong)BitConverter.DoubleToInt64Bits(m_E[i]), prime);
                    hash = FoldUInt64(hash, (ulong)BitConverter.DoubleToInt64Bits(m_A[i]), prime);
                    hash = FoldUInt64(hash, (uint)m_R[i], prime);
                }
            }
            return hash;
        }

        static ulong FoldUInt64(ulong hash, ulong value, ulong prime)
        {
            unchecked
            {
                for (int b = 0; b < 8; b++)
                {
                    hash = (hash ^ ((value >> (b * 8)) & 0xFF)) * prime;
                }
            }
            return hash;
        }
    }
}
