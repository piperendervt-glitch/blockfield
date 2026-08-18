using UnityEngine;
using UnityEngine.UI;

namespace BlockField.Aquarium
{
    /// <summary>
    /// 水槽モードのデバッグパネル (系列2 Phase B)。
    ///
    /// 【パネルに出す値はログにも出す】装着中のユーザーは数値を読み上げられず、
    /// セッション後に転記もできない。パネルにしか無い数値は事実上取得できない
    /// （CLAUDE.md。同じ漏れを2回起こしている）。
    /// ここに出す項目は <see cref="AquariumFlow"/> が毎秒ログへ出しているものと同じ。
    ///
    /// 【FPS は先頭行】判定に使う最重要指標なので、視野の中心に最も近い位置に置く
    /// （Demo 8.5 でグラフに隠れて読めなかった件の再発防止）。
    /// </summary>
    public sealed class AquariumPanel : MonoBehaviour
    {
        const float k_RefreshInterval = 0.5f;

        [SerializeField] AquariumFlow m_Flow;
        [SerializeField] FlowParticleView m_Particles;
        [SerializeField] AquariumJellyfish m_Jelly;
        [SerializeField] AquariumDebugView m_Debug;
        [SerializeField] Text m_Text;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }
        public FlowParticleView particles { get => m_Particles; set => m_Particles = value; }
        public AquariumJellyfish jelly { get => m_Jelly; set => m_Jelly = value; }
        public AquariumDebugView debugView { get => m_Debug; set => m_Debug = value; }
        public Text text { get => m_Text; set => m_Text = value; }

        float m_Smoothed;
        float m_Next;

        /// <summary>
        /// クラゲの行。**遊泳と流れの比**を出すのが要点で、
        /// 「流されている」のか「泳いでいる」のかを数値で見分けるため
        /// （比が 1 を超えていれば自力が勝っている）。
        /// </summary>
        string JellyLine()
        {
            var body = m_Jelly != null ? m_Jelly.Body : null;
            if (body == null) return "Jelly[R-B]: 未投入";

            // 【平均で出す】瞬時値は拍動の位相で 0.19〜0.0004 m/s まで振れるので、
            // パネルで読んでも比の判定に使えない。直近1拍動の平均を出す
            return $"Jelly[R-B]: {body.BellDiameter * 100f:F0}cm " +
                $"({m_Jelly.BellIndex + 1}/{AquariumJellyfish.BellDiameterChoices.Length})" +
                // 【姿勢を装着中に読めるように】K2 で軸が状態になったので、
                // 傾きが分からないと「上下に泳げているか」を判定できない
                $"   傾き={m_Jelly.TiltDegrees:F1}°" +
                $"   復元[R-Trig]={m_Jelly.RightingGain:F1}" +
                $"({m_Jelly.RightingIndex + 1}/{AquariumJellyfish.RightingChoices.Length})" +
                $"   刺激[L-Trig]={m_Jelly.StimulusCount}回" +
                $"   沈降[R-Trig長]={m_Jelly.SinkRatio:P0}" +
                $"({m_Jelly.SinkIndex + 1}/{AquariumJellyfish.SinkChoices.Length})" +
                $"   拍動[L-Trig長]={(m_Jelly.Pulsing ? "ON" : "**停止**")}" +
                $"   repel[R-Grip]={m_Jelly.WallRepelSpeed:F2}" +
                $"({m_Jelly.RepelIndex + 1}/{AquariumJellyfish.WallRepelChoices.Length})" +
                $"   pulse={body.PulseCount}   swim={m_Jelly.SwimSpeedMean:F3}" +
                $"   flow={m_Jelly.DriftSpeedMean:F3}   実移動={m_Jelly.ActualSpeedMean:F3}"
                // 止水（流速 0.00）では比が定義できない。0.00 と出すと
                // 「自力で進んでいない」と読めてしまうので、判定の言葉で出す
                + (m_Jelly.DriftSpeedMean > 1e-4f
                    ? $"   swim/flow={m_Jelly.SwimToFlowRatio:F2}"
                    : "   [止水: 動く分はすべて自力]");
        }

        /// <summary>
        /// デバッグ表示の行。**焼き込んだ壁が現実の壁と重なっているか**を見るための表示で、
        /// いま何を描いているかが分からないと判定にならない。
        /// </summary>
        string DebugLine()
        {
            if (m_Debug == null) return "Debug[L-Grip]: 未配線";
            int i = (int)m_Debug.Current;
            // 【何を確かめる段かを画面に出す】装着中は色の意味も手順も覚えていられない。
            // 「水色がどっちだったか」を思い出せないまま見ても判定にならない（2026-08-16）
            string anchor = m_Debug.space == null ? "  [アンカー未配線]"
                : m_Debug.space.IsReady ? "" : "  [アンカー未確定: 描画停止中]";
            return $"Debug[L-Grip]: {m_Debug.CurrentName}{anchor} " +
                $"({i + 1}/{AquariumDebugView.ModeNames.Length})   n={m_Debug.DrawnCells}\n" +
                $"  → {AquariumDebugView.ModeHints[i]}";
        }

        void Update()
        {
            m_Smoothed = Mathf.Lerp(m_Smoothed, Time.unscaledDeltaTime, 0.05f);
            if (Time.unscaledTime < m_Next || m_Text == null) return;
            m_Next = Time.unscaledTime + k_RefreshInterval;

            float fps = m_Smoothed > 0.0001f ? 1f / m_Smoothed : 0f;
            var field = m_Flow != null ? m_Flow.Field : null;

            if (field == null)
            {
                m_Text.text = $"FPS: {fps:F1}\n{(m_Flow != null ? m_Flow.Status : "未配線")}";
                return;
            }

            var g = field.Grid;
            var preset = m_Particles != null ? m_Particles.Current : default;

            // 【切り替えられる値は現在値を必ず出す】2026-08-16 のセッションで
            // 目標流速とセルサイズの現在値がパネルに無く、**自分がどの段階にいるか
            // 分からないまま比較する**ことになった。ボタン名も実物と食い違っていた
            // （表示は B、実際は Y）。段階は「今/全体」の形で出す
            m_Text.text =
                $"FPS: {fps:F1}   Tick: {m_Flow.TickMs:F2}ms   Bake: {m_Flow.BakeMs}ms\n" +
                $"Speed[R-A]: {m_Flow.TargetSpeed:F3} m/s ({m_Flow.SpeedIndex + 1}/{AquariumFlow.TargetSpeedChoices.Length})" +
                $"  = {m_Flow.TargetSpeed / 72f * 100f:F2}cm/frame   Max: {m_Flow.MaxSpeed:F3}\n" +
                $"Cell[L-X]: {m_Flow.CellSize * 100f:F1}cm ({m_Flow.CellSizeIndex + 1}/{AquariumFlow.CellSizeChoices.Length})" +
                $"   {g.Width}x{g.Height}x{g.Depth}={g.CellCount}" +
                $"   Solid: {m_Flow.SolidCells} (壁{m_Flow.MeshSolidCells}+縁{m_Flow.BorderSolidCells})\n" +
                $"View[L-Y]: {preset.Name} ({m_Particles.PresetIndex + 1}/{FlowParticleView.Presets.Length})" +
                $"   n={m_Particles.DrawnParticles}   size={preset.Size * 100f:F1}cm   t={field.TickCount}\n" +
                JellyLine() + "\n" + DebugLine();
        }
    }
}
