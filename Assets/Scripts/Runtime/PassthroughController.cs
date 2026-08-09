using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace BlockField
{
    /// <summary>
    /// パススルー（現実映像）の有効／無効を1箇所で切り替える (Demo 4.5b VRモードの下ごしらえ)。
    ///
    /// 【なぜ集約するか】パススルーの成立条件は複数のコンポーネントに散っている:
    /// カメラの Clear Flags と背景色（アルファ0でないと現実映像が合成されない）、
    /// ARCameraBackground（現実映像の描画）、AROcclusionManager（現実物体による遮蔽）。
    /// これらがシーン生成コード・権限フロー・カメラ設定に分散していると、
    /// VRモードの切り替えで抜けが出る。<see cref="SetPassthroughEnabled"/> に集約する。
    ///
    /// 本コンポーネントは**現時点で挙動を変えない**。起動時に既定値
    /// （パススルー有効）を適用するだけで、切り替えの利用は Demo 4.5b で行う。
    ///
    /// 【MR合成制約（CLAUDE.md）】パススルー有効時は背景のアルファを0にする。
    /// HDR も無効でなければならない（B10G11R11 はアルファを持たない）。
    /// これは URP アセット側の設定なのでここでは扱わない。
    /// </summary>
    public sealed class PassthroughController : MonoBehaviour
    {
        [SerializeField] Camera m_Camera;
        [SerializeField] ARCameraBackground m_CameraBackground;
        [SerializeField] AROcclusionManager m_OcclusionManager;

        /// <summary>
        /// VRモードの背景色（不透明）。パススルー無効時のみ使う。
        /// 暗い青灰＝夜空／洞窟のイメージ。真っ黒にしないのは、ブロックの
        /// 底面（明度0.6）や AO の効いた面と背景が同化しないようにするため。
        /// </summary>
        [SerializeField] Color m_VrBackgroundColor = new Color(0.055f, 0.075f, 0.115f, 1f);

        public Camera targetCamera { get => m_Camera; set => m_Camera = value; }
        public ARCameraBackground cameraBackground { get => m_CameraBackground; set => m_CameraBackground = value; }
        public AROcclusionManager occlusionManager { get => m_OcclusionManager; set => m_OcclusionManager = value; }
        public Color vrBackgroundColor { get => m_VrBackgroundColor; set => m_VrBackgroundColor = value; }

        /// <summary>パススルーが有効か。</summary>
        public bool IsPassthroughEnabled { get; private set; } = true;

        // VRへ入る直前のオクルージョン状態（MRへ戻すときに復元する）
        bool m_OcclusionWasEnabled;
        bool m_OcclusionRestorePending;

        void Awake()
        {
            // 既定はパススルー有効（従来どおりの挙動）
            SetPassthroughEnabled(true);
        }

        /// <summary>
        /// パススルーの有効／無効を切り替える。**カメラ側の切り替えはすべてここに集約する**。
        ///
        /// 有効: 背景は透明（アルファ0）で現実映像が合成される。ARCameraBackground 有効。
        /// 無効: 背景は不透明の VR 背景色。ARCameraBackground を止めて現実映像を描かない。
        ///
        /// オクルージョンはパススルー無効時には意味を持たないどころか有害である
        /// （遮蔽する現実物体が描かれないのに深度だけが効き、仮想物体が消える）。
        /// 無効化する際に元の状態を覚えておき、MRへ戻すときに復元する
        /// （権限が下りていなければ元々 false なので、false のまま戻る）。
        /// </summary>
        public void SetPassthroughEnabled(bool enabled)
        {
            IsPassthroughEnabled = enabled;

            if (m_Camera != null)
            {
                m_Camera.clearFlags = CameraClearFlags.SolidColor;
                m_Camera.backgroundColor = enabled
                    ? new Color(0f, 0f, 0f, 0f)   // アルファ0 = パススルーと合成される
                    : m_VrBackgroundColor;
            }

            if (m_CameraBackground != null)
            {
                m_CameraBackground.enabled = enabled;
            }

            if (m_OcclusionManager != null)
            {
                if (!enabled)
                {
                    // VRへ: 現在の状態を覚えてから止める
                    m_OcclusionWasEnabled = m_OcclusionManager.enabled;
                    m_OcclusionManager.enabled = false;
                }
                else if (m_OcclusionRestorePending)
                {
                    // MRへ戻る: VRに入る前の状態へ復元する
                    m_OcclusionManager.enabled = m_OcclusionWasEnabled;
                }
            }
            m_OcclusionRestorePending = !enabled;

            Debug.Log($"[Passthrough] {(enabled ? "有効（MR）" : "無効（VR）")}: " +
                $"clear=SolidColor bg={(m_Camera != null ? m_Camera.backgroundColor.ToString() : "カメラ未設定")} " +
                $"cameraBackground={(m_CameraBackground != null ? m_CameraBackground.enabled.ToString() : "未設定")}");
        }
    }
}
