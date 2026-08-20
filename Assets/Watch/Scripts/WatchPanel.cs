using BlockField.Aquarium;
using BlockField.SimCore.Watch;
using UnityEngine;
using UnityEngine.UI;

namespace BlockField.Watch
{
    /// <summary>
    /// L0 のデバッグパネル。
    ///
    /// 【パネルに出す値はログにも出す】装着中のユーザーは数値を読み上げられず、
    /// セッション後に転記もできない。同じ漏れを3回起こしているので、
    /// `WatchPanelLogParityTests` がこの対応をテストで固定している。
    ///
    /// 【刻印を先頭に】シーン名・ブランチ・HEAD・**アンカー識別子**。
    /// 外部センサの登録座標はアンカーに紐づくので、アンカーが変わったことに
    /// その場で気づける必要がある。静かにずれるのが一番まずい。
    /// </summary>
    public sealed class WatchPanel : MonoBehaviour
    {
        const float k_RefreshInterval = 0.5f;

        [SerializeField] WatchField m_Field;
        [SerializeField] HeadPoseProducer m_Head;
        [SerializeField] WatchSpaceRenderer m_Space;
        [SerializeField] WatchView m_View;
        [SerializeField] Text m_Text;

        public WatchField field { get => m_Field; set => m_Field = value; }
        public HeadPoseProducer head { get => m_Head; set => m_Head = value; }
        public WatchSpaceRenderer space { get => m_Space; set => m_Space = value; }
        public WatchView view { get => m_View; set => m_View = value; }
        public Text text { get => m_Text; set => m_Text = value; }

        float m_Smoothed;
        float m_Next;

        /// <summary>アンカー識別子。長い GUID は行が伸びて見切れるので先頭8文字。</summary>
        string AnchorIdentity()
        {
            string guid = m_Space != null ? m_Space.AnchorGuid : null;
            if (m_Space == null) return "(未配線)";
            return string.IsNullOrEmpty(guid) ? "(未確定)" : guid.Substring(0, 8);
        }

        void Update()
        {
            m_Smoothed = Mathf.Lerp(m_Smoothed, Time.unscaledDeltaTime, 0.05f);
            if (Time.unscaledTime < m_Next || m_Text == null) return;
            m_Next = Time.unscaledTime + k_RefreshInterval;

            float fps = m_Smoothed > 0.0001f ? 1f / m_Smoothed : 0f;
            var f = m_Field != null ? m_Field.Field : null;
            var ticker = m_Field != null ? m_Field.Ticker : null;

            string stamp = $"{BuildStamp.Text}  アンカー={AnchorIdentity()}";

            if (f == null || ticker == null)
            {
                m_Text.text = $"{stamp}\nFPS {fps:F1}\n" +
                    $"{(m_Field != null ? m_Field.Status : "未配線")}";
                return;
            }

            var pos = m_Head != null ? m_Head.LastRoomPosition : Vector3.zero;

            // 【初めて見た人が意味を取れるか】前回のセッションで
            // 「描画 n=実際/つもり」「喪失で何を見るか」が伝わらなかった。
            // 凡例を出し、食い違ったときだけ印を出す
            string draw = m_View == null ? "未配線"
                : m_View.Truncated
                    ? $"{m_View.DrawnCells}/{m_View.WantedCells} セル  **切り捨て**"
                    : $"{m_View.DrawnCells} セル";

            string tracking = f.Coverage == L0Coverage.None
                ? $"**測れていない**（{m_Head?.LastLabel}）床が灰になる"
                : $"測れている（{m_Head?.LastLabel}）";

            m_Text.text =
                $"{stamp}\n" +
                $"FPS {fps:F1}   ティック {ticker.Tick}   遅延 {ticker.Backlog * 1000f:F0}ms" +
                $"   未装着で止まった分 {ticker.DroppedTicks}\n" +
                $"いま: {tracking}\n" +
                $"頭の位置 ({pos.x:F2}, {pos.y:F2}, {pos.z:F2}) m\n" +
                $"測れている床 {f.CoveredCells} セル / 部屋の床 {f.ScannedCells} セル" +
                $" = {m_Field.FloorArea:F1} m2\n" +
                $"段[左グリップ] {m_View?.CurrentName}   描画 {draw}\n" +
                $"格子 {m_Field.GridSource} {m_Field.Grid.Width}x{m_Field.Grid.Depth}" +
                $"   L0b の確からしさ {(m_Head != null ? m_Head.LastConfidence : 0f):F2}\n" +
                $"凡例 黄=足元 / 青=測れている床 / 灰=測れていない床 / 何も無い=部屋の外\n" +
                $"{(m_Field.Replaying ? $"**再生中** {m_Field.ReplayCursor}/{m_Field.ReplayCount}" : "実時間")}" +
                $"  [右A で切替]  再生元 {m_Field.ReplaySource}  記録 {m_Field.RecordState}";
        }
    }
}
