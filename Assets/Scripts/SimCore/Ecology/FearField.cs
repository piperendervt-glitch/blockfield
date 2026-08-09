namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 恐怖場 (Demo 8 第1段 H1)。狼が通ったセルに残る「危険の痕跡」。
    ///
    /// 【何を場に移したか】草食獣が持っていた「どこが危ないか」という知識を、
    /// 個体の内部状態ではなく空間の性質にした。個体は自分の周りの場を読むだけで、
    /// 狼を見つける処理も記憶も持たない。移したのは情動そのものではなく、
    /// **情動が生む行動傾向の空間分布**である。
    ///
    /// 【τ（減衰率）の設計意図】植生場 0.02 より速い 0.03 にしている。
    /// 危険は移動するので、古い情報は価値が下がるためである。
    /// 植物は生えた場所に留まるので痕跡が長持ちしてよいが、
    /// 「1分前にここに狼がいた」は「1分前にここに草があった」より当てにならない。
    /// τ の違いがそのまま情報の鮮度の違いを表す（層別τ設計）。
    ///
    /// 拡散率 0.1 は植生場 0.15 より小さい。恐怖は「通った線」として残ってほしく、
    /// にじみすぎると道の形が失われるため。これが M1（けもの道の創発）の前提になる。
    /// </summary>
    public sealed class FearField : DiffusingField
    {
        /// <summary>World.Fields のキー。ContentHash の畳み込み順は名前昇順。</summary>
        public const string FieldName = "fear";

        public FearField(int width, int depth)
            : base(FieldName, width, depth)
        {
        }

        public override void Update(SimParams p)
        {
            UpdateDiffusion(p.fearDiffuse, p.fearDecay);
        }
    }
}
