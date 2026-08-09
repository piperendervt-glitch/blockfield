using System.Collections.Generic;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;

namespace BlockField.SimCore.Ecology
{
    /// <summary>イベント種別。</summary>
    public enum SimEventType : byte
    {
        PlayerPlace = 0,
        PlayerBreak = 1,

        /// <summary>
        /// 現実観測 (Demo 4.5 G1)。payload 本体は SimEvent には載せず、
        /// <see cref="SimEvent.payloadIndex"/> で EventLog の付随テーブルを指す。
        /// </summary>
        Observation = 2,

        /// <summary>植物の独立破壊 (Demo 4 UX)。地形は不変更、植物のみ消滅＋植生場×0.5。</summary>
        PlayerBreakPlant = 3,
    }

    /// <summary>
    /// シムイベント (Demo 4 F2 / Demo 4.5 G1)。JSON 化しやすいプレーンな構造を保つ。
    /// - PlayerPlace: blockId は設置ブロック
    /// - PlayerBreak: blockId は破壊時点のブロック（監査用）
    /// - Observation: payloadIndex が EventLog.Observations のインデックスを指す
    /// - applied: 適用されたか（無効操作は false のまま記録される）
    ///
    /// SimEvent 自体は太らせない方針: 大きな payload は EventLog の並列テーブルに置き、
    /// ここにはインデックスのみを持つ。これにより「イベントログ」は1成果物のままで、
    /// 決定論 f(初期シード, イベントログ) が文字通り成立する。
    /// </summary>
    public struct SimEvent
    {
        public long tick;
        public SimEventType type;
        public Int3 cell;
        public byte blockId;
        public bool applied;

        /// <summary>付随テーブルへのインデックス。payload を持たないイベントは -1。</summary>
        public int payloadIndex;

        /// <summary>payload を持たないイベント用（既存呼び出しとの互換オーバーロード）。</summary>
        public SimEvent(long tick, SimEventType type, Int3 cell, byte blockId, bool applied)
            : this(tick, type, cell, blockId, applied, -1)
        {
        }

        public SimEvent(long tick, SimEventType type, Int3 cell, byte blockId, bool applied, int payloadIndex)
        {
            this.tick = tick;
            this.type = type;
            this.cell = cell;
            this.blockId = blockId;
            this.applied = applied;
            this.payloadIndex = payloadIndex;
        }
    }

    /// <summary>
    /// イベントログ = 「イベント列＋付随テーブル」を束ねた1オブジェクト (Demo 4.5 G1)。
    /// 決定論 f(初期シード, イベントログ) の「入力の記録」であり、ContentHash には含めない。
    /// World.Replay がこのオブジェクトからワールドを再構築する。
    ///
    /// 【M4 の保証範囲】
    /// ここに載る観測データ（<see cref="Observations"/>）はセル単位の整数高さであり、
    /// リプレイ経路に浮動小数点の幾何演算が入らない。生メッシュのアーカイブは
    /// このログには載らず、M4 の保証対象外の反復用資材である（prereg demo45 参照）。
    /// </summary>
    public sealed class EventLog
    {
        readonly List<SimEvent> m_Events = new List<SimEvent>();
        readonly List<RoomObservation> m_Observations = new List<RoomObservation>();

        public IReadOnlyList<SimEvent> Events => m_Events;

        /// <summary>Observation イベントの payload 本体（並列テーブル）。</summary>
        public IReadOnlyList<RoomObservation> Observations => m_Observations;

        public void Append(SimEvent e)
        {
            m_Events.Add(e);
        }

        /// <summary>
        /// 観測データを付随テーブルへ追加し、そのインデックスを返す。
        /// 呼び出し側はこのインデックスを SimEvent.payloadIndex に載せる。
        /// </summary>
        public int AddObservation(RoomObservation observation)
        {
            m_Observations.Add(observation);
            return m_Observations.Count - 1;
        }

        /// <summary>payloadIndex から観測データを引く。範囲外・非Observation は null。</summary>
        public RoomObservation GetObservation(int payloadIndex)
        {
            if (payloadIndex < 0 || payloadIndex >= m_Observations.Count)
            {
                return null;
            }
            return m_Observations[payloadIndex];
        }
    }
}
