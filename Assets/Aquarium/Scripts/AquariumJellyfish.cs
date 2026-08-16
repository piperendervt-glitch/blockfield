using BlockField.SimCore.Fluid;
using UnityEngine;

namespace BlockField.Aquarium
{
    /// <summary>
    /// 水槽のクラゲ1体 (系列2 Phase C-7/8)。
    ///
    /// 【真実側】粒子と違い、クラゲは位置が力学の状態である。
    /// 決定論の対象に入れ、**固定ティック（神経 40Hz）**で進める。
    /// 流れ場は 20Hz なので、流れ1ティックにつき神経2ステップの整数比になる。
    ///
    /// 【流れとの結合は一方向】流れはクラゲを運ぶが、クラゲは流れに書き戻さない。
    /// 航跡場（生物が流れに書き込む場）は後の段。
    /// </summary>
    public sealed class AquariumJellyfish : MonoBehaviour
    {
        /// <summary>神経を進める固定周波数 (Hz)。1拍動 = 40 ステップ = 1.0 秒。</summary>
        const float k_NeuralHz = 40f;

        /// <summary>
        /// 実機で切り替える傘径 (m)。ミズクラゲの実物は 10〜25cm。
        /// 実部屋に浮かべる以上、見え方に直結するので振れるようにする。
        /// </summary>
        public static readonly float[] BellDiameterChoices = { 0.10f, 0.15f, 0.25f };

        /// <summary>
        /// 壁面での反発の強さ (m/s)。実機で振る。
        ///
        /// 【なぜ振れる形にするか】役割は「壁に沿って離れていく」ことで、
        /// **弾かれるように見えると不自然**になる。適正値は見た目で決めるしかない。
        /// 遊泳は 0.04 m/s なので、それを下回ると押し続ける推力に勝てず張り付きが残る。
        /// 0.00 は反発なし（軸ごとの拒否だけ）で、比較のために置いてある。
        /// </summary>
        public static readonly float[] WallRepelChoices = { 0.10f, 0.20f, 0.00f, 0.05f };

        [SerializeField] AquariumFlow m_Flow;
        [SerializeField] int m_BellIndex = 1;
        [SerializeField] int m_RepelIndex;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }

        public int BellIndex => m_BellIndex;
        public int RepelIndex => m_RepelIndex;
        public float WallRepelSpeed => WallRepelChoices[m_RepelIndex];
        public float BellDiameter => BellDiameterChoices[m_BellIndex];

        /// <summary>クラゲ本体。焼き込みが済むまで null。</summary>
        public Jellyfish Body { get; private set; }

        /// <summary>直近の流速 (m/s)。パネルとログ用。</summary>
        public Vector3 FlowAt { get; private set; }

        /// <summary>
        /// 直近1拍動(1.0秒)の**平均**遊泳速度 (m/s) と、流れに運ばれた平均速度 (m/s)。
        ///
        /// 【瞬時値では判定できない】<see cref="Jellyfish.SwimSpeed"/> は拍動の位相で
        /// 0.19〜0.0004 m/s まで振れる。1秒ごとの標本化は拍動周期と同じなので
        /// 位相が固定され、実機ログには常に減衰しきった値だけが載っていた。
        /// **傘径と流速の比を判定するには平均が要る**ので、変位の差分で測る。
        /// </summary>
        public float SwimSpeedMean { get; private set; }
        public float DriftSpeedMean { get; private set; }

        /// <summary>遊泳が流れに勝っているか。1 を超えていれば自力が勝つ。</summary>
        public float SwimToFlowRatio =>
            DriftSpeedMean > 1e-6f ? SwimSpeedMean / DriftSpeedMean : 0f;

        float m_Accumulator;

        // 平均を取る窓（1拍動ぶん）の始点
        long m_WindowStep;
        float m_WinSwimX, m_WinSwimZ, m_WinDriftX, m_WinDriftY, m_WinDriftZ;

        void Update()
        {
            var field = m_Flow != null ? m_Flow.Field : null;
            if (field == null)
            {
                // 【焼き直しでクラゲを作り直さない】セルサイズの切り替えは
                // Field を一度 null にする。以前はここで Body も捨てていたため、
                // 2026-08-16 のセッションでは 7 分間に **36 回**湧き直していた
                // （うち利用者の傘径操作は 3 回だけ）。毎回スポーン地点へ戻るので
                // 「生きて見えるか」を時間をかけて見ることができない。
                // 格子が変わっても部屋座標は同じなので、位置はそのまま持ち越せる
                return;
            }
            if (Body == null)
            {
                Spawn(field);
                return;
            }

            m_Accumulator += Time.deltaTime;
            float step = 1f / k_NeuralHz;
            int stepped = 0;
            while (m_Accumulator >= step && stepped < 8)
            {
                field.SampleVelocity(Body.X, Body.Y, Body.Z,
                    out float vx, out float vy, out float vz);
                FlowAt = new Vector3(vx, vy, vz);
                Body.Step(step, vx, vy, vz);
                KeepInsideTank(field);
                m_Accumulator -= step;
                stepped++;
            }

            UpdateMeanSpeeds();
        }

