using System;
using BlockField.SimCore.Excitable;

namespace BlockField.SimCore.Fluid
{
    /// <summary>
    /// 水槽に浮かべるクラゲ1体 (系列2 Phase C-7/8)。
    ///
    /// 神経は jelly_1 で実装済みの <see cref="ExcitableField"/> のリングをそのまま使う
    /// （タグ `jelly-1.1`。M-J1a〜M-J3c 全判定合格）。
    ///
    /// 【推力は 2D リム収縮のまま】Phase C-8 の文面は「傘の囲む体積の dV/dt から推力」
    /// だが、初回は jelly_1 と同じ 2D リム収縮で実機へ出す。理由:
    /// - 目的は「生きて見えるか」の早期確認で、それには足りる
    /// - dV/dt を入れると**3Dの姿勢（傘の軸）という新しい状態が増え**、
    ///   実機で問題が出たときの切り分けが困難になる
    ///   （Phase B の「原因でない修正を同時に入れない」と同じ判断）
    /// - 抗力係数の逆算（`jelly_side.html` の内部量から）が済んでいないので、
    ///   dV/dt を入れても係数は暫定値のまま。**逆算を先にやる**のが順序として正しい
    ///
    /// 【限界（この段の既知の制約）】リム収縮の推力はリング平面内にしか出ない。
    /// リング平面は水平に固定しているので、**自力で泳ぐのは水平方向だけ**である。
    /// 鉛直方向の動きは流れに運ばれる分しかない。
    /// 次段の dV/dt モデル（推力の大きさは体積変化、向きは傘の軸、
    /// 旋回は収縮の非対称から）でこの制約が外れる。
    ///
    /// 【クラゲは真実側】粒子と違い、位置が力学の状態である。
    /// 決定論の対象に入れ、固定ティックで進める。
    /// </summary>
    public sealed class Jellyfish
    {
        readonly JellyParams m_Params;
        readonly ExcitableField m_Ring;
        readonly float[] m_Cos;
        readonly float[] m_Sin;
        readonly float m_SpeedScale;

        // モデル単位の速度（リング平面 = 水平面）。m/s へは m_SpeedScale で換算する
        float m_ModelVx, m_ModelVz;

        /// <summary>位置 (m、部屋座標)。</summary>
        public float X { get; private set; }
        public float Y { get; private set; }
        public float Z { get; private set; }

        /// <summary>自力遊泳の速度 (m/s)。水平のみ（上の限界を参照）。</summary>
        public float SwimVx => m_ModelVx * m_SpeedScale;
        public float SwimVz => m_ModelVz * m_SpeedScale;

        /// <summary>
        /// **その瞬間の**自力遊泳の速さ (m/s)。
        ///
        /// 【平均値として読んではいけない】推力は発火したステップにしか出ず、
        /// あとは抗力で減衰するので、この値は 1 拍動のあいだに 0.19 から 0.0004 まで振れる。
        /// 2026-08-16 の実機ログは 1 秒間隔（= 拍動周期そのもの）でこれを出していたため、
        /// **常に減衰しきった位相で標本化**され、0.0007 m/s と出ていた。目標は 0.040 m/s。
        /// 平均を見たいときは <see cref="SwimPathX"/> の差分を使うこと。
        /// </summary>
        public float SwimSpeed => (float)Math.Sqrt(SwimVx * SwimVx + SwimVz * SwimVz);

        /// <summary>
        /// 自力遊泳ぶんだけを積分した変位 (m)。流れに運ばれたぶんは含まない。
        /// 2点の差を経過時間で割れば、その区間の平均遊泳速度になる
        /// （<see cref="CalibrateSpeedScale"/> が目標へ合わせているのと同じ統計量）。
        /// </summary>
        public float SwimPathX { get; private set; }
        public float SwimPathZ { get; private set; }

