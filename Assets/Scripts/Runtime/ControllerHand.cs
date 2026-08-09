using System.Text;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

namespace BlockField
{
    /// <summary>
    /// コントローラのボタン入力が「本当に意図した手から来たか」を確かめるヘルパー
    /// (Demo 4.5b の調査で追加)。
    ///
    /// 【なぜ必要か】<c>&lt;XRController&gt;{LeftHand}/primaryButton</c> のような
    /// usage 付きバインドは、XR デバイスに LeftHand/RightHand の usage が
    /// 付いていなければ**どちらの手でも一致してしまう**。右手Aと左手Xに別々の機能を
    /// 割り当てているため、取り違えると「Xを押したらシードが変わる」ことになる。
    /// コールバックで実際のデバイスの usage を確認し、違えば無視する。
    ///
    /// あわせて発火元のデバイス名と usage をログに残し、実機で1回で切り分けられるようにする。
    /// </summary>
    public static class ControllerHand
    {
        /// <summary>コールバックの発火元が左手か。usage が付いていないデバイスは false。</summary>
        public static bool IsLeft(InputAction.CallbackContext context) =>
            HasUsage(context, CommonUsages.LeftHand.ToString());

        /// <summary>コールバックの発火元が右手か。usage が付いていないデバイスは false。</summary>
        public static bool IsRight(InputAction.CallbackContext context) =>
            HasUsage(context, CommonUsages.RightHand.ToString());

        static bool HasUsage(InputAction.CallbackContext context, string usage)
        {
            var device = context.control?.device;
            if (device == null)
            {
                return false;
            }
            foreach (var u in device.usages)
            {
                if (u == usage)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>発火元の説明（デバイス名 ＋ usage 一覧）。ログ用。</summary>
        public static string Describe(InputAction.CallbackContext context)
        {
            var device = context.control?.device;
            if (device == null)
            {
                return "デバイス不明";
            }

            var sb = new StringBuilder();
            sb.Append(device.name).Append(" usages=[");
            bool first = true;
            foreach (var u in device.usages)
            {
                if (!first) sb.Append(' ');
                sb.Append(u.ToString());
                first = false;
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
