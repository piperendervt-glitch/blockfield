namespace BlockField.SimCore.Watch
{
    /// <summary>
    /// L0 の固定ティック。**20Hz 固定。フレーム駆動にしない。**
    ///
    /// 【なぜフレーム駆動にしないか】フレームレートは端末の負荷で揺れる。
    /// 記録の時間軸がフレームに乗ると、**同じログから同じ絵が再生できない**。
    /// 決定論 f(シード, イベントログ) を L0 の側で壊さないため、
    /// 実時間を蓄えて固定ステップで刻む。
    ///
    /// 【遅延を測る】1フレームで消化したティック数と積み残しを出す。
    /// 層を1シーンに並べる判断（分割するかどうか）は**体感ではなく遅延で決める**。
    /// </summary>
    public sealed class L0Ticker
    {
        public const int HzDefault = 20;

        /// <summary>1フレームで消化する上限。長い停止のあとに走り出さないための蓋。</summary>
        public const int MaxStepsPerFrame = 8;

        public int Hz { get; }
        public float StepSeconds => 1f / Hz;

        /// <summary>これまでに刻んだティック数。レコードの時刻になる。</summary>
        public int Tick { get; private set; }

        /// <summary>直近のフレームで消化したティック数。</summary>
        public int StepsLastFrame { get; private set; }

        /// <summary>積み残しの時間 (s)。**これがティック遅延**。</summary>
        public float Backlog => m_Accumulator;

        /// <summary>上限で切り捨てたティック数の累計。0 でないなら 20Hz を維持できていない。</summary>
        public int DroppedTicks { get; private set; }

        float m_Accumulator;

        public L0Ticker(int hz = HzDefault)
        {
            Hz = hz > 0 ? hz : HzDefault;
        }

        /// <summary>
        /// 実時間を進め、刻むべきティック数を返す。呼び出し側はその回数だけ
        /// プロデューサを読んで場へ取り込む。
        /// </summary>
        public int Advance(float deltaSeconds)
        {
            m_Accumulator += deltaSeconds;
            int steps = 0;
            while (m_Accumulator >= StepSeconds && steps < MaxStepsPerFrame)
            {
                m_Accumulator -= StepSeconds;
                steps++;
            }

            // 上限に当たった分は捨てる。捨てた事実を数えておかないと、
            // 「20Hz を維持できていない」ことに気づけない
            if (m_Accumulator >= StepSeconds)
            {
                int dropped = (int)(m_Accumulator / StepSeconds);
                DroppedTicks += dropped;
                m_Accumulator -= dropped * StepSeconds;
            }

            Tick += steps;
            StepsLastFrame = steps;
            return steps;
        }
    }
}