        /// <summary>流れに運ばれたぶんだけを積分した変位 (m)。鉛直はこちらにしか出ない。</summary>
        public float DriftPathX { get; private set; }
        public float DriftPathY { get; private set; }
        public float DriftPathZ { get; private set; }

        /// <summary>これまでの拍動回数（ペースメーカーが実際に発火した数）。</summary>
        public long PulseCount { get; private set; }

        /// <summary>神経ステップ数。</summary>
        public long StepCount { get; private set; }

        public ExcitableField Ring => m_Ring;
        public float BellDiameter => m_Params.BellDiameter;

        /// <summary>1拍動のステップ数。平均を取る窓の長さに使う。</summary>
        public int PulsePeriodTicks => m_Params.PulsePeriodTicks;

        /// <summary>壁面での反発の強さ (m/s)。実機で振るためパネルとログに出す。</summary>
        public float WallRepelSpeed => m_Params.WallRepelSpeed;

        /// <summary>モデル速度を m/s へ換算する係数（診断用）。</summary>
        public float SpeedScale => m_SpeedScale;

        // ================= K2: dV/dt 噴流モデル =================

        /// <summary>傘の姿勢。噴流モデルのときだけ動く（<see cref="JellyPosture"/>）。</summary>
        public JellyPosture Posture => m_Posture;
        JellyPosture m_Posture = JellyPosture.Upright;

        /// <summary>角速度（部屋座標、rad/s）。トルクの積分だけで変わる。</summary>
        float m_OmX, m_OmY, m_OmZ;

        /// <summary>噴流モデルの速度（部屋座標、モデル単位）。軸方向に推力が乗る。</summary>
        float m_JetVx, m_JetVy, m_JetVz;

        /// <summary>前ステップの傘の囲む体積（モデル単位）。dV/dt に使う。</summary>
        float m_PrevVolume;
        bool m_HasPrevVolume;

        /// <summary>噴流モデルか。</summary>
        public bool IsJetModel => m_Params.JetModel;

        /// <summary>
        /// 沈降速度 (m/s、下向き)。世界法則（追記10）。
        /// 較正では無効にするので、較正中は 0 を返す。
        /// </summary>
        public float SinkSpeed => m_SinkEnabled ? m_Params.SwimSpeed * SinkRatio : 0f;
        bool m_SinkEnabled = true;

        /// <summary>
        /// **ペースメーカーを働かせるか**（実行時の入力。パラメータは変えない）。
        ///
        /// 「拍動＝沈まないための努力」を見せるには、止めて沈むところを
        /// 見せるのが最も直接的である。湧かせ直すと位置が戻って見えないので、
        /// 実行時のトグルにしてある。
        /// </summary>
        public bool PacemakerEnabled { get; set; } = true;

        /// <summary>沈降ぶんだけを積分した変位 (m)。自力遊泳とは分けて記録する。</summary>
        public float SinkPathY { get; private set; }

        /// <summary>
        /// **実行時に変えられる世界法則**（復元の強さ・沈降比）。
        ///
        /// 【なぜ実行時に変えるか】作り直すと姿勢が初期化される。姿勢を代入する口は
        /// 作らない方針なので、作り直し＝直立に戻る、になる。
        /// ところが**復元の差は姿勢が崩れた状態でしか見えない**
        /// （切ると 175° まで倒れて戻らない。入れると 14° で止まり 2.3° へ戻る）。
        /// 2026-08-18 の実機では切り替えのたびに作り直しており、
        /// **差が現れる状態そのものを毎回消していた**（7分で 64 回）。
        ///
        /// これらは値の差し替えであって状態の代入ではないので、実行時に変えてよい。
        /// </summary>
        public float RightingGain { get; set; }
        public float SinkRatio { get; set; }

        /// <summary>軸が真上から傾いている角度（度）。判定とログ用。</summary>
        public float TiltDegrees => m_Posture.TiltDegrees();

