using System;
using System.Collections.Generic;
using BlockField.SimCore.Ecology;

namespace SimRunner
{
    /// <summary>
    /// 検証で回す条件の定義。
    ///
    /// 【対照の作り方】機構を切るときは**場への書き込みは残し、効果だけを0にする**。
    /// 書き込みまで止めると RNG の消費列が変わって世界の進行そのものが別物になり、
    /// 「その機構の効果」ではなく「別の世界」を比べることになる。
    /// </summary>
    public sealed class Condition
    {
        public string Name { get; }
        public string Description { get; }
        public SimParams Params { get; }

        Condition(string name, string description, SimParams p)
        {
            Name = name;
            Description = description;
            Params = p;
        }

        public static readonly Condition Default =
            new Condition("default", "既定パラメータ（現在の実装そのまま）", SimParams.Default);

        public static readonly Condition TrampleOff = new Condition(
            "trample-off",
            "踏み荒らしの効果のみ無効（書き込みは残す）。Demo 8 第3段 M2 の対照",
            Tweak(p => { p.trampleSuppression = 0f; p.trampleCrushRate = 0f; return p; }));

        /// <summary>
        /// 踏み荒らし→植生の**結合だけ**を切る (第4.5段の先行課題: アブレーション)。
        ///
        /// 【なぜ trample-off と別に要るか】trample-off は成長抑制
        /// (<see cref="SimParams.trampleSuppression"/>) と踏み潰し
        /// (<see cref="SimParams.trampleCrushRate"/>) を**同時に**切るので、
        /// 「結合が効いているか」の問いには答えられない。踏み荒らし場が植生へ
        /// 及ぼす経路は2本あり、結合行列に載っているのは前者だけである。
        /// 48シードの実測では、踏跡の植物密度比への寄与は
        /// 抑制が約2/3（0.731→0.945）、踏み潰しが残り約1/3（→1.051）だった。
        /// </summary>
        public static readonly Condition TrampleSuppressOff = new Condition(
            "trample-suppress-off",
            "踏み荒らし→植生の結合のみ無効（踏み潰しは残す）。結合行列のアブレーション用",
            Tweak(p => { p.trampleSuppression = 0f; return p; }));

        public static readonly Condition NutrientOff = new Condition(
            "nutrient-off",
            "死の場の養分効果のみ無効。Demo 8 第2段 M2 の対照",
            Tweak(p => { p.deathNutrientGrowth = 0f; return p; }));

        public static readonly Condition FearOff = new Condition(
            "fear-off",
            "草食獣が恐怖場を読まない。Demo 8 第2段 M3（迂回行動）の対照",
            Tweak(p => { p.herbivoreFearWeight = 0f; return p; }));

        /// <summary>
        /// 変異を有効にした条件 (Demo 8 第4.5段 K1 の M 判定用)。
        ///
        /// **現行の確定値から始めて変異だけを加える。** 変異は平均を変えない
        /// （ガウスノイズの期待値は0）はずなので、生態指標が第4段の合格範囲から
        /// 外れたら sigma が大きすぎるという読み方になる（prereg の M 判定）。
        /// E1/E2 の実験条件はここではなく、掃引で決めてから足す。
        /// </summary>
        public static readonly Condition Mutation = new Condition(
            "mutation",
            "変異あり（rate=1.0 / sigma=0.1）。第4.5段 K1 の M 判定用",
            Tweak(p => { p.mutationRate = 1f; p.mutationSigma = 0.1f; return p; }));

        /// <summary>SimParams は構造体なので、既定値のコピーを書き換えて返す。</summary>
        static SimParams Tweak(Func<SimParams, SimParams> mutate) => mutate(SimParams.Default);

        // ---- ランダム対照 (Demo 8 第4段 K5) ----
        //
        // 【なぜ「対照条件」ではなく「並走する対照」なのか】--conditions で別条件として
        // 並べると、条件ごとに別々のシード集合の平均を比べることになる。
        // 群れ指標のように分散の大きい量では、その差が機構の効果なのか
        // シードの引きなのか分からない。**同一シードで対にして走らせ、
        // シードごとの差を取る**と、地形と初期配置の違いが相殺される
        // （対応のある比較）。これは 4c の M4「ランダム対照との比で有意に高い」が
        // 要求している形でもある。
        //
        // 対照は本条件とは**別の World・別の Rng インスタンス**で走る。
        // 乱数は World が持つので、対照を足しても本条件の乱数列には一切触れない
        // （--control の有無でハッシュが変わらないことをテストで固定してある）。

        /// <summary>
        /// 群れ重みだけを切った対照 (4c 用)。他は本条件と完全に同一。
        ///
        /// **現時点では w_colony の既定値が 0 なので、対照は本条件と一致する。**
        /// それが正常であり、両者のハッシュ一致は「対照の配線が本条件を
        /// 壊していない」ことの確認になる。4c で w_colony に値が入ると分岐する。
        /// </summary>
        public Condition AsColonyWeightControl() => new Condition(
            Name + "-control",
            "群れ重み w_colony のみ 0（4c の対照）",
            WithColonySelfWeight(Params, 0f));

        static SimParams WithColonySelfWeight(SimParams p, float w)
        {
            p.colonySelfWeight = w;
            return p;
        }

        /// <summary>
        /// 全ての重みを 0 にしたランダム歩行対照（**器のみ。現在は未使用**）。
        ///
        /// roadmap v9「地図性能の計測」用。場を一切読まない個体を走らせ、
        /// 「場を読むことがどれだけの利得を生むか」を測る一般の基準線になる。
        /// 4c の判定には使わない（群れ重みだけを切った対照のほうが、
        /// 差の原因を1つに絞れるため）。
        ///
        /// 実装時の注意: 重みは <see cref="EntityWeights.ForagingFor"/> /
        /// <see cref="EntityWeights.WanderingFor"/> が SimParams から組み立てるので、
        /// ここを 0 にするだけでは足りない場（suitability など個体が持たない重み）は
        /// 影響を受けない。**行動が場を読まなくなるだけで、場の書き込みは残す**
        /// （このファイル冒頭の対照の作り方の原則）。
        /// </summary>
        public Condition AsRandomWalkControl() => new Condition(
            Name + "-randomwalk",
            "全ての場の重みを0にしたランダム歩行（地図性能の基準線。未使用）",
            Tweak(p =>
            {
                p.colonySelfWeight = 0f;
                p.herbivoreVegetationWeight = 0f;
                p.herbivoreFearWeight = 0f;
                p.wolfPreyWeight = 0f;
                return p;
            }));

        /// <summary>名前から引く。--conditions で指定するときに使う。</summary>
        public static readonly IReadOnlyDictionary<string, Condition> All =
            new Dictionary<string, Condition>
            {
                [Default.Name] = Default,
                [TrampleOff.Name] = TrampleOff,
                [TrampleSuppressOff.Name] = TrampleSuppressOff,
                [NutrientOff.Name] = NutrientOff,
                [FearOff.Name] = FearOff,
                [Mutation.Name] = Mutation,
            };
    }
}
