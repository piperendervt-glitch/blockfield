using System.Collections.Generic;

namespace BlockField.SimCore.Ecology
{
    /// <summary>イベント種別。実書き込みは Demo 4（設置・破壊）と Demo 4.5（観測）で導入。</summary>
    public enum SimEventType : byte
    {
        PlayerPlace = 0,
        PlayerBreak = 1,
        Observation = 2,
    }

    public readonly struct SimEvent
    {
        public readonly long tick;
        public readonly SimEventType type;
        public readonly string payload;

        public SimEvent(long tick, SimEventType type, string payload)
        {
            this.tick = tick;
            this.type = type;
            this.payload = payload;
        }
    }

    /// <summary>
    /// イベントログの枠 (Demo 3 E5)。決定論を f(初期シード, イベントログ) へ拡張するための入力記録。
    /// 「ログは状態ではなく入力の記録」なので ContentHash には含めない。
    /// </summary>
    public sealed class EventLog
    {
        readonly List<SimEvent> m_Events = new List<SimEvent>();

        public IReadOnlyList<SimEvent> Events => m_Events;

        public void Append(SimEvent e)
        {
            m_Events.Add(e);
        }
    }
}
