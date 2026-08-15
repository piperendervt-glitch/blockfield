namespace BlockField.SimCore.Excitable
{
    /// <summary>
    /// 興奮性媒質のパラメータ (jelly_1 J1)。
    ///
    /// 既定値は Python プロトタイプ（<c>docs/prototypes/jelly/excitable_ring.py</c>、
    /// <c>j2_wave_amplitude.py</c>）と揃えてある。ただし
    /// <see cref="RefractoryTicks"/> だけは **14**（プロトタイプの既定 4 は
    /// 訂正前の値。prereg §7.3-1 / 修正3）。
    ///
    /// 【なぜ double か】プロトタイプが Python の float（＝IEEE754 倍精度）で
    /// 走っており、移植の最初の作業がその数値の再現だからである（prereg §6.3）。
    /// 生態系側の場が float なのとは意図的に揃えていない。
    /// </summary>
    public struct ExcitableParams
    {
        /// <summary>発火閾値 θ。入力＋残存興奮がこれ以上なら発火する。</summary>
        public double Threshold;

        /// <summary>発火時の興奮度 e_max。近傍から見た「発火した」の判定値でもある。</summary>
        public double ExcitedLevel;

        /// <summary>未発火セルの興奮度の毎ステップ減衰率 δ。</summary>
        public double Decay;

        /// <summary>発火した近傍1つあたりの入力 k。</summary>
        public double Coupling;

        /// <summary>
        /// 不応期 R₀（ステップ数）。発火したセルはこの間、興奮できない。
        ///
        /// **14 が正しい値である。** N=16 のリングで一方向波がリエントリーしない
        /// 境界は 13/14 で、安全側の 14 を採る（prereg 修正3）。
        /// 旧値 4 の根拠「1周 ≈ 8ステップ」は**両方向波の対蹠到達時間**との
        /// 混同だった。一方向波が1周するには N ステップ要る。
        /// </summary>
        public int RefractoryTicks;

        /// <summary>
        /// ホップごとの振幅減衰 g。発火セルは
        /// 「発火した近傍の振幅の最大値 × g」を受け取る。
        ///
        /// **装飾ではなく操舵機構の構成要素である**（prereg 修正2）。
        /// g = 1.0（減衰なし）だと抗力との相互作用で「後発の側が勝つ」力学になり、
        /// 逃避の符号が反転する。J1 では推力を計算しないので効かないが、
        /// 状態の持ち方を J2 で変えずに済むよう、ここで器を入れておく。
        /// </summary>
        public double Attenuation;

        /// <summary>プロトタイプ準拠の既定値（R₀ のみ 14 に訂正済み）。</summary>
        public static ExcitableParams Default => new ExcitableParams
        {
            Threshold = 0.5,
            ExcitedLevel = 1.0,
            Decay = 0.5,
            Coupling = 0.6,
            RefractoryTicks = 14,
            Attenuation = 0.85,
        };
    }
}
