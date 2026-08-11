using System;

namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// コロニー場 (Demo 8 第4段 K1)。**繁殖が成立した場所**に残る痕跡。種ごとに1枚持つ。
    ///
    /// 【何を場に移そうとしているか】これまでの場は「生き物がどこにいたか」
    /// （獲物場・踏み荒らし場）、「どこで死んだか」（死の場）、「どこが危ないか」
    /// （恐怖場）の痕跡だった。コロニー場は**どこで次の世代が生まれたか**を覚える。
    /// 第4段の狙いは、繁殖判定から「相手個体の探索」を消し (K3)、
    /// 移動の重みに自種のコロニー場を足して群れを創発させること (K4) にある。
    /// 4a の時点では**書くだけ**で、誰もこの場を読まない。
    ///
    /// 【なぜ種ごとに3枚か（判断1）】狼も繁殖しており（Demo 8.5 以降
    /// <see cref="Entity.IsAnimal"/> は常に true）、対称に扱わない理由がない。
    /// 狼のパックは生態的にも自然で、第4.5段では狼の群れ度も進化対象になりうる。
    /// 他種のコロニー場を読む「盗聴」は器（<see cref="EntityWeights"/> の重み）だけ
    /// 作って重み0で寝かせる。手で入れた盗聴より、進化が盗聴を発見するかを
    /// 見るほうが研究価値が高いため（判断2）。
    ///
    /// 【τ の設計意図】減衰 0.0025（τ≈400）は死の場 0.003（τ≈333）と同程度で、
    /// 恐怖場 0.03（τ≈33）よりはるかに遅い。集落は世代を跨いで残る痕跡であり、
    /// 「1分前にここに狼がいた」より「ここは代々子が生まれてきた場所だ」のほうが
    /// 長く価値を持つ、という時間スケールの違いをそのまま表す。
    ///
    /// 【拡散を fear より絞った理由 — 実測】prereg の初期案は「fear と同程度
    /// （0.1）から開始」だったが、繁殖は 1000ティックあたり 7.25 回しか起きず、
    /// 死（同 60 回）の 1/8 である。0.1 で 1 パス拡散させると痕跡が
    /// τ の寿命の中で σ≈4.5セルまで広がり、**ピーク値が表示下限 0.02 に一度も
    /// 届かない**（薄く広がった痕跡は読み出しにも表示にも使えない）。
    /// 死の場と同じ 0.02・1パスに絞ってある。詳細は
    /// <see cref="SimParams.colonyDiffuse"/> のコメント参照。
    /// </summary>
    public sealed class ColonyField : DiffusingField
    {
        /// <summary>この場が対応する種。</summary>
        public EntityKind Kind { get; }

        /// <summary>
        /// 種ごとの場の名前。ContentHash・重みの並びは名前昇順なので、
        /// 共通の接頭辞を付けて 3 枚が隣り合うようにしてある
        /// （順序は colony-pig → colony-sheep → colony-wolf）。
        /// </summary>
        public static string NameFor(EntityKind kind) => kind switch
        {
            EntityKind.Sheep => "colony-sheep",
            EntityKind.Pig => "colony-pig",
            EntityKind.Wolf => "colony-wolf",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), $"未知の種: {kind}"),
        };

        /// <summary>
        /// 3枚の名前（名前昇順）。
        /// <see cref="World.ComputeContentHash(System.Collections.Generic.ICollection{string})"/> で
        /// 「コロニー場を除いた部分」を取り出すときの除外指定に使う（判定 M0b）。
        /// </summary>
        public static readonly string[] AllNames =
        {
            NameFor(EntityKind.Pig),
            NameFor(EntityKind.Sheep),
            NameFor(EntityKind.Wolf),
        };

        public ColonyField(EntityKind kind, int width, int depth)
            : base(NameFor(kind), width, depth)
        {
            Kind = kind;
        }

        public override void Update(SimParams p)
        {
            int passes = p.colonyDiffusePasses < 1 ? 1 : p.colonyDiffusePasses;
            for (int i = 0; i < passes - 1; i++)
            {
                UpdateDiffusion(p.colonyDiffuse, 0f);
            }
            UpdateDiffusion(p.colonyDiffuse, p.colonyDecay);
        }
    }
}
