using System;

namespace BlockField.SimCore.Excitable
{
    /// <summary>
    /// 神経環の収縮を推力に変える擬似流体の体 (jelly_1 J2)。
    ///
    /// 【格子流体を解かない】「重い物理は保存量の収支モデルに落とす」の適用。
    /// 各セルの発火を**推力インパルス**として与え、抗力は速度への一次減衰で表す。
    /// 浸漬境界法は捨てた（prereg §8）。PhysX も使わず、積分はここで行う。
    ///
    /// 【方向を計算するコードを持たない】これが J2 の中心的主張である。
    /// このクラスにあるのは:
    /// - <c>angle = 2π i / n</c> — セル i が体のどこに付いているかという**解剖学的な定数**。
    ///   刺激から算出した方向ではない
    /// - 発火セルが自分の位置と**逆向き**に体を押す、という局所則
    ///
    /// 刺激の方向を求める計算も、場の勾配の符号判定も、heading 変数も無い。
    /// 逃避方向は「伝播の時間差（近い側が先に発火する）＋ 振幅の減衰勾配
    /// （近い側が強く押す）」の合成として**創発する**。
    ///
    /// 到達方向の読み出し（atan2）は**このクラスに置いていない**。
    /// 位置 <see cref="X"/> / <see cref="Y"/> を外に出すだけにして、
    /// 角度は測定側で計算する。「方向を計算していない」という主張を
    /// grep で確かめられる形に保つため。
    /// </summary>
    public sealed class RingSwimmer
    {
        readonly ExcitableField m_Field;
        readonly int m_CellCount;
        readonly double[] m_Cos;
        readonly double[] m_Sin;

        double m_Vx, m_Vy;

        public RingSwimmer(int cellCount)
        {
            m_CellCount = cellCount;
            m_Field = new ExcitableField(ExcitableGraphs.Ring(cellCount));

            // セルの取り付け角は体の形であって、走行中に変わらない。
            // 毎ステップ三角関数を呼ばないよう先に展開しておく
            m_Cos = new double[cellCount];
            m_Sin = new double[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                double a = 2.0 * Math.PI * i / cellCount;
                m_Cos[i] = Math.Cos(a);
                m_Sin[i] = Math.Sin(a);
            }
        }

        public ExcitableField Field => m_Field;
        public int CellCount => m_CellCount;

        /// <summary>重心の位置。角度の読み出しは測定側で行う（クラス説明を参照）。</summary>
        public double X { get; private set; }
        public double Y { get; private set; }

        public double Vx => m_Vx;
        public double Vy => m_Vy;

        /// <summary>
        /// 刺激を入れる。**不応期中のセルには入らない**（プロトタイプの
        /// <c>if ts==t and R[c]==0</c> と同じ）。入ったかどうかを返す。
        /// </summary>
        public bool TryStimulate(int cell, ExcitableParams p)
        {
            if (m_Field.Refractory(cell) != 0)
            {
                return false;
            }
            m_Field.Stimulate(cell, p);
            return true;
        }

        /// <summary>
        /// 1ステップ進める。順序は プロトタイプ <c>swim()</c> と同じ:
        /// 場を1ステップ → 発火セルの推力を速度へ加算 → 抗力 → 位置へ積分。
        ///
        /// 推力は**発火セルの振幅 A** で決まる。刺激から遠いセルほど
        /// A が小さい（波が g を掛けながら運んでくる）ので、
        /// 近い側が強く押す。これが逃避の向きを生む勾配である。
        /// </summary>
        /// <param name="drag">速度への一次減衰係数。毎ステップ (1-drag) 倍になる。</param>
        public void Step(ExcitableParams p, double drag)
        {
            m_Field.Step(p);

            var fired = m_Field.LastFired;
            for (int f = 0; f < fired.Count; f++)
            {
                int i = fired[f];
                double amp = m_Field.Amplitude(i);
                // 収縮したセルは自分の側と**逆向き**に体を押す
                m_Vx -= amp * m_Cos[i];
                m_Vy -= amp * m_Sin[i];
            }

            m_Vx *= (1.0 - drag);
            m_Vy *= (1.0 - drag);
            X += m_Vx;
            Y += m_Vy;
        }
    }
}
