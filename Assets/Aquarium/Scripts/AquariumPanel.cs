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
        [SerializeField] Text m_Text;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }
        public FlowParticleView particles { get => m_Particles; set => m_Particles = value; }
        public Text text { get => m_Text; set => m_Text = value; }

        float m_Smoothed;
        float m_Next;

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

            m_Text.text =
                $"FPS: {fps:F1}   Tick: {m_Flow.TickMs:F2}ms   Bake: {m_Flow.BakeMs}ms\n" +
                $"Cell[A]: {m_Flow.CellSize * 100f:F1}cm   Grid: {g.Width}x{g.Height}x{g.Depth} = {g.CellCount}\n" +
                $"Solid: {m_Flow.SolidCells}   MaxSpeed: {m_Flow.MaxSpeed:F4}   t={field.TickCount}\n" +
                $"View[B]: {preset.Name}   n={(m_Particles != null ? m_Particles.DrawnParticles : 0)}   " +
                $"size={preset.Size * 100f:F1}cm   bright={preset.Brightness:F2}";
        }
    }
}
