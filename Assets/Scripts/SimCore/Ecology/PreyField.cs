namespace BlockField.SimCore.Ecology
{
    /// <summary>
    /// 獲物場 (Demo 8 第1段 H3)。草食獣が通ったセルに残る「獲物の匂い」。
    ///
    /// 【何を場に移したか】狼が持っていた「半径6セル以内の最近接の草食獣を探す」
    /// という**探索計算そのもの**を場の読み出しに置き換えた。
    /// 全個体を走査する O(N²) の処理が、4近傍を見るだけの O(1) になる。
    /// 狼は獲物の位置を知らず、匂いの濃い方へ進むだけである。
    ///
    /// 【τの設計 — 当初の想定が実測で覆った】
    /// 当初は「獲物は動くので鮮度が重要」と考え、恐怖場より速く消える設計
    /// （deposit 0.3 / 拡散 0.15 / 減衰 0.05）にした。しかし実測すると捕食率が
    /// 置換前の30%まで落ち、狼が絶滅した。
    ///
    /// 原因は鮮度ではなく**匂いの届く距離**だった。この拡散方式で匂いが届く距離は
    /// おおよそ L = sqrt(D / decay)（D は1パスあたり拡散率/4）で決まり、
    /// 当初値では L ≈ 0.9セル。つまり狼は隣接するまで匂いを感じられず、
    /// 感じたときには既に捕食できる距離だった＝方向情報がゼロだった。
    ///
    /// 旧実装の視界半径6セルに相当する L を得るには減衰を遅くして拡散を強める必要があり、
    /// 拡散4パス・減衰0.015 で L ≈ 7.3セルとした。捕食率は置換前の122%に回復している。
    /// 結果として減衰は植生場(0.02)より**遅く**なった。当初の「鮮度重視」という
    /// 想定は誤りで、追跡に使える勾配を張るには一定の持続が要るのが実態である。
    ///
    /// この2つの場は対になっている。恐怖場だけでは狼の探索コードが削れず、
    /// 判定基準「個体側のコードが実際に削れたか」を満たせない（prereg 参照）。
    /// </summary>
    public sealed class PreyField : DiffusingField
    {
        /// <summary>World.Fields のキー。ContentHash の畳み込み順は名前昇順。</summary>
        public const string FieldName = "prey";

        public PreyField(int width, int depth)
            : base(FieldName, width, depth)
        {
        }

        public override void Update(SimParams p)
        {
            // 匂いの到達距離を稼ぐため拡散を複数回かける。減衰は最後の1回だけ
            int passes = p.preyDiffusePasses < 1 ? 1 : p.preyDiffusePasses;
            for (int i = 0; i < passes - 1; i++)
            {
                UpdateDiffusion(p.preyDiffuse, 0f);
            }
            UpdateDiffusion(p.preyDiffuse, p.preyDecay);
        }
    }
}
