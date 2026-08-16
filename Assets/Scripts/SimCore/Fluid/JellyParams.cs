using BlockField.SimCore.Excitable;

namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// 水槽に浮かべるクラゲのパラメータ (系列2 Phase C)。
    ///
    /// 【jelly_1 のモデルには長さも時間も無い】`RingSwimmer` のセルは
    /// 単位円上の角度しか持たず、推力の単位も抗力の時定数も無次元である。
    /// J3a で測った持続遊泳速度 0.298032 は「推力単位/ティック」であって m/s ではない。
    /// **傘径から遊泳速度は導出できない。** 長さ・時間・速さの3つとも
    /// ここで外から与える（Phase B で ψ の振幅に物理スケールが無かったのと同じ構造）。
    /// </summary>
    public struct JellyParams
    {
        /// <summary>傘の直径 (m)。ミズクラゲの実物は 10〜25cm。</summary>
        public float BellDiameter;

        /// <summary>
        /// **目標遊泳速度 (m/s)。暫定値 0.04。**
        ///
        /// モデルの速度は無次元なので、この値へ正規化して単位を与える。
        /// 1拍動（1秒）あたり 4cm = 傘径の 0.27 倍進む計算。
        ///
        /// 【暫定である理由】prereg jelly_1 §9 は「擬似流体の抗力係数は未確定。
        /// J2 の実装時に jelly_side.html の内部量の実測から逆算する
        /// （理論値を使わない）」としている。**その逆算はまだ済んでいない。**
        /// jelly_2 で逆算値に置き換える。
        /// </summary>
        public float SwimSpeed;

        /// <summary>ペースメーカーの周期（神経ステップ数）。40 ステップ = 1.0 秒。</summary>
        public int PulsePeriodTicks;

        /// <summary>ペースメーカーのセル位置。ここが進行方向を決める（heading 変数は持たない）。</summary>
        public int PacemakerCell;

        /// <summary>神経環のセル数。jelly_1 は 16。</summary>
        public int RingCells;

        /// <summary>擬似流体の抗力（神経1ステップあたり）。jelly_1 の J3 と同じ 0.1。</summary>
        public float Drag;

        /// <summary>興奮性媒質のパラメータ。R₀=14 など jelly_1 の確定値。</summary>
        public ExcitableParams Excitable;

        public static JellyParams Default => new JellyParams
        {
            BellDiameter = 0.15f,
            SwimSpeed = 0.04f,
            PulsePeriodTicks = 40,
            PacemakerCell = 8,
            RingCells = 16,
            Drag = 0.1f,
            Excitable = ExcitableParams.Default,
        };
    }
}
