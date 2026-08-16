namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// 流れ場のパラメータ (系列2 Phase B)。
    ///
    /// **世界法則の側だけを持つ。** 生物の戦略パラメータはここに置かない
    /// （置いた時点で「創発」の主張が無効になる。prototypes/README の設計原則）。
    /// </summary>
    public struct FlowParams
    {
        /// <summary>ノイズのシード。3成分にはこれと +1、+2 を使う。</summary>
        public uint Seed;

        /// <summary>fBm のオクターブ数。3 が既定（roadmap の「カールノイズ3オクターブ」）。</summary>
        public int Octaves;

        /// <summary>
        /// ノイズの空間スケール（セルあたりの周波数）。
        /// 小さいほど渦が大きくなる。0.13 でおよそ 8 セル周期の渦。
        /// </summary>
        public float NoiseScale;

        /// <summary>1ティックあたりノイズ座標を進める量。流れの「ゆっくりした変化」の速さ。</summary>
        public float NoiseTimeStep;

        /// <summary>
        /// 境界のランプ幅（セル数）。壁からこの距離まで ψ を滑らかに 0 へ落とす。
        /// 大きいほど流れが壁を遠回りする。2〜3 セルが目安。
        /// </summary>
        public float BoundaryRampCells;

        /// <summary>
        /// 浮力項の係数。**Phase D（温度センサ）まで 0**。
        /// 枠だけ用意しておくのは、後から入れるときに ψ の構成を変えずに済ませるため。
        ///
        /// 【アブレーション可能性】温度は「流れを作る」と「代謝を動かす」の2経路で効く。
        /// この2つを独立に無効化できる形にしておかないと、ある挙動が選ばれた理由が
        /// 流れなのか代謝なのか切り分けられなくなる（roadmap 系列2 §水流）。
        /// </summary>
        public float BuoyancyWeight;

        public static FlowParams Default => new FlowParams
        {
            Seed = 20260816u,
            Octaves = 3,
            NoiseScale = 0.13f,
            NoiseTimeStep = 0.01f,
            BoundaryRampCells = 2.5f,
            BuoyancyWeight = 0f,
        };
    }
}
