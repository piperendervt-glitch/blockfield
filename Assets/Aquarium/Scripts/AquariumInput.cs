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
    ///
    /// セッション時間は5分が目安（CLAUDE.md）なので、
    /// 装着したまま両方を回せる形にしてある。
    /// </summary>
    public sealed class AquariumInput : MonoBehaviour
    {
        [SerializeField] AquariumFlow m_Flow;
        [SerializeField] FlowParticleView m_Particles;
        [SerializeField] AquariumJellyfish m_Jelly;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }
        public FlowParticleView particles { get => m_Particles; set => m_Particles = value; }
        public AquariumJellyfish jelly { get => m_Jelly; set => m_Jelly = value; }

        InputAction m_CellSizeAction;
        InputAction m_PresetAction;
        InputAction m_SpeedAction;
        InputAction m_BellAction;

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
