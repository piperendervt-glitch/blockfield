using System.Collections.Generic;
using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>イベント種別。Observation は Demo 4.5（現実観測）で使用予定。</summary>
    public enum SimEventType : byte
    {
        PlayerPlace = 0,
        PlayerBreak = 1,
        Observation = 2,

        /// <summary>植物の独立破壊 (Demo 4 UX)。地形は不変更、植物のみ消滅＋植生場×0.5。</summary>
        PlayerBreakPlant = 3,
    }

    /// <summary>
    /// シムイベント (Demo 4 F2)。JSON 化しやすいプレーンな構造を保つ。
    /// - PlayerPlace: blockId は設置ブロック
    /// - PlayerBreak: blockId は破壊時点のブロック（監査用）
    /// - applied: 適用されたか（無効操作は false のまま記録される）
    /// </summary>
    public struct SimEvent
    {
        public long tick;
        public SimEventType type;
        public Int3 cell;
        public byte blockId;
        public bool applied;

        public SimEvent(long tick, SimEventType type, Int3 cell, byte blockId, bool applied)
        {
            this.tick = tick;
            this.type = type;
            this.cell = cell;
            this.blockId = blockId;
            this.applied = applied;
        }
    }

    /// <summary>
    /// イベントログ。決定論 f(初期シード, イベントログ) の「入力の記録」であり、
    /// ContentHash には含めない。World.Replay がこのリストからワールドを再構築する。
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
