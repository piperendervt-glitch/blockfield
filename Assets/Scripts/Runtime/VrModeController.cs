using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockField
{
    /// <summary>
    /// MR ⇄ VR のモード切替 (Demo 4.5b V3)。
    ///
    /// MRモード: パススルー有効。現実の部屋の上に地形と生態系が乗る（Demo 4.5 まで）。
    /// VRモード: パススルー無効。部屋の外殻（壁・天井・床下）をボクセルで描き、
    ///           世界が丸ごとブロックで閉じる。公開・録画用。
    ///
    /// 【割り当て: 左手Xボタン】
    /// 事前登録では「右手A長押し1秒」だったが、左手X（primaryButton）に変更した。
    /// 理由: 右手Aの短押しはシード巡回に割り当て済みで、長押しと共存させると
    /// 「離すまで短押しか長押しか確定できない」ため、既存機能に1秒の遅延が入る。
    /// 左手Xは Demo 4.5 G7 で箱庭の表示トグルを廃止したときに空いている。
    ///
    /// 【位置ずれ】地形・生態系・外殻はすべて TerrainField.TerrainRoot 配下（アンカー相対）
    /// なので、モード切替は表示の ON/OFF だけで座標には一切触れない。
    /// </summary>
    public sealed class VrModeController : MonoBehaviour
    {
        /// <summary>切替の連打防止（秒）。</summary>
        const float k_ToggleCooldown = 0.5f;

        [SerializeField] PassthroughController m_Passthrough;
        [SerializeField] RoomShellView m_Shell;

        public PassthroughController passthrough { get => m_Passthrough; set => m_Passthrough = value; }
        public RoomShellView shell { get => m_Shell; set => m_Shell = value; }

        /// <summary>VRモードか（パネル表示用）。</summary>
        public bool IsVrMode { get; private set; }

        InputAction m_ToggleAction;
        bool m_ToggleRequested;
        bool m_Applied;
        float m_LastToggleTime = float.NegativeInfinity;

        void Awake()
        {
            m_ToggleAction = new InputAction("VrModeToggle", InputActionType.Button,
                "<XRController>{LeftHand}/primaryButton");
            m_ToggleAction.performed += OnTogglePerformed;
        }

        void OnDestroy()
        {
            m_ToggleAction.performed -= OnTogglePerformed;
            m_ToggleAction.Dispose();
        }

        void OnEnable() => m_ToggleAction.Enable();
        void OnDisable() => m_ToggleAction.Disable();

        void OnTogglePerformed(InputAction.CallbackContext context)
        {
            // usage が付いていないデバイスだと右手Aと取り違える恐れがあるため、左手からの
            // 入力であることを確かめる。どのデバイスが発火したかは実機の切り分け用に必ず出す
            bool isLeft = ControllerHand.IsLeft(context);
            Debug.Log($"[VrMode] 入力: {ControllerHand.Describe(context)} 左手判定={isLeft}");
            if (!isLeft)
            {
                return;
            }
            m_ToggleRequested = true;
        }

        void Update()
        {
            // 起動時に既定（MR）を1回適用して、外殻の表示状態を確定させる
            if (!m_Applied)
            {
                m_Applied = true;
                Apply();
            }

            bool requested = m_ToggleRequested;
            m_ToggleRequested = false;
            if (requested && Time.unscaledTime - m_LastToggleTime >= k_ToggleCooldown)
            {
                m_LastToggleTime = Time.unscaledTime;
                IsVrMode = !IsVrMode;
                Apply();
            }
        }

        void Apply()
        {
            if (m_Passthrough != null)
            {
                m_Passthrough.SetPassthroughEnabled(!IsVrMode);
            }
            if (m_Shell != null)
            {
                m_Shell.SetVisible(IsVrMode);
            }

            Debug.Log($"[VrMode] {(IsVrMode ? "VR（パススルー無効・外殻表示）" : "MR（パススルー有効・外殻非表示）")}");
            DebugPanel.Notify($"mode {(IsVrMode ? "VR" : "MR")}");
        }
    }
}
