using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockField.Watch
{
    /// <summary>
    /// L0 の実機操作。**今測りたいものでボタンを割り当てる**（使う頻度ではない）。
    ///
    /// | 操作 | 内容 |
    /// |---|---|
    /// | 左手グリップ | 表示段の切り替え（頭位置のみ / 走査境界 / カバレッジ全体） |
    /// | 右手 A | **実時間と再生の切り替え**。再生が同じであることを目で確かめるため |
    /// </summary>
    public sealed class WatchInput : MonoBehaviour
    {
        [SerializeField] WatchView m_View;
        [SerializeField] WatchField m_Field;

        public WatchView view { get => m_View; set => m_View = value; }
        public WatchField field { get => m_Field; set => m_Field = value; }

        InputAction m_Grip;
        InputAction m_A;
        float m_PrevGrip;
        bool m_PrevA;

        void Awake()
        {
            m_Grip = new InputAction("LeftGrip", InputActionType.Value,
                "<XRController>{LeftHand}/grip", expectedControlType: "Axis");
            m_A = new InputAction("RightPrimary", InputActionType.Button,
                "<XRController>{RightHand}/primaryButton");
        }

        void OnEnable() { m_Grip.Enable(); m_A.Enable(); }
        void OnDisable() { m_Grip.Disable(); m_A.Disable(); }

        void Update()
        {
            float grip = m_Grip.ReadValue<float>();
            if (grip > 0.7f && m_PrevGrip <= 0.7f && m_View != null)
            {
                m_View.CycleMode();
                Debug.Log($"[Watch] 表示段を {m_View.CurrentName} に切り替え");
            }
            m_PrevGrip = grip;

            bool a = m_A.IsPressed();
            if (a && !m_PrevA && m_Field != null)
            {
                m_Field.Replaying = !m_Field.Replaying;
                Debug.Log($"[Watch] {(m_Field.Replaying ? "再生" : "実時間")} に切り替え");
            }
            m_PrevA = a;
        }
    }
}
