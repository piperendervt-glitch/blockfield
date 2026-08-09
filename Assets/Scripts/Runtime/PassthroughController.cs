using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace BlockField
{
    /// <summary>
    /// パススルー（現実映像）の有効／無効を1箇所で切り替える (Demo 4.5b VRモードの下ごしらえ)。
    ///
    /// 【なぜ集約するか】パススルーの成立条件は複数のコンポーネントに散っている:
    /// ARCameraManager（パススルーレイヤーの生成・破棄）、カメラの Clear Flags と
    /// 背景色のアルファ、ARCameraBackground、AROcclusionManager（現実物体による遮蔽）。
    /// これらがシーン生成コード・権限フロー・カメラ設定に分散していると、
    /// VRモードの切り替えで抜けが出る。<see cref="SetPassthroughEnabled"/> に集約する。
    ///
    /// 【パススルーが消える本当の条件 — 2026-08-09 の実機で判明】
    /// Meta Quest のパススルーは**カメラ映像の描画ではなく OpenXR のコンポジション
    /// レイヤー**である。com.unity.xr.meta-openxr の MetaOpenXRCameraSubsystem は
    /// Provider.Start() で CreatePassthroughLayer()、Stop() で DestroyPassthroughLayer()
    /// を呼ぶ。つまり**レイヤーを止められるのは ARCameraManager の enabled だけ**で、
    /// ARCameraBackground を切っても背景色のアルファを1にしても、レイヤーは
    /// 出続ける（アプリのレイヤーが不透明なら隠れるはずだが、実機では消えなかった）。
    /// 初版は ARCameraBackground と背景色だけを触っていたため VRモードが成立しなかった。
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
        [SerializeField] ARCameraManager m_CameraManager;
        [SerializeField] ARCameraBackground m_CameraBackground;
        [SerializeField] AROcclusionManager m_OcclusionManager;

        /// <summary>
        /// VRモードの背景色（不透明）。パススルー無効時のみ使う。
        /// 暗い青灰＝夜空／洞窟のイメージ。真っ黒にしないのは、ブロックの
        /// 底面（明度0.6）や AO の効いた面と背景が同化しないようにするため。
        /// </summary>
        [SerializeField] Color m_VrBackgroundColor = new Color(0.055f, 0.075f, 0.115f, 1f);

        public Camera targetCamera { get => m_Camera; set => m_Camera = value; }
        /// <summary>パススルーレイヤーの生成・破棄を握る本体。これを切らないと現実映像は消えない。</summary>
        public ARCameraManager cameraManager { get => m_CameraManager; set => m_CameraManager = value; }
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

            // ここが本命。ARCameraManager を切ると Meta OpenXR のカメラサブシステムが
            // Stop() され、DestroyPassthroughLayer() でコンポジションレイヤーごと消える。
            // 戻すと Start() で作り直される
            if (m_CameraManager != null)
            {
                m_CameraManager.enabled = enabled;
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

            // 実機で「どれが適用され、どれが未設定だったか」を1行で切り分けられるようにする
            Debug.Log($"[Passthrough] {(enabled ? "有効（MR）" : "無効（VR）")}: " +
                $"cameraMgr={(m_CameraManager != null ? m_CameraManager.enabled.ToString() : "未設定")} " +
                $"camBg={(m_CameraBackground != null ? m_CameraBackground.enabled.ToString() : "未設定")} " +
                $"clear={(m_Camera != null ? m_Camera.clearFlags.ToString() : "未設定")} " +
                $"bgA={(m_Camera != null ? m_Camera.backgroundColor.a.ToString("F2") : "-")} " +
                $"hdr={(m_Camera != null ? m_Camera.allowHDR.ToString() : "-")} " +
                $"occl={(m_OcclusionManager != null ? m_OcclusionManager.enabled.ToString() : "未設定")}");
        }
    }
}
