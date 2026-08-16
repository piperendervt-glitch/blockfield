using System;
using BlockField.SimCore.Excitable;

namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// 水槽に浮かべるクラゲ1体 (系列2 Phase C-7/8)。
    ///
    /// 神経は jelly_1 で実装済みの <see cref="ExcitableField"/> のリングをそのまま使う
    /// （タグ `jelly-1.1`。M-J1a〜M-J3c 全判定合格）。
    ///
    /// 【推力は 2D リム収縮のまま】Phase C-8 の文面は「傘の囲む体積の dV/dt から推力」
    /// だが、初回は jelly_1 と同じ 2D リム収縮で実機へ出す。理由:
    /// - 目的は「生きて見えるか」の早期確認で、それには足りる
    /// - dV/dt を入れると**3Dの姿勢（傘の軸）という新しい状態が増え**、
    ///   実機で問題が出たときの切り分けが困難になる
    ///   （Phase B の「原因でない修正を同時に入れない」と同じ判断）
    /// - 抗力係数の逆算（`jelly_side.html` の内部量から）が済んでいないので、
    ///   dV/dt を入れても係数は暫定値のまま。**逆算を先にやる**のが順序として正しい
    ///
    /// 【限界（この段の既知の制約）】リム収縮の推力はリング平面内にしか出ない。
    /// リング平面は水平に固定しているので、**自力で泳ぐのは水平方向だけ**である。
    /// 鉛直方向の動きは流れに運ばれる分しかない。
    /// 次段の dV/dt モデル（推力の大きさは体積変化、向きは傘の軸、
    /// 旋回は収縮の非対称から）でこの制約が外れる。
    ///
    /// 【クラゲは真実側】粒子と違い、位置が力学の状態である。
    /// 決定論の対象に入れ、固定ティックで進める。
    /// </summary>
    public sealed class Jellyfish
    {
        readonly JellyParams m_Params;
        readonly ExcitableField m_Ring;
        readonly float[] m_Cos;
        readonly float[] m_Sin;
        readonly float m_SpeedScale;

        // モデル単位の速度（リング平面 = 水平面）。m/s へは m_SpeedScale で換算する
        float m_ModelVx, m_ModelVz;

        /// <summary>位置 (m、部屋座標)。</summary>
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Z { get; private set; }

        /// <summary>自力遊泳の速度 (m/s)。水平のみ（上の限界を参照）。</summary>
        public float SwimVx => m_ModelVx * m_SpeedScale;
        public float SwimVz => m_ModelVz * m_SpeedScale;

        /// <summary>
        /// **その瞬間の**自力遊泳の速さ (m/s)。
        ///
        /// 【平均値として読んではいけない】推力は発火したステップにしか出ず、
        /// あとは抗力で減衰するので、この値は 1 拍動のあいだに 0.19 から 0.0004 まで振れる。
        /// 2026-08-16 の実機ログは 1 秒間隔（= 拍動周期そのもの）でこれを出していたため、
        /// **常に減衰しきった位相で標本化**され、0.0007 m/s と出ていた。目標は 0.040 m/s。
        /// 平均を見たいときは <see cref="SwimPathX"/> の差分を使うこと。
        /// </summary>
        public float SwimSpeed => (float)Math.Sqrt(SwimVx * SwimVx + SwimVz * SwimVz);

        /// <summary>
        /// 自力遊泳ぶんだけを積分した変位 (m)。流れに運ばれたぶんは含まない。
        /// 2点の差を経過時間で割れば、その区間の平均遊泳速度になる
        /// （<see cref="CalibrateSpeedScale"/> が目標へ合わせているのと同じ統計量）。
        /// </summary>
        public float SwimPathX { get; private set; }
        public float SwimPathZ { get; private set; }

        /// <summary>流れに運ばれたぶんだけを積分した変位 (m)。鉛直はこちらにしか出ない。</summary>
        public float DriftPathX { get; private set; }
        public float DriftPathY { get; private set; }
        public float DriftPathZ { get; private set; }

        /// <summary>これまでの拍動回数（ペースメーカーが実際に発火した数）。</summary>
        public long PulseCount { get; private set; }

        /// <summary>神経ステップ数。</summary>
        public long StepCount { get; private set; }

        public ExcitableField Ring => m_Ring;
        public float BellDiameter => m_Params.BellDiameter;

        /// <summary>1拍動のステップ数。平均を取る窓の長さに使う。</summary>
        public int PulsePeriodTicks => m_Params.PulsePeriodTicks;

        /// <summary>モデル速度を m/s へ換算する係数（診断用）。</summary>
        public float SpeedScale => m_SpeedScale;

        public Jellyfish(JellyParams p, float x, float y, float z)
        {
            m_Params = p;
            m_Ring = new ExcitableField(ExcitableGraphs.Ring(p.RingCells));

            m_Cos = new float[p.RingCells];
            m_Sin = new float[p.RingCells];
            for (int i = 0; i < p.RingCells; i++)
            {
                double a = 2.0 * Math.PI * i / p.RingCells;
                m_Cos[i] = (float)Math.Cos(a);
                m_Sin[i] = (float)Math.Sin(a);
            }

            X = x; Y = y; Z = z;
            m_SpeedScale = CalibrateSpeedScale(p);
        }

        /// <summary>
        /// モデルの持続遊泳速度を測り、目標速度へ合わせる係数を出す。
        ///
        /// jelly_1 の J3a と同じ手順（過渡を外した区間の平均速度）を、
        /// 実際のパラメータで走らせて測る。解析式で出さないのは
        /// パラメータ（g・抗力・周期）を変えても追従させるため。
        /// 同じパラメータなら同じ値が出るので決定論は保たれる。
        /// </summary>
        static float CalibrateSpeedScale(JellyParams p)
        {
            var ring = new ExcitableField(ExcitableGraphs.Ring(p.RingCells));
            var cos = new float[p.RingCells];
            var sin = new float[p.RingCells];
            for (int i = 0; i < p.RingCells; i++)
            {
                double a = 2.0 * Math.PI * i / p.RingCells;
                cos[i] = (float)Math.Cos(a);
                sin[i] = (float)Math.Sin(a);
            }

            float vx = 0f, vz = 0f, x = 0f, z = 0f;
            float x800 = 0f, z800 = 0f;
            const int total = 1600;

            for (int t = 0; t < total; t++)
            {
                if (t % p.PulsePeriodTicks == 0 && ring.Refractory(p.PacemakerCell) == 0)
                {
                    ring.Stimulate(p.PacemakerCell, p.Excitable);
                }
                ring.Step(p.Excitable);

                var fired = ring.LastFired;
                for (int f = 0; f < fired.Count; f++)
                {
                    int i = fired[f];
                    float amp = (float)ring.Amplitude(i);
                    vx -= amp * cos[i];
                    vz -= amp * sin[i];
                }
                vx *= (1f - p.Drag); vz *= (1f - p.Drag);
                x += vx; z += vz;
                if (t == 799) { x800 = x; z800 = z; }
            }

            // 過渡を外した区間 800〜1600 の平均速度（モデル単位/ティック）
            float dx = x - x800, dz = z - z800;
            float sustained = (float)Math.Sqrt(dx * dx + dz * dz) / (total - 800);
            return sustained > 1e-9f ? p.SwimSpeed / sustained : 0f;
        }

        /// <summary>
        /// 神経1ステップぶん進める。
        /// </summary>
        /// <param name="dtSeconds">1ステップの実時間（神経 40Hz なら 1/40）。</param>
        /// <param name="flowVx">その位置の流速 (m/s)。クラゲは流れに書き戻さない。</param>
        public void Step(float dtSeconds, float flowVx, float flowVy, float flowVz)
        {
            if (StepCount % m_Params.PulsePeriodTicks == 0
                && m_Ring.Refractory(m_Params.PacemakerCell) == 0)
            {
                m_Ring.Stimulate(m_Params.PacemakerCell, m_Params.Excitable);
                PulseCount++;
            }

            m_Ring.Step(m_Params.Excitable);

            // 収縮したセルは自分の側と逆向きに体を押す（jelly_1 と同じ局所則）
            var fired = m_Ring.LastFired;
            for (int f = 0; f < fired.Count; f++)
            {
                int i = fired[f];
                float amp = (float)m_Ring.Amplitude(i);
                m_ModelVx -= amp * m_Cos[i];
                m_ModelVz -= amp * m_Sin[i];
            }
            m_ModelVx *= (1f - m_Params.Drag);
            m_ModelVz *= (1f - m_Params.Drag);

            // 流れが運ぶ。自力遊泳は水平のみ（リング平面が水平に固定されているため）
            X += (SwimVx + flowVx) * dtSeconds;
            Y += flowVy * dtSeconds;
            Z += (SwimVz + flowVz) * dtSeconds;

            // 診断用の内訳。位置の更新式はそのまま（丸めの経路を変えないため）
            SwimPathX += SwimVx * dtSeconds;
            SwimPathZ += SwimVz * dtSeconds;
            DriftPathX += flowVx * dtSeconds;
            DriftPathY += flowVy * dtSeconds;
            DriftPathZ += flowVz * dtSeconds;

            StepCount++;
        }

        /// <summary>
        /// 位置を直接置く。**壁へのめり込みを戻す用**であり、力学の一部ではない。
        /// 速度は変えないので、押し戻しても泳ぎ方は変わらない。
        /// </summary>
        public void Teleport(float x, float y, float z)
        {
            X = x; Y = y; Z = z;
        }

        /// <summary>
        /// リムの収縮の度合い（0 = 弛緩、1 = 最大収縮）。傘の描画に使う。
        /// 不応期の残りを 0〜1 に写したもので、発火直後が最も縮んでいる。
        /// </summary>
        public float Contraction(int cell)
        {
            int r0 = m_Params.Excitable.RefractoryTicks;
            if (r0 <= 0) return 0f;
            return m_Ring.Refractory(cell) / (float)r0;
        }

        /// <summary>決定論の検証用ハッシュ。神経の状態と位置・速度を畳み込む。</summary>
        public ulong ComputeContentHash()
        {
            const ulong prime = 1099511628211UL;
            ulong hash = m_Ring.ComputeContentHash();
            unchecked
            {
                foreach (float v in new[] { X, Y, Z, m_ModelVx, m_ModelVz })
                {
                    uint bits = (uint)BitConverter.SingleToInt32Bits(v);
                    hash = (hash ^ (bits & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 8) & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 16) & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 24) & 0xFF)) * prime;
                }
                hash = (hash ^ (ulong)StepCount) * prime;
            }
            return hash;
        }
    }
}
