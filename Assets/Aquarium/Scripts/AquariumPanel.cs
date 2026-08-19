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

            // 【1行を短く保つ】項目を足し続けた結果、行が右へ伸びて視野から
            // 見切れ、**現在値が読めないので比較そのものが成立しなかった**
            // （2026-08-18 の実機で沈降比の判定が測れなかった）。
            // 2行に割り、ラベルを詰める。1行 62 文字を上限の目安とする
            return $"Jelly[R-B]:{body.BellDiameter * 100f:F0}cm({m_Jelly.BellIndex + 1}/3)" +
                $" 傾き{m_Jelly.TiltDegrees:F0}°" +
                $" 拍動{(m_Jelly.Pulsing ? "ON" : "**停止**")}" +
                $" 刺激{m_Jelly.StimulusCount}" +
                $" 侵害{body.NociceptedCells}/16({body.ContactMask:X4})" +
                $" p{body.PulseCount}\n" +
                $"沈降[R-Grip]{m_Jelly.SinkRatio:P0}({m_Jelly.SinkIndex + 1}/{AquariumJellyfish.SinkChoices.Length})" +
                $" 復元[R-Trig]{m_Jelly.RightingGain:F1}({m_Jelly.RightingIndex + 1}/{AquariumJellyfish.RightingChoices.Length})" +
                $" 泳{m_Jelly.SwimSpeedMean:F3} 実{m_Jelly.ActualSpeedMean:F3}" +
                (m_Jelly.DriftSpeedMean > 1e-4f
                    ? $" 流{m_Jelly.DriftSpeedMean:F3}"
                    : " [止水]");
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
                m_Text.text = $"{BuildStamp.Text}  アンカー={AnchorIdentity()}\nFPS: {fps:F1}\n{(m_Flow != null ? m_Flow.Status : "未配線")}";
                return;
            }

            var g = field.Grid;
            var preset = m_Particles != null ? m_Particles.Current : default;

            // 【切り替えられる値は現在値を必ず出す】2026-08-16 のセッションで
            // 目標流速とセルサイズの現在値がパネルに無く、**自分がどの段階にいるか
            // 分からないまま比較する**ことになった。ボタン名も実物と食い違っていた
            // （表示は B、実際は Y）。段階は「今/全体」の形で出す
            m_Text.text =
                // 【何が動いているかを画面に出す】シーンを取り違えたまま実機セッションを
                // 始めた件の再発防止（2026-08-19）。パッケージ名は共通なので
                // 実機側には他に判別する手段が無い
                $"{BuildStamp.Text}  アンカー={AnchorIdentity()}\n" +
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
    
        /// <summary>
        /// **いま基準にしているアンカーの識別子**。刻印（シーン名・ブランチ・HEAD）と
        /// 並べて出す。将来 外部センサを登録すると登録座標はアンカーに紐づくので、
        /// 部屋の再走査や Guardian のリセットでアンカーが変わったことに
        /// **その場で気づける**必要がある。静かにずれるのが一番まずい。
        /// 同じ値をログにも出している（<see cref="AquariumFlow"/>）。
        /// </summary>
        string AnchorIdentity()
        {
            var origin = m_Flow != null ? m_Flow.origin : null;
            if (origin == null) return "(未配線)";
            if (string.IsNullOrEmpty(origin.AnchorGuid)) return "(未確定)";
            // 先頭8文字で足りる。長い GUID を出すと行が伸びて見切れる
            return origin.AnchorGuid.Substring(0, 8);
        }
}
}
