using BlockField.SimCore.Watch;
using UnityEngine;
using UnityEngine.XR;

namespace BlockField.Watch
{
    /// <summary>
    /// Quest 3 の頭位置のプロデューサ。**L0a（観測）と L0b（定位）を持つ。**
    /// 変換を掛けて記録するのは L0c（<see cref="WatchField"/>）の仕事である。
    ///
    /// 【L0a は座標系に依存しない】<see cref="TryObserve"/> は
    /// **デバイス座標の生の観測**を返す。部屋座標が成立していなくても記録できる
    /// （段0 のゲートが落ちたときの保険。roadmap v14.1）。
    ///
    /// 【L0b はプロデューサごとに独立】<see cref="Localize"/> は
    /// **変換と、その確からしさ**を返す。頭位置は Meta の SLAM が部屋座標まで
    /// 面倒を見るので**変換は恒等**、確からしさは追跡状態から導出する。
    /// 固定カメラはマーカー＋幾何照合、機体は点群位置合わせで同じ形を返す。
    /// **同じ出力を出す限り、上は区別しない。**
    ///
    /// 【確からしさの出力口は最初から持つ】段1 では恒等を返すだけだが、
    /// 口が無いと後から足すときに上の層を触ることになる。
    /// </summary>
    public sealed class HeadPoseProducer : MonoBehaviour
    {
        public const int HeadProducerId = 1;

        [SerializeField] WatchSpaceRenderer m_Space;

        public WatchSpaceRenderer space { get => m_Space; set => m_Space = value; }

        public int ProducerId => HeadProducerId;

        /// <summary>直近に読めた位置（診断・描画用）。</summary>
        public Vector3 LastRoomPosition { get; private set; }

        /// <summary>直近のラベル。パネルとログに出す。</summary>
        public L0Label LastLabel { get; private set; } = L0Label.NotWorn;

        /// <summary>直近の確からしさ（L0b）。パネルとログに出す。</summary>
        public float LastConfidence { get; private set; }

        /// <summary>
        /// **L0a: 観測。** デバイス座標の生の位置を返す。加工しない。
        ///
        /// 頭位置の場合、Meta の SLAM が返すのは既にワールド座標なので、
        /// **アンカーへ落とすところまでを「観測」とみなす**
        /// （アンカーの適用は <see cref="WatchSpaceRenderer"/> に閉じている）。
        /// アンカーが未確定・ヘッド未取得なら false。**0 を返さない。**
        /// </summary>
        public bool TryObserve(out Vector3 position, out L0Label label)
        {
            label = CurrentLabel();
            LastLabel = label;
            position = default;

            if (label != L0Label.Measured || m_Space == null) return false;
            if (!m_Space.TryGetHeadInRoom(out position)) return false;

            LastRoomPosition = position;
            return true;
        }

        /// <summary>
        /// **L0b: 定位。** 部屋座標への変換と、その確からしさを返す。
        ///
        /// 段1 は恒等変換。確からしさは追跡状態から導出する
        /// （追跡中 1、喪失・未装着・アンカー未確定 0）。
        /// **変換は時間の関数であってよい** — 頭位置が定数なのは特殊例にすぎない。
        /// </summary>
        public L0Localization Localize()
        {
            float confidence =
                m_Space == null || !m_Space.IsReady ? 0f
                : LastLabel == L0Label.Measured ? 1f
                : 0f;
            LastConfidence = confidence;
            return L0Localization.Identity(ProducerId, confidence);
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
