namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 場の識別子 (Demo 8 第4段 K2)。
    ///
    /// 【値の並びは場の名前昇順】<see cref="EntityWeights.FieldNames"/> の索引と
    /// 一致させてあり、<c>FieldIds.NameOf</c> はその配列を引くだけで済む。
    /// ハッシュの畳み込み順・重みの並びと同じ規約に揃えることで、
    /// 場を足したときに「どこかだけ並びがずれる」ことを起こさない
    /// （並びの一致は EditMode テストで検証する）。
    ///
    /// 【なぜ文字列でなく enum か】結合の適用は毎ティック全セルを回る経路にある。
    /// 文字列キーで <see cref="World.Fields"/> を引くと、セル数×結合数だけ
    /// 辞書アクセスが発生する。enum なら分岐1つで場の参照が取れる。
    /// </summary>
    public enum FieldId
    {
        // コロニー場 (Demo 8 第4段 K1)。名前昇順なので死の場より前に来る
        ColonyPig = 0,
        ColonySheep = 1,
        ColonyWolf = 2,

        Death = 3,
        Fear = 4,
        Prey = 5,
        Suitability = 6,
        Trample = 7,
        Vegetation = 8,
    }

    /// <summary>
    /// 結合の形 (Demo 8 第4段 K2)。「source が target にどう効くか」を表す。
    ///
    /// 係数の符号ではなく形で区別するのは、両者が**掛かる場所が違う**ためである。
    /// 促進は成長の結果に足され、抑制は成長量そのものに掛かる。
    /// 符号だけでは「足すのか掛けるのか」を表せない。
    /// </summary>
    public enum CouplingForm
    {
        /// <summary>target += 係数 × source（成長の促進。死の場→植生の養分がこれ）。</summary>
        GrowthBoost,

        /// <summary>target の成長量に (1 - 係数 × source) を掛ける（踏み荒らし→植生がこれ）。</summary>
        GrowthSuppress,
    }

    /// <summary>
    /// 場と場の結合 (Demo 8 第4段 K2)。
    ///
    /// 【なぜ器を作るか】これまで「死の場が植生を育てる」「踏み荒らし場が植生を抑える」は
    /// <see cref="Simulation"/> の成長計算に直接書かれていた。場が増えるたびに
    /// 成長計算へ分岐が足される形になっており、場の数の二乗で複雑になる。
    /// 結合を**データ**にすれば、成長計算は「自分を target とする結合を全部適用する」
    /// という1つの規則になり、結合を足すのは <see cref="SimParams"/> への1行になる。
    /// 第4.5段（進化）で結合の係数そのものを進化させる余地もここに生まれる。
    ///
    /// 【K2 は純粋なリファクタリング】既存2結合を移設するだけで、計算式も適用順も
    /// 変えない。判定 M0a は「48シードで ContentHash が移設前と完全一致」であり、
    /// 浮動小数の演算順が1つでも変われば不一致になる。
    ///
    /// 【抑制の下限は SimParams 側】(1 - 係数 × source) の下限
    /// (<see cref="SimParams.trampleSuppressionFloor"/>) は結合ごとには持たない。
    /// 現状ただ1つの抑制結合のために持ち込むと、移設が「純粋な移設」でなくなるため。
    /// 結合ごとの下限が要るようになった時点で構造体に足す。
    /// </summary>
    public readonly struct FieldCoupling
    {
        /// <summary>効かせる側の場。</summary>
        public readonly FieldId source;

        /// <summary>効かされる側の場。</summary>
        public readonly FieldId target;

        /// <summary>結合の強さ。意味は <see cref="form"/> によって変わる。</summary>
        public readonly float coefficient;

        /// <summary>結合の形。</summary>
        public readonly CouplingForm form;

        public FieldCoupling(FieldId source, FieldId target, float coefficient, CouplingForm form)
        {
            this.source = source;
            this.target = target;
            this.coefficient = coefficient;
            this.form = form;
        }
    }

    /// <summary><see cref="FieldId"/> の補助。</summary>
    public static class FieldIds
    {
        /// <summary>
        /// 場の識別名。<see cref="EntityWeights.FieldNames"/> が唯一の定義であり、
        /// ここはその索引を引くだけにする（名前の二重管理を作らないため）。
        /// </summary>
        public static string NameOf(FieldId id) => EntityWeights.FieldNames[(int)id];
    }
}