        /// <summary>
        /// 傘の囲む体積（モデル単位）。収縮でリムが縮むと減る。
        /// 形は <c>V ∝ r̄² · h</c>（r̄ = リムの平均半径）。dV/dt の元。
        /// </summary>
        float BellVolume()
        {
            float sum = 0f;
            for (int i = 0; i < m_Params.RingCells; i++) sum += Contraction(i);
            float meanC = sum / m_Params.RingCells;
            float r = 1f - k_RimShrink * meanC;      // 半径（傘径で規格化）
            return r * r;                             // 高さは一定なので比例定数に畳む
        }

        /// <summary>収縮でリムがどれだけ縮むか。View の k_ContractionDepth と同じ値。</summary>
        const float k_RimShrink = 0.32f;

        /// <summary>
        /// 水槽の形。渡すと固体セルへ入る移動を受け付けなくなる
        /// （<see cref="JellyBoundary"/>）。null なら境界なし（単体テスト用）。
        /// </summary>
        readonly FlowGrid m_Tank;
        readonly bool[] m_Contact;
        bool m_InContact;

        public Jellyfish(JellyParams p, float x, float y, float z, FlowGrid tank = null)
            : this(p, x, y, z, tank, calibrate: true) { }

        /// <param name="calibrate">
        /// false なら換算係数を 1 のままにする。**較正そのものが本体を走らせる**ので、
        /// その内側で再び較正しないための入口（無限再帰の回避）。
        /// </param>
        Jellyfish(JellyParams p, float x, float y, float z, FlowGrid tank, bool calibrate)
        {
            m_Params = p;
            m_Tank = tank;
            m_Ring = new ExcitableField(ExcitableGraphs.Ring(p.RingCells));

            m_Cos = new float[p.RingCells];
            m_Sin = new float[p.RingCells];
            for (int i = 0; i < p.RingCells; i++)
            {
                double a = 2.0 * Math.PI * i / p.RingCells;
                m_Cos[i] = (float)Math.Cos(a);
                m_Sin[i] = (float)Math.Sin(a);
            }

            // 侵害受容の受け皿。較正の走行では水槽が無いので確保しない
            m_Contact = tank != null ? new bool[p.RingCells] : null;

            X = x; Y = y; Z = z;
            RightingGain = p.RightingGain;
            SinkRatio = p.SinkRatio;
            m_SpeedScale = !calibrate ? 1f
                : p.JetModel ? CalibrateJetSpeedScale(p)
                : CalibrateSpeedScale(p);
        }

        /// <summary>
        /// **噴流モデルの換算係数**を実測で出す（jelly_2 K2）。
        ///
        /// 【なぜ別に要るか】<see cref="CalibrateSpeedScale"/> は 2D リム収縮の
        /// モデルを走らせて係数を出す。噴流の速度は推力の形が違うので**桁が違い**、
        /// 2D 用の係数を掛けると速度が合わない。実機で
        /// **目標 0.04 m/s に対し 0.001067 m/s（2.7%、1.07mm/s）** になり、
        /// 「止水でクラゲが移動しない」と報告された（2026-08-16）。
        ///
        /// 【なぜテストで捕まらなかったか】M-K2d は変位の**向き**しか見ておらず、
        /// 大きさの下限は「0 でないこと」を保証する 1e-4 m だけだった。
        /// **変位が出ることと、視認できる速度で出ることは別**である。
        /// 速度の大きさを見る判定を M-K2i として足した。
        ///
        /// 【経路長で測る】噴流モデルは旋回するので正味変位は曲がったぶん短くなる。
        /// 「どれだけ速く動くか」は経路長で測るのが正しい。
        /// </summary>
        static float CalibrateJetSpeedScale(JellyParams p)
        {
            // 【較正は必ずペースメーカーを働かせる】測るのは「この個体が自分の
            // ペースメーカーでどれだけ速く泳ぐか」であって、判定側が発火を
            // 制御するために切っているかどうかとは無関係。
            // 継承すると刺激ゼロ → 速度ゼロ → 係数 0 になり、**全部止まる**
            var probeParams = p;
            probeParams.Pacemaker = true;
            var probe = new Jellyfish(probeParams, 0f, 0f, 0f, null, calibrate: false);
            // 【較正では沈降を切る】沈降ぶんまで正規化すると遊泳速度が目標からずれる。
            // 測るのは「自力でどれだけ速く動くか」（追記10 A10.2）
            probe.m_SinkEnabled = false;
            const float dt = 1f / 40f;

            for (int t = 0; t < 800; t++) probe.Step(dt, 0f, 0f, 0f);   // 過渡を外す

            float px = probe.X, py = probe.Y, pz = probe.Z;
            double path = 0;
            for (int t = 0; t < 800; t++)
            {
                probe.Step(dt, 0f, 0f, 0f);
                double ddx = probe.X - px, ddy = probe.Y - py, ddz = probe.Z - pz;
                path += Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz);
                px = probe.X; py = probe.Y; pz = probe.Z;
            }
            float sustained = (float)(path / (800.0 * dt));
            return sustained > 1e-9f ? p.SwimSpeed / sustained : 0f;
        }

