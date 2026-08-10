namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 踏み荒らし場 (Demo 8 第3段 J1)。動物が通ったセルに残り、植生を抑える。
    ///
    /// 【死の場との対】死の場が「死んだ場所に草が茂る」なら、
    /// 踏み荒らし場は「歩いた場所の草が消える」。個体の行動が地形の
    /// 見た目を変え、その見た目がまた行動を変えるという循環を作る。
    ///
    /// 【τ中】減衰 0.02（τ≈50ティック）。これは「踏まれた草が回復する速さ」であり、
    /// 恐怖場 (0.03, τ≈33) よりやや遅く、死の場 (0.003, τ≈333) より大幅に速い。
    /// 通行が続くかぎり道は残り、通らなくなれば草が戻る。
    ///
    /// 【拡散は最小】1パス・拡散率0.02（到達距離 L≈1.0セル）。
    /// 踏み跡は歩いた筋そのものであるべきで、にじませると道の形が失われる。
    /// 第2段の教訓（総量が「書き込み量×τ」で頭打ちの場では、広げるほど
    /// 1セルあたりの値が閾値を割って逆に狭くなる）から、パス数は増やさない。
    /// </summary>
    public sealed class TrampleField : DiffusingField
    {
        public const string FieldName = "trample";

        public TrampleField(int width, int depth) : base(FieldName, width, depth)
        {
        }

        public override void Update(SimParams p)
        {
            int passes = p.trampleDiffusePasses < 1 ? 1 : p.trampleDiffusePasses;
            for (int i = 0; i < passes - 1; i++)
            {
                UpdateDiffusion(p.trampleDiffuse, 0f);
            }
            UpdateDiffusion(p.trampleDiffuse, p.trampleDecay);
        }
    }
}
