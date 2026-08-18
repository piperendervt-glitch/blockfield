using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockField.Aquarium
{
    /// <summary>
    /// 水槽モードの実機操作 (系列2 Phase B)。
    ///
    /// 実機セッションで確かめたいのは2点なので、それぞれにボタンを割り当てる。
    /// - **左手X: セルサイズ**（8cm → 6.5cm → 5.5cm）。コストと見え方の変化を見る
    /// - **左手Y: 粒子プリセット**（微粒子 → 粗い粒 → 速い所が明るい）。水に見えるかを探す
    /// - **右手A: 目標流速**（0.03 → 0.08 → 0.15 m/s）。速さは一発で当たらないので振る
    /// - **右手B: 傘径**（10 → 15 → 25 cm）。実部屋での見え方に直結する
    /// - **右手グリップ: 壁の反発**（0.06 → 0.12 → 0.00 → 0.03 m/s）。
    ///   止水でクラゲが壁に張り付く件の調整。弾かれて見えない強さを探す
    /// - **右手トリガー: 復元トルク**（0.5 → 0(切) → 2.0）。姿勢が立ち直るかを見る
    /// - **左手トリガー: 刺激を注入**（側方のセルを一発叩く。M-J3b の実機版）。
    ///   単一ペースメーカーでも回頭は出るが平衡傾斜が約5度で見えないため、
    ///   **見える大きさにする**ために使う
    /// - **左手グリップ: デバッグ表示**（なし → 固体セル(遮蔽あり) → 固体セル(遮蔽なし)
    ///   → 水槽の外接箱）。**焼き込んだ壁が現実の壁と重なっているか**を目で確かめる
    ///
    /// セッション時間は5分が目安（CLAUDE.md）なので、
    /// 装着したまま全部を回せる形にしてある。
    /// </summary>
    public sealed class AquariumInput : MonoBehaviour
    {
        [SerializeField] AquariumFlow m_Flow;
        [SerializeField] FlowParticleView m_Particles;
        [SerializeField] AquariumJellyfish m_Jelly;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }
        public FlowParticleView particles { get => m_Particles; set => m_Particles = value; }
        public AquariumJellyfish jelly { get => m_Jelly; set => m_Jelly = value; }

        [SerializeField] AquariumDebugView m_Debug;
        public AquariumDebugView debugView { get => m_Debug; set => m_Debug = value; }

        InputAction m_CellSizeAction;
        InputAction m_PresetAction;
        InputAction m_SpeedAction;
        InputAction m_BellAction;
        InputAction m_DebugAction;
        InputAction m_RepelAction;
        InputAction m_RightingAction;
        InputAction m_StimulusAction;

        void OnEnable()
        {
            m_CellSizeAction = new InputAction("AquariumCellSize", InputActionType.Button,
                "<XRController>{LeftHand}/primaryButton");
            m_CellSizeAction.performed += OnCellSize;
            m_CellSizeAction.Enable();

            m_PresetAction = new InputAction("AquariumPreset", InputActionType.Button,
                "<XRController>{LeftHand}/secondaryButton");
            m_PresetAction.performed += OnPreset;
            m_PresetAction.Enable();

            m_SpeedAction = new InputAction("AquariumSpeed", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
            m_SpeedAction.performed += OnSpeed;
            m_SpeedAction.Enable();

            m_BellAction = new InputAction("AquariumBell", InputActionType.Button,
                "<XRController>{RightHand}/secondaryButton");
            m_BellAction.performed += OnBell;
            m_BellAction.Enable();

            // ボタン4つは既に埋まっているのでグリップを使う
            m_DebugAction = new InputAction("AquariumDebug", InputActionType.Button,
                "<XRController>{LeftHand}/gripPressed");
            m_DebugAction.performed += OnDebug;
            m_DebugAction.Enable();

            m_RepelAction = new InputAction("AquariumRepel", InputActionType.Button,
                "<XRController>{RightHand}/gripPressed");
            m_RepelAction.performed += OnRepel;
            m_RepelAction.Enable();

            m_RightingAction = new InputAction("AquariumRighting", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            m_RightingAction.performed += OnRighting;
            m_RightingAction.Enable();

            m_StimulusAction = new InputAction("AquariumStimulus", InputActionType.Button,
                "<XRController>{LeftHand}/triggerPressed");
            m_StimulusAction.performed += OnStimulus;
            m_StimulusAction.Enable();
        }

        void OnRighting(InputAction.CallbackContext _)
        {
            if (m_Jelly != null) m_Jelly.CycleRighting();
        }

        void OnStimulus(InputAction.CallbackContext _)
        {
            if (m_Jelly != null) m_Jelly.InjectStimulus();
        }

        void OnRepel(InputAction.CallbackContext _)
        {
            if (m_Jelly != null) m_Jelly.CycleWallRepel();
        }

        void OnDebug(InputAction.CallbackContext _)
        {
            if (m_Debug != null) m_Debug.CycleMode();
        }

        void OnDisable()
        {
            if (m_CellSizeAction != null)
            {
                m_CellSizeAction.performed -= OnCellSize;
                m_CellSizeAction.Disable();
                m_CellSizeAction.Dispose();
                m_CellSizeAction = null;
            }
            if (m_PresetAction != null)
            {
                m_PresetAction.performed -= OnPreset;
                m_PresetAction.Disable();
                m_PresetAction.Dispose();
                m_PresetAction = null;
            }
            if (m_SpeedAction != null)
            {
                m_SpeedAction.performed -= OnSpeed;
                m_SpeedAction.Disable();
                m_SpeedAction.Dispose();
                m_SpeedAction = null;
            }
            if (m_BellAction != null)
            {
                m_BellAction.performed -= OnBell;
                m_BellAction.Disable();
                m_BellAction.Dispose();
                m_BellAction = null;
            }
            if (m_DebugAction != null)
            {
                m_DebugAction.performed -= OnDebug;
                m_DebugAction.Disable();
                m_DebugAction.Dispose();
                m_DebugAction = null;
            }
            if (m_RepelAction != null)
            {
                m_RepelAction.performed -= OnRepel;
                m_RepelAction.Disable();
                m_RepelAction.Dispose();
                m_RepelAction = null;
            }
            if (m_RightingAction != null)
            {
                m_RightingAction.performed -= OnRighting;
                m_RightingAction.Disable();
                m_RightingAction.Dispose();
                m_RightingAction = null;
            }
            if (m_StimulusAction != null)
            {
                m_StimulusAction.performed -= OnStimulus;
                m_StimulusAction.Disable();
                m_StimulusAction.Dispose();
                m_StimulusAction = null;
            }
        }

        void OnBell(InputAction.CallbackContext _)
        {
            if (m_Jelly != null) m_Jelly.CycleBellDiameter();
        }

        void OnSpeed(InputAction.CallbackContext _)
        {
            if (m_Flow != null) m_Flow.CycleTargetSpeed();
        }

        void OnCellSize(InputAction.CallbackContext _)
        {
            if (m_Flow != null) m_Flow.CycleCellSize();
        }

        void OnPreset(InputAction.CallbackContext _)
        {
            if (m_Particles != null) m_Particles.CyclePreset();
        }
    }
}
