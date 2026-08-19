using System.Collections.Generic;
using System.IO;
using BlockField.Aquarium;
using BlockField.SimCore.Watch;
using UnityEngine;

namespace BlockField.Watch
{
    /// <summary>
    /// L0 の駆動役。**20Hz 固定ティック**でプロデューサを読み、滞在の場へ取り込む。
    ///
    /// 【この場は「この人（装着者）の滞在」を主張する】「誰かの滞在」ではない。
    /// プロデューサが Quest 3 の頭位置1つだけであり、装着者以外は原理的に観測できない。
    /// **カバレッジの定義がこの主張に依存している。**
    ///
    /// 【走査済み領域の定義】シーンメッシュの近く（距離場が飽和していない範囲）を
    /// 走査済みとする。扉の向こうや隣室はメッシュが無いので距離場が飽和し、
    /// **常に欠測**になる。部屋の焼き込みは系列2 の <see cref="AquariumFlow"/> が
    /// 持っている1本の経路をそのまま使う（同じ組み立てを2か所に書かない）。
    /// </summary>
    public sealed class WatchField : MonoBehaviour
    {
        /// <summary>この距離（セル数）より遠いセルは「走査されていない」とみなす。</summary>
        public const float ScannedBandCells = 6f;

        [SerializeField] AquariumFlow m_Room;
        [SerializeField] HeadPoseProducer m_Head;
        [SerializeField] WatchView m_View;
        [SerializeField] bool m_Replay;
        [SerializeField] string m_ReplayLogPath;

        public AquariumFlow room { get => m_Room; set => m_Room = value; }
        public HeadPoseProducer head { get => m_Head; set => m_Head = value; }
        public WatchView view { get => m_View; set => m_View = value; }

        /// <summary>**実時間と再生を切り替える。** 再生が同じであることを目で確かめるため。</summary>
        public bool Replaying { get => m_Replay; set => m_Replay = value; }
        public string replayLogPath { get => m_ReplayLogPath; set => m_ReplayLogPath = value; }

        public PresenceField Field { get; private set; }
        public L0Ticker Ticker { get; } = new L0Ticker();

        public string Status { get; private set; } = "部屋の焼き込み待ち";

        /// <summary>再生で読み込んだレコード数と、いま何件目か。</summary>
        public int ReplayCount => m_Replay ? m_Replayed.Count : 0;
        public int ReplayCursor { get; private set; }

        readonly List<L0Sample> m_Replayed = new List<L0Sample>();

        void Update()
        {
            if (Field == null && !TryBuildField()) return;

            int steps = Ticker.Advance(Time.unscaledDeltaTime);
            for (int i = 0; i < steps; i++) Step();
        }

        bool TryBuildField()
        {
            var grid = m_Room != null && m_Room.Field != null ? m_Room.Field.Grid : null;
            if (grid == null) { Status = "部屋の焼き込み待ち"; return false; }

            // 走査済み = 水であって、かつシーンメッシュの近くにあるセル。
            // 遠い（距離場が飽和している）セルは**部屋として走査されていない**
            var scanned = new bool[grid.CellCount];
            int n = 0;
            for (int z = 0; z < grid.Depth; z++)
                for (int y = 0; y < grid.Height; y++)
                    for (int x = 0; x < grid.Width; x++)
                    {
                        int i = grid.Index(x, y, z);
                        bool ok = !grid.IsSolid(x, y, z)
                            && grid.DistanceInCells(i) < ScannedBandCells;
                        scanned[i] = ok;
                        if (ok) n++;
                    }

            Field = new PresenceField(grid.Width, grid.Height, grid.Depth, grid.CellSize,
                grid.OriginX, grid.OriginY, grid.OriginZ, scanned);
            Status = $"走査済み {n} / 全 {grid.CellCount} セル";

            if (m_Replay) LoadReplay();
            Debug.Log($"[Watch] 場を作った: {Status}  再生={(m_Replay ? "ON" : "OFF")}");
            return true;
        }

        void Step()
        {
            L0Sample sample;
            if (m_Replay)
            {
                if (m_Replayed.Count == 0) return;
                sample = m_Replayed[ReplayCursor % m_Replayed.Count];
                ReplayCursor++;
            }
            else if (m_Head == null || !m_Head.TryRead(Ticker.Tick, out sample))
            {
                return;
            }

            Field.Ingest(sample);

            // 【記録は毎ティック】再生で同じ絵になる必要があるので間引かない。
            // 20Hz なので 1 秒あたり 20 行
            if (!m_Replay) Debug.Log(L0LogFormat.Format(sample));

            // 【パネルの値はログにも出す】1秒に1回。装着中のユーザーは数値を
            // 読み上げられず、セッション後に転記もできない（同じ漏れを3回起こしている）
            if (Ticker.Tick % L0Ticker.HzDefault == 0) LogStatus();
        }

        void LoadReplay()
        {
            m_Replayed.Clear();
            ReplayCursor = 0;
            if (string.IsNullOrEmpty(m_ReplayLogPath) || !File.Exists(m_ReplayLogPath))
            {
                Debug.LogWarning($"[Watch] 再生ログが無い: {m_ReplayLogPath}");
                return;
            }
            foreach (string line in File.ReadLines(m_ReplayLogPath))
            {
                if (L0LogFormat.TryParse(line, out var s)) m_Replayed.Add(s);
            }
            Debug.Log($"[Watch] 再生ログを読んだ: {m_Replayed.Count} 件 ({m_ReplayLogPath})");
        }

        /// <summary>セルの中心の部屋座標。描画用。</summary>
        public Vector3 CellCenter(int index)
        {
            var f = Field;
            int x = index % f.Width;
            int y = index / f.Width % f.Height;
            int z = index / (f.Width * f.Height);
            return new Vector3(
                f.OriginX + (x + 0.5f) * f.CellSize,
                f.OriginY + (y + 0.5f) * f.CellSize,
                f.OriginZ + (z + 0.5f) * f.CellSize);
        }
    
        /// <summary>
        /// パネルに出している値を**そのまま**ログへ出す。
        /// 対応は <c>WatchPanelLogParityTests</c> がテストで固定している。
        /// </summary>
        void LogStatus()
        {
            var f = Field;
            var pos = m_Head != null ? m_Head.LastRoomPosition : Vector3.zero;
            Debug.Log($"[Watch] Tick={Ticker.Tick} 遅延={Ticker.Backlog * 1000f:F1}ms " +
                $"落し={Ticker.DroppedTicks} 歩進={Ticker.StepsLastFrame} " +
                $"頭=({pos.x:F2},{pos.y:F2},{pos.z:F2}) 状態={m_Head?.LastLabel} " +
                $"カバレッジ={f.CoveredCells} 欠測={f.MissingCells} " +
                $"走査済={f.ScannedCells}/{f.CellCount} " +
                $"View={(m_View != null ? m_View.CurrentName : "未配線")} " +
                $"n={(m_View != null ? m_View.DrawnCells : 0)} " +
                $"{(Replaying ? $"再生 {ReplayCursor}/{ReplayCount}" : "実時間")} " +
                $"刻印={BuildStamp.Text} アンカー={AnchorIdentity()}");
        }

        /// <summary>アンカー識別子。登録座標はこれに紐づくので毎セッション残す。</summary>
        string AnchorIdentity()
        {
            var sp = m_Head != null ? m_Head.space : null;
            if (sp == null) return "(未配線)";
            return string.IsNullOrEmpty(sp.AnchorGuid) ? "(未確定)" : sp.AnchorGuid;
        }
    }
}
