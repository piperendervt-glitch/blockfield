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

        /// <summary>
        /// 壁面での反発の強さ (m/s)。0 で無効。
        /// 推力の向きは一定なので、これが無いと壁際に張り付いたままになる
        /// （<see cref="JellyBoundary.Repulsion"/>）。
        /// </summary>
        public float WallRepelSpeed;

        /// <summary>反発が働く帯の幅（セル数）。壁からこの距離より外では効かない。</summary>
        public float WallBandCells;

        // ================= K2: dV/dt 噴流モデル =================

        /// <summary>
        /// **dV/dt 噴流モデルを使う**（jelly_2 K2）。false なら jelly_1 の
        /// 2D リム収縮（水平のみ、姿勢なし）。
        ///
        /// 既定は false。Phase C（タグ `aquarium-c.2`）の挙動をそのまま残し、
        /// 「原因でない修正を同時に入れない」を守る。実機へ出すのは判定の後。
        /// </summary>
        public bool JetModel;

        /// <summary>
        /// 非対称収縮からトルクへの結合係数。**遺伝子**（jelly_2 §4.1）。
        ///
        /// 【値は暫定】K1 の打ち切りにより実測から取れない。K2 の実装要件は
        /// 「経路が存在し、係数が外部から与えられる形になっている」ことのみで、
        /// 値の探索は K4 に送る（jelly_1 §5.2 と同じ形）。
        /// 判定 M-K2b は**対照との比**で置いてあるので、この値に依存しない。
        /// </summary>
        public float TurnGain;

        /// <summary>
        /// 姿勢の復元トルク（傘を上向きへ戻す）。**遺伝子**。0 でアブレーション。
        ///
        /// 実物のミズクラゲは姿勢を立て直す。入れないと逆さまのまま泳ぎ続け、
        /// 「生きて見えない」の要因になる。ただし時定数で戻るので、
        /// 壁から離れる時間は稼げる（K3 の「往復」の予想は生きたまま）。
        /// </summary>
        public float RightingGain;

        /// <summary>角速度の減衰（1ステップあたり）。並進の <see cref="Drag"/> の回転版。</summary>
        public float RotationDrag;

        /// <summary>
        /// 内蔵ペースメーカーを働かせるか（既定 true）。
        ///
        /// 【なぜ切れる形が要るか】ペースメーカーは**1セル**（`PacemakerCell`）を
        /// 叩くので、それ自体が非対称な発火である。対称／片側／鏡像を比べる判定では
        /// これが全条件に混ざり、**片側条件が対称になり鏡像条件だけが非対称になる**
        /// という取り違えが起きた（最初に書いたテストが実際にそうなった）。
        /// 発火のさせ方を判定側が完全に決められるようにする。
        /// プロトタイプの `world.pace` と同じ位置づけ。
        /// </summary>
        public bool Pacemaker;

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
            // 【既定は 0（無効）。旋回が入るまで意味がない】
            // 2026-08-16 の実機で 0.05 / 0.10 / 0.20 を振ったが、**どれでも止まった**。
            // 強くすると釣り合う位置が遠くなるだけで（0.20 で壁から 11cm、
            // 10秒以上その場に静止）、釣り合うこと自体は変わらない。
            //
            // 理由は推力の向きが一定であること。重心まわりの回転が無いので
            // クラゲは一方向にしか進まず、**どの壁にも真正面から当たる**。
            // 位置を押し戻しても推力が同じ向きに出続けるので釣り合うだけ。
            // 「壁の向きが一様でなければ壁沿いに滑る」という見立ては外れた
            // （実際には真正面のケースしか起きなかった）。
            //
            // 機構は残してあるが、**旋回が入るまでは有効にしない**。
            // 有効にすると「対処済み」に見えてしまう。
            WallRepelSpeed = 0f,
            // K2 は既定オフ。Phase C の挙動を変えない
            JetModel = false,
            Pacemaker = true,
            // どちらも遺伝子の暫定値。K4 が探索する
            TurnGain = 1.0f,
            RightingGain = 0.5f,
            RotationDrag = 0.1f,
            WallBandCells = 2.5f,
            Excitable = ExcitableParams.Default,
        };
    }
}