        /// <summary>
        /// モデルの持続遊泳速度を測り、目標速度へ合わせる係数を出す。
        ///
        /// jelly_1 の J3a と同じ手順（過渡を外した区間の平均速度）を、
        /// 実際のパラメータで走らせて測る。解析式で出さないのは
        /// パラメータ（g・抗力・周期）を変えても追従させるため。
        /// 同じパラメータなら同じ値が出るので決定論は保たれる。
        /// </summary>
        static float CalibrateSpeedScale(JellyParams p)
        {
            var ring = new ExcitableField(ExcitableGraphs.Ring(p.RingCells));
            var cos = new float[p.RingCells];
            var sin = new float[p.RingCells];
            for (int i = 0; i < p.RingCells; i++)
            {
                double a = 2.0 * Math.PI * i / p.RingCells;
                cos[i] = (float)Math.Cos(a);
                sin[i] = (float)Math.Sin(a);
            }

            float vx = 0f, vz = 0f, x = 0f, z = 0f;
            float x800 = 0f, z800 = 0f;
            const int total = 1600;

            for (int t = 0; t < total; t++)
            {
                if (t % p.PulsePeriodTicks == 0 && ring.Refractory(p.PacemakerCell) == 0)
                {
                    ring.Stimulate(p.PacemakerCell, p.Excitable);
                }
                ring.Step(p.Excitable);

                var fired = ring.LastFired;
                for (int f = 0; f < fired.Count; f++)
                {
                    int i = fired[f];
                    float amp = (float)ring.Amplitude(i);
                    vx -= amp * cos[i];
                    vz -= amp * sin[i];
                }
                vx *= (1f - p.Drag); vz *= (1f - p.Drag);
                x += vx; z += vz;
                if (t == 799) { x800 = x; z800 = z; }
            }

            // 過渡を外した区間 800〜1600 の平均速度（モデル単位/ティック）
            float dx = x - x800, dz = z - z800;
            float sustained = (float)Math.Sqrt(dx * dx + dz * dz) / (total - 800);
            return sustained > 1e-9f ? p.SwimSpeed / sustained : 0f;
        }

