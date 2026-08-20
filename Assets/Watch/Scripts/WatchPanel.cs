using System.Collections.Generic;
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
    /// 【5行に絞る】**数値はログから後で読む。装着中に見るのは「そう見えるか」だけ。**
    /// 9行あったが箱は6行ぶんしか無く、項目を足すたびに下が押し出されていた。
    /// 凡例・描画数・格子と確からしさの3件が装着中に確認不能だったのはこれが原因で、
    /// **足すことが壊すことになっていた**（2026-08-20）。
    /// 行が箱に収まることは `WatchPanelFitsTests` が固定している。
    ///
    /// 【警告は1行に集約する】検査を足しても**行数が増えない形**にする。
    /// 異常が無ければ「異常なし」、あればその内容。多すぎるときは件数。
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

            if (m_Field != null) m_Field.FrameMs = m_Smoothed * 1000f;

            var f = m_Field != null ? m_Field.Field : null;
            string stamp = $"{BuildStamp.Text}  アンカー={AnchorIdentity()}";

            if (f == null)
            {
                m_Text.text = $"{stamp}\nいま: 場を作れていない\n段 —\n" +
                    $"凡例 黄=足元 / 青=測れている床 / 灰=測れていない床\n" +
                    $"警告 {(m_Field != null ? m_Field.Status : "未配線")}";
                return;
            }

            string state = f.Coverage == L0Coverage.None
                ? $"**測れていない**（{m_Head?.LastLabel}）床が灰"
                : $"測れている（{m_Head?.LastLabel}）";

            m_Text.text =
                $"{stamp}\n" +
                $"いま: {state}\n" +
                $"段[左グリップ] {m_View?.CurrentName}\n" +
                $"凡例 黄=足元 / 青=測れている床 / 灰=測れていない床 / 無=部屋の外\n" +
                $"警告 {Warnings()}";
        }

        /// <summary>
        /// **警告を1行に集約する。** 検査を足しても行数が増えない形。
        /// 異常が無ければ「異常なし」。多すぎるときは件数だけ出す
        /// （**黙って落とさない** — 何件あるかは必ず見える）。
        /// </summary>
        string Warnings()
        {
            var w = new List<string>();
            if (m_Field != null)
            {
                if (m_Field.RecordState != "記録中") w.Add(m_Field.RecordState);
                // 格子は専用行を持たず、**新規作成のときだけ**ここに出る
                if (m_Field.GridSource == "新規作成") w.Add("格子を新規作成");
                if (m_Field.Replaying) w.Add($"再生中 {m_Field.ReplayCursor}/{m_Field.ReplayCount}");
            }
            if (m_View != null && m_View.Truncated) w.Add("描画を切り捨て");

            if (w.Count == 0) return "異常なし";
            if (w.Count <= 2) return string.Join(" / ", w);
            return $"{w.Count}件: {w[0]} ほか";
        }
    }
}
