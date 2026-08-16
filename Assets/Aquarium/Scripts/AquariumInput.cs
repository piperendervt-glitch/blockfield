using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockField.Aquarium
{
    /// <summary>
    /// 水槽モードの実機操作 (系列2 Phase B)。
    ///
    /// 実機セッションで確かめたいのは2点なので、それぞれにボタンを割り当てる。
    /// - **左手X: セルサイズ**（8cm → 6.5cm → 5.5cm）。コストと見え方の変化を見る
    /// - **左手Y: 粒子プリセット**（微粒子 → 粗い粒 → 流線強調）。水に見えるかを探す
    ///
    /// セッション時間は5分が目安（CLAUDE.md）なので、
    /// 装着したまま両方を回せる形にしてある。
    /// </summary>
    public sealed class AquariumInput : MonoBehaviour
    {
        [SerializeField] AquariumFlow m_Flow;
        [SerializeField] FlowParticleView m_Particles;

        public AquariumFlow flow { get => m_Flow; set => m_Flow = value; }
        public FlowParticleView particles { get => m_Particles; set => m_Particles = value; }

        InputAction m_CellSizeAction;
        InputAction m_PresetAction;

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