        /// <summary>
        /// 神経1ステップぶん進める。
        /// </summary>
        /// <param name="dtSeconds">1ステップの実時間（神経 40Hz なら 1/40）。</param>
        /// <param name="flowVx">その位置の流速 (m/s)。クラゲは流れに書き戻さない。</param>
        public void Step(float dtSeconds, float flowVx, float flowVy, float flowVz)
        {
            if (m_Params.Pacemaker && PacemakerEnabled
                && StepCount % m_Params.PulsePeriodTicks == 0
                && m_Ring.Refractory(m_Params.PacemakerCell) == 0)
            {
                m_Ring.Stimulate(m_Params.PacemakerCell, m_Params.Excitable);
                PulseCount++;
            }

            StepNociception();

            m_Ring.Step(m_Params.Excitable);

            // 収縮したセルは自分の側と逆向きに体を押す（jelly_1 と同じ局所則）
            var fired = m_Ring.LastFired;
            for (int f = 0; f < fired.Count; f++)
            {
                int i = fired[f];
                float amp = (float)m_Ring.Amplitude(i);
                m_ModelVx -= amp * m_Cos[i];
                m_ModelVz -= amp * m_Sin[i];
            }
            m_ModelVx *= (1f - m_Params.Drag);
            m_ModelVz *= (1f - m_Params.Drag);

            if (m_Params.JetModel)
            {
                StepJet(dtSeconds);
            }

            // 壁から離れる向きの速度。壁は環境の情報で、神経が決めた推力は書き換えない
            JellyBoundary.Repulsion(m_Tank, X, Y, Z,
                m_Params.WallBandCells, m_Params.WallRepelSpeed,
                out float rx, out float ry, out float rz);

            // 噴流モデルなら3方向すべてに自力の速度が出る（jelly_1 の水平限定が外れる）
            float sx = m_Params.JetModel ? m_JetVx * m_SpeedScale : SwimVx;
            float sy = m_Params.JetModel ? m_JetVy * m_SpeedScale : 0f;
            float sz = m_Params.JetModel ? m_JetVz * m_SpeedScale : SwimVz;

            // 沈降は世界法則。自力遊泳でも流れでもないので別の項として足す
            float sink = SinkSpeed;

            float toX = X + (sx + flowVx + rx) * dtSeconds;
            float toY = Y + (sy + flowVy + ry - sink) * dtSeconds;
            float toZ = Z + (sz + flowVz + rz) * dtSeconds;

            // 壁の中へは入らせない。壁は環境の情報で、神経が決めた推力は書き換えない
            JellyBoundary.ClampMove(m_Tank, X, Y, Z, ref toX, ref toY, ref toZ);
            X = toX; Y = toY; Z = toZ;

            // 診断用の内訳。位置の更新式はそのまま（丸めの経路を変えないため）
            SwimPathX += sx * dtSeconds;
            SwimPathZ += sz * dtSeconds;
            SinkPathY -= sink * dtSeconds;
            DriftPathX += flowVx * dtSeconds;
            DriftPathY += flowVy * dtSeconds;
            DriftPathZ += flowVz * dtSeconds;

            StepCount++;
        }

        /// <summary>
        /// 境界からの侵害受容を1ステップぶん（jelly_2 K3）。
        ///
        /// **どのセルが発火するかは環境が決める。** ここがやるのは
        /// <see cref="JellyBoundary.SurfaceContact"/> が真を立てたセルを
        /// そのまま刺激することだけで、逃避の向きは作らない。強さも通常の刺激と同格
        /// （別に持つと自由度が増えて創発の主張が弱まる — prereg §3.1）。
        ///
        /// 【侵入時に1回だけ】毎ステップ入れると不応期で半分が空振りし、
        /// T = R₀ の谷（§5.2）と同じ現象が起きる。周期 T ごとに入れる形も
        /// 撃ちすぎになった（追記14 A14.3）。**再発火の周期は環境が決める** —
        /// 「入る → 撃つ → 出る → 沈む → 入る」で、こちらでは選べない。
        ///
        /// 【副作用: 恒久ロックアウト】**接触が解除されない環境（隅・天井）では、
        /// 再発火の周期が実質的に無限になる。** 解除条件を「退出」以外に持たせるかは
        /// 実機で見てから決める（追記16 A16.5）。沈降を 1.10 → 1.50 に強めると
        /// 着底が 6/48 → 3/48 に**減る**のはこの副作用の直接の証拠で、
        /// 沈降が速いほど帯から速く抜けてロックアウトが解ける。
        /// </summary>
        void StepNociception()
        {
            if (!m_Params.Nociception || m_Tank == null || m_Contact == null) return;

            int hit = JellyBoundary.SurfaceContact(m_Tank, X, Y, Z,
                m_Params.BellDiameter * 0.5f, m_Posture, m_Cos, m_Sin,
                m_Params.NociceptionBandCells, m_Contact);

            NociceptedCells = hit;
            if (hit == 0) { m_InContact = false; return; }

            // 【侵入時に1回だけ】以前は帯を出入りするたびに数え直しており、
            // 壁と床に同時に接する状況で撃ちすぎになった。不応期が飽和して
            // ペースメーカーの進行波を潰し、**48シード中6個体が着底した**
            // （発火 67〜162回。漂えた個体は 16回。prereg 追記14 A14.3）。
            // 出入りの周期は環境が決めるので、こちらでは選べない
            if (m_InContact) return;
            m_InContact = true;

            for (int i = 0; i < m_Contact.Length; i++)
            {
                if (m_Contact[i]) StimulateCell(i);
            }
            NociceptionCount++;
        }

