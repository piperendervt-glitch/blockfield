using BlockField.SimCore.Watch;
using UnityEngine;
using UnityEngine.XR;

namespace BlockField.Watch
{
    /// <summary>
    /// Quest 3 の頭位置を L0 のレコードとして書くプロデューサ。
    ///
    /// 【これはソースの1つにすぎない】L0 はセンサではなく**ストリーム形式**である。
    /// このクラスは「その形を書く実装」であって、L0 そのものではない。
    /// **L1 以上はプロデューサの種類を知らない。**
    ///
    /// 【変換は恒等】<see cref="WatchSpaceRenderer.TryGetHeadInRoom"/> がすでに
    /// 部屋座標で返すので、このプロデューサの「部屋座標への変換」は恒等である。
    /// 将来 外部センサを足すときは、そのプロデューサが自分の変換を1つ持つ。
    ///
    /// 【カバレッジの主張】トラッキングが生きていれば、カバレッジは
    /// **走査済みの部屋領域全体**になる。自分の位置が分かるなら、
    /// **自分がどこに居ないかも分かる**からである。
    /// トラッキングを失う・未装着なら**空集合**にする。推定で埋めない。
    /// </summary>
    public sealed class HeadPoseProducer : MonoBehaviour, IL0Producer
    {
        public const int HeadProducerId = 1;

        [SerializeField] WatchSpaceRenderer m_Space;

        public WatchSpaceRenderer space { get => m_Space; set => m_Space = value; }

        public int ProducerId => HeadProducerId;

        /// <summary>直近に読めた部屋座標（診断・描画用）。</summary>
        public Vector3 LastRoomPosition { get; private set; }

        /// <summary>直近のラベル。パネルとログに出す。</summary>
        public L0Label LastLabel { get; private set; } = L0Label.NotWorn;

        /// <summary>**頭位置は恒等変換。** 生値がすでに部屋座標で来る。</summary>
        public void ToRoom(float x, float y, float z, out float rx, out float ry, out float rz)
        {
            rx = x; ry = y; rz = z;
        }

        public bool TryRead(int tick, out L0Sample sample)
        {
            L0Label label = CurrentLabel();
            LastLabel = label;

            if (label != L0Label.Measured || m_Space == null
                || !m_Space.TryGetHeadInRoom(out var head))
            {
                // **空集合。** 直前値保持もゼロ埋めもしない
                sample = new L0Sample(ProducerId, tick, 0f, 0f, 0f, 0f,
                    L0Coverage.None, label == L0Label.Measured ? L0Label.TrackingLost : label);
                LastLabel = sample.Label;
                return true;
            }

            ToRoom(head.x, head.y, head.z, out float rx, out float ry, out float rz);
            LastRoomPosition = new Vector3(rx, ry, rz);
            sample = new L0Sample(ProducerId, tick, rx, ry, rz, 1f,
                L0Coverage.ScannedRoom, L0Label.Measured);
            return true;
        }

        /// <summary>
        /// 装着とトラッキングの状態。**未装着とトラッキング喪失を分けて記録する**
        /// （どちらもカバレッジは空集合だが、区別できないと後で読めない）。
        /// </summary>
        static L0Label CurrentLabel()
        {
            var hmd = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!hmd.isValid) return L0Label.NotWorn;

            if (hmd.TryGetFeatureValue(CommonUsages.userPresence, out bool worn) && !worn)
                return L0Label.NotWorn;

            if (hmd.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && !tracked)
                return L0Label.TrackingLost;

            return L0Label.Measured;
        }
    }
}