        /// <summary>窓（1拍動）が閉じたら平均を出し直す。</summary>
        void UpdateMeanSpeeds()
        {
            long elapsed = Body.StepCount - m_WindowStep;
            if (elapsed < Body.PulsePeriodTicks) return;

            float seconds = elapsed / k_NeuralHz;
            float sx = Body.SwimPathX - m_WinSwimX, sz = Body.SwimPathZ - m_WinSwimZ;
            float dx = Body.DriftPathX - m_WinDriftX;
            float dy = Body.DriftPathY - m_WinDriftY;
            float dz = Body.DriftPathZ - m_WinDriftZ;

            SwimSpeedMean = Mathf.Sqrt(sx * sx + sz * sz) / seconds;
            DriftSpeedMean = Mathf.Sqrt(dx * dx + dy * dy + dz * dz) / seconds;

            m_WindowStep = Body.StepCount;
            m_WinSwimX = Body.SwimPathX; m_WinSwimZ = Body.SwimPathZ;
            m_WinDriftX = Body.DriftPathX; m_WinDriftY = Body.DriftPathY;
            m_WinDriftZ = Body.DriftPathZ;
        }

        /// <summary>
        /// 壁の反発の強さを切り替える。**位置は引き継ぐ**（湧かせ直さない）。
        /// 張り付いている個体がその場で離れるかを見たいので、作り直すと確認にならない。
        /// </summary>
        public void CycleWallRepel()
        {
            m_RepelIndex = (m_RepelIndex + 1) % WallRepelChoices.Length;
            if (Body != null)
            {
                var p = JellyParams.Default;
                p.BellDiameter = BellDiameter;
                p.WallRepelSpeed = WallRepelSpeed;
                var moved = new Jellyfish(p, Body.X, Body.Y, Body.Z, m_Flow.Field.Grid);
                Body = moved;
                m_WindowStep = 0;
                m_WinSwimX = m_WinSwimZ = 0f;
                m_WinDriftX = m_WinDriftY = m_WinDriftZ = 0f;
                SwimSpeedMean = DriftSpeedMean = 0f;
            }
            Debug.Log($"[Aquarium] 壁の反発を {WallRepelSpeed:F2} m/s に切り替え");
        }

        /// <summary>傘径を切り替える。位置は引き継がず、湧かせ直す。</summary>
        public void CycleBellDiameter()
        {
            m_BellIndex = (m_BellIndex + 1) % BellDiameterChoices.Length;
            Body = null;
            Debug.Log($"[Aquarium] 傘径を {BellDiameter * 100f:F0}cm に切り替え");
        }

        void Spawn(FlowField field)
        {
            var g = field.Grid;
            // 部屋の真ん中あたりの水セルへ置く
            float cx = g.OriginX + g.Width * g.CellSize * 0.5f;
            float cy = g.OriginY + g.Height * g.CellSize * 0.55f;
            float cz = g.OriginZ + g.Depth * g.CellSize * 0.5f;

            var p = JellyParams.Default;
            p.BellDiameter = BellDiameter;
            p.WallRepelSpeed = WallRepelSpeed;
            Body = new Jellyfish(p, cx, cy, cz, g);

            m_WindowStep = 0;
            m_WinSwimX = m_WinSwimZ = 0f;
            m_WinDriftX = m_WinDriftY = m_WinDriftZ = 0f;
            SwimSpeedMean = DriftSpeedMean = 0f;

            Debug.Log($"[Aquarium] クラゲを投入: 傘 {BellDiameter * 100f:F0}cm / " +
                $"目標遊泳 {p.SwimSpeed:F3}m/s / 拍動 {p.PulsePeriodTicks / k_NeuralHz:F2}秒 / " +
                $"換算係数 {Body.SpeedScale:F4} / 位置({cx:F2}, {cy:F2}, {cz:F2})");
        }

        /// <summary>
        /// 固体セルや格子の外へ出たら、水のある側へ押し戻す。
        ///
        /// 【なぜ要るか】クラゲは流れに運ばれるが、境界のランプは流れを
        /// 壁に沿わせるだけで**貫通を厳密に禁止してはいない**
        /// （|u·n|/|u| = 0.14 で 0 ではない）。積分の刻みによっては壁に入りうる。
        /// 壁にめり込んだクラゲは「生きて見えるか」の判定を壊すので、
        /// 表示上の破綻を防ぐ最小限の処理を入れる。
        /// </summary>
        void KeepInsideTank(FlowField field)
        {
            var g = field.Grid;
            float margin = BellDiameter * 0.5f;
            float minX = g.OriginX + margin, maxX = g.OriginX + g.Width * g.CellSize - margin;
            float minY = g.OriginY + margin, maxY = g.OriginY + g.Height * g.CellSize - margin;
            float minZ = g.OriginZ + margin, maxZ = g.OriginZ + g.Depth * g.CellSize - margin;

            float x = Mathf.Clamp(Body.X, minX, maxX);
            float y = Mathf.Clamp(Body.Y, minY, maxY);
            float z = Mathf.Clamp(Body.Z, minZ, maxZ);

            // 【上へ逃がす処理を消した】固体セルに入ったら1セルぶん上へ押していたが、
            // 壁や家具の中では上へ押しても壁の中のままなので、毎ステップ登り続けて
            // 天井に到達する。2026-08-16 の実機ログに72秒間貼り付いた記録が残っている。
            // 固体セルへは JellyBoundary.ClampMove で入らせないので、逃がす必要もない。
            // ここに残すのは格子の外へ出さないクランプだけ（焼き直しで格子が変わる場合の保険）
            if (x != Body.X || y != Body.Y || z != Body.Z) Body.Teleport(x, y, z);
        }
    }
}