        /// <summary>直近のステップで壁の帯に入っていた受容器の数。診断とログ用。</summary>
        public int NociceptedCells { get; private set; }

        /// <summary>
        /// 侵害受容が刺激を入れた**試行**回数。診断とログ用。
        ///
        /// 【実際に興奮した数ではない】<see cref="StimulateCell"/> は不応期のセルを
        /// 弾くので、全セルが不応期なら空振りでもこの値は 1 増える（追記16 A16.4）。
        /// </summary>
        public long NociceptionCount { get; private set; }

        /// <summary>
        /// 指定したセルを刺激する。**環境が刺激の位置を決める入口**（jelly_2 K3）。
        ///
        /// 逃避の向きは既存の機構（伝播の時間差 + 減衰勾配）から創発するので、
        /// ここで向きを渡すことはない。渡すのは**どのセルか**だけである。
        /// テストが対称／片側／鏡像を作るのにも使う。
        /// </summary>
        public void StimulateCell(int cell)
        {
            if (cell < 0 || cell >= m_Params.RingCells) return;
            if (m_Ring.Refractory(cell) != 0) return;
            m_Ring.Stimulate(cell, m_Params.Excitable);
        }

        /// <summary>
        /// **テスト専用**: 角速度を1ステップぶん与えて姿勢を動かす。
        ///
        /// 軸を直接代入する口はどこにも作らない（「軸は積分されるだけで、
        /// 代入されない」— 追記7 A7.1）。傾いた初期姿勢が要るテストは、
        /// **積分を通して**傾ける。
        /// </summary>
        public void NudgeForTest(float omX, float omY, float omZ, float dt)
        {
            m_Posture.Integrate(omX, omY, omZ, dt);
        }

