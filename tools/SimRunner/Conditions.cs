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

        public static readonly Condition NutrientOff = new Condition(
            "nutrient-off",
            "死の場の養分効果のみ無効。Demo 8 第2段 M2 の対照",
            Tweak(p => { p.deathNutrientGrowth = 0f; return p; }));

        public static readonly Condition FearOff = new Condition(
            "fear-off",
            "草食獣が恐怖場を読まない。Demo 8 第2段 M3（迂回行動）の対照",
            Tweak(p => { p.herbivoreFearWeight = 0f; return p; }));

        /// <summary>SimParams は構造体なので、既定値のコピーを書き換えて返す。</summary>
        static SimParams Tweak(Func<SimParams, SimParams> mutate) => mutate(SimParams.Default);

        /// <summary>名前から引く。--conditions で指定するときに使う。</summary>
        public static readonly IReadOnlyDictionary<string, Condition> All =
            new Dictionary<string, Condition>
            {
                [Default.Name] = Default,
                [TrampleOff.Name] = TrampleOff,
                [NutrientOff.Name] = NutrientOff,
                [FearOff.Name] = FearOff,
            };
    }
}