        /// <summary>
        /// dV/dt 噴流モデルの1ステップ（jelly_2 K2）。
        ///
        /// - 推力の**大きさ**: 傘の囲む体積の減少から。形は K1 が読んだ
        ///   <c>∝ dV·(dV/開口)</c>（`jelly_side.html` の `J = c·dA·(dA/ap)`）
        /// - 推力の**向き**: 傘の軸
        /// - **旋回**: 収縮の非対称から。各発火セルが自分の位置で軸方向に水を吐くので、
        ///   トルクは <c>Σ(amp_i · r̂_i) × 軸</c> になる。**対称に発火すれば
        ///   Σ(amp_i · r̂_i) = 0 なのでトルクは構造的に 0**（M-K2a が構成から従う）
        ///
        /// 【方向を計算していない】トルクは局所的な寄与の総和で、既存の推力
        /// （`m_ModelVx -= amp * m_Cos[i]`）とまったく同じ形である。
        /// 軸は <see cref="JellyPosture.Integrate"/> でしか変わらない。
        /// </summary>
        void StepJet(float dtSeconds)
        {
            // --- 推力: 体積の減少 ---
            float volume = BellVolume();
            float dV = m_HasPrevVolume ? m_PrevVolume - volume : 0f;
            m_PrevVolume = volume;
            m_HasPrevVolume = true;

            if (dV > 0f)
            {
                // 開口はリムの面積に比例（体積と同じ規格化）。dV·(dV/開口)
                float aperture = volume > 1e-6f ? volume : 1e-6f;
                float thrust = dV * (dV / aperture);
                m_JetVx += thrust * m_Posture.AxisX;
                m_JetVy += thrust * m_Posture.AxisY;
                m_JetVz += thrust * m_Posture.AxisZ;
            }
            m_JetVx *= (1f - m_Params.Drag);
            m_JetVy *= (1f - m_Params.Drag);
            m_JetVz *= (1f - m_Params.Drag);

            // --- 旋回: 発火の非対称 ---
            float mx = 0f, my = 0f, mz = 0f;
            var fired = m_Ring.LastFired;
            for (int f = 0; f < fired.Count; f++)
            {
                int i = fired[f];
                float amp = (float)m_Ring.Amplitude(i);
                m_Posture.RadialAt(m_Cos[i], m_Sin[i], out float ux, out float uy, out float uz);
                mx += amp * ux; my += amp * uy; mz += amp * uz;
            }
            // トルク = (Σ amp·r̂) × 軸
            float g = m_Params.TurnGain;
            float tx = g * (my * m_Posture.AxisZ - mz * m_Posture.AxisY);
            float ty = g * (mz * m_Posture.AxisX - mx * m_Posture.AxisZ);
            float tz = g * (mx * m_Posture.AxisY - my * m_Posture.AxisX);

            // --- 復元: 受動的な物理（行き先の計算ではない）---
            m_Posture.RightingTorque(RightingGain,
                out float wx, out float wy, out float wz);
            tx += wx; ty += wy; tz += wz;

            m_OmX = (m_OmX + tx) * (1f - m_Params.RotationDrag);
            m_OmY = (m_OmY + ty) * (1f - m_Params.RotationDrag);
            m_OmZ = (m_OmZ + tz) * (1f - m_Params.RotationDrag);

            // 軸はここでしか変わらない
            m_Posture.Integrate(m_OmX, m_OmY, m_OmZ, dtSeconds);
        }

        /// <summary>
        /// 位置を直接置く。**壁へのめり込みを戻す用**であり、力学の一部ではない。
        /// 速度は変えないので、押し戻しても泳ぎ方は変わらない。
        /// </summary>
        public void Teleport(float x, float y, float z)
        {
            X = x; Y = y; Z = z;
        }

        /// <summary>
        /// リムの収縮の度合い（0 = 弛緩、1 = 最大収縮）。傘の描画に使う。
        /// 不応期の残りを 0〜1 に写したもので、発火直後が最も縮んでいる。
        /// </summary>
        public float Contraction(int cell)
        {
            int r0 = m_Params.Excitable.RefractoryTicks;
            if (r0 <= 0) return 0f;
            return m_Ring.Refractory(cell) / (float)r0;
        }

        /// <summary>決定論の検証用ハッシュ。神経の状態と位置・速度を畳み込む。</summary>
        public ulong ComputeContentHash()
        {
            const ulong prime = 1099511628211UL;
            ulong hash = m_Ring.ComputeContentHash();
            unchecked
            {
                foreach (float v in new[] { X, Y, Z, m_ModelVx, m_ModelVz })
                {
                    uint bits = (uint)BitConverter.SingleToInt32Bits(v);
                    hash = (hash ^ (bits & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 8) & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 16) & 0xFF)) * prime;
                    hash = (hash ^ ((bits >> 24) & 0xFF)) * prime;
                }
                hash = (hash ^ (ulong)StepCount) * prime;
            }
            return hash;
        }
    }
}
