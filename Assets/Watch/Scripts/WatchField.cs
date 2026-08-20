using System.Collections.Generic;
using System.IO;
using BlockField.Aquarium;
using BlockField.SimCore.Watch;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BlockField.Watch
{
    /// <summary>
    /// L0 の駆動役。**20Hz 固定ティック**でプロデューサを読み、滞在の場へ取り込む。
    ///
    /// 【この場は「この人（装着者）の滞在」を主張する】「誰かの滞在」ではない。
    /// プロデューサが Quest 3 の頭位置1つだけであり、装着者以外は原理的に観測できない。
    /// **カバレッジの定義がこの主張に依存している。**
    ///
    /// 【場は床面の 2D 格子】滞在は本来床の量である。3D で持つと自分の高さのセルが
    /// 目の位置に描かれて見えない（2026-08-19 の実機で足元の印が視認できなかった）。
    /// 高さは <see cref="PresenceField.HeightAt"/> の属性として持つ。
    ///
    /// 【走査済みの定義】**Quest の Scene が持つ床の境界ポリゴンの内側。**
    /// `ARPlane.classifications` が `Floor` の面の `boundary` をそのまま使う。
    ///
    /// 【近似を2回外している】「部屋の内側」を自前で作ろうとして2回とも外した。
    /// 1回目「メッシュ表面から 6 セル（48cm）以内」— **狭すぎて部屋の中央が走査外**。
    /// この 6 は距離場の飽和値ですらなく、こちらが置いた定数だった（飽和は 7.97 セル）。
    /// 2回目「その床の列にメッシュ由来の固体があるか」— **広すぎて壁の外側まで含んだ**
    /// （天井のメッシュが外へ伸びるため。実測で外接箱のほぼ全域 8.19/9.14 m²）。
    /// **3回目の近似は置かない。**
    ///
    /// 【取れなければ埋めない】床のポリゴンが得られないときは
    /// **場を作らない**。近似へ落ちない（静かに壊れる形を禁じる）。
    /// 理由は <see cref="Status"/> に出す。
    /// </summary>
    public sealed class WatchField : MonoBehaviour
    {
        /// <summary>床から浮かせる量 (m)。床と同一平面だと z-fighting でちらつく。</summary>
        public const float FloorLift = 0.01f;

        [SerializeField] AquariumFlow m_Room;
        [SerializeField] HeadPoseProducer m_Head;
        [SerializeField] WatchView m_View;
        [SerializeField] ARPlaneManager m_Planes;
        [SerializeField] bool m_Replay;

        public AquariumFlow room { get => m_Room; set => m_Room = value; }
        public HeadPoseProducer head { get => m_Head; set => m_Head = value; }
        public WatchView view { get => m_View; set => m_View = value; }
        public ARPlaneManager planes { get => m_Planes; set => m_Planes = value; }

        /// <summary>
        /// **実時間と再生を切り替える。** 再生元が無ければ切り替えても実時間のまま。
        /// 無反応にしないため、状態は <see cref="ReplaySource"/> に出す。
        /// </summary>
        public bool Replaying
        {
            get => m_Replay && m_Replayed.Count > 0;
            set => m_Replay = value;
        }

        /// <summary>再生元の状態（「前回 N件」か「再生元なし」）。パネルとログに出す。</summary>
        public string ReplaySource { get; private set; } = "(未読込)";

        public PresenceField Field { get; private set; }
        public L0Ticker Ticker { get; } = new L0Ticker();

        public string Status { get; private set; } = "部屋の焼き込み待ち";

        public int ReplayCount => m_Replayed.Count;
        public int ReplayCursor { get; private set; }

        readonly List<L0Sample> m_Replayed = new List<L0Sample>();
        StreamWriter m_Recorder;

        /// <summary>
        /// 今回のセッションの記録の置き場。**次回のセッションで再生する材料**になる。
        /// 端末の永続領域なので、アプリを入れ替えても残る。
        /// </summary>
        string RecordPath => Path.Combine(Application.persistentDataPath, "l0_session.log");

        void OnDestroy() => CloseRecorder();

        void OnApplicationPause(bool paused)
        {
            if (paused) m_Recorder?.Flush();
        }

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

            if (!TryGetFloorPolygon(out var polygon, out float floorHeight, out string why))
            {
                Status = why;
                return false;
            }

            int w = grid.Width, d = grid.Depth;
            int n = PolygonMask.Build(polygon, w, d, grid.CellSize,
                grid.OriginX, grid.OriginZ, floorHeight, out var scanned, out var floorY);

            if (n == 0)
            {
                Status = "床ポリゴンが格子と重ならない";
                return false;
            }

            Field = new PresenceField(w, d, grid.CellSize, grid.OriginX, grid.OriginZ,
                scanned, floorY);
            FloorArea = n * grid.CellSize * grid.CellSize;
            Status = $"床 {n} セル = {FloorArea:F2} m²（格子 {w}x{d}）";

            LoadReplay();       // 再生元は**前回**の記録。上書きする前に読む
            OpenRecorder();
            Debug.Log($"[Watch] 場を作った: {Status}  床高={floorHeight:F2}m  再生元={ReplaySource}");
            return true;
        }

        /// <summary>走査済み領域の面積 (m²)。実部屋の広さと突き合わせるために出す。</summary>
        public float FloorArea { get; private set; }

        /// <summary>
        /// 床の境界ポリゴンを**部屋座標の XZ** で取る。
        ///
        /// 【見つからないときは近似しない】理由を返して場を作らせない。
        /// どの面が見えているかをログに出すので、次のセッションで何が取れるか分かる。
        /// </summary>
        bool TryGetFloorPolygon(out float[] polygonXZ, out float floorHeight, out string why)
        {
            polygonXZ = null;
            floorHeight = 0f;

            if (m_Planes == null) { why = "ARPlaneManager が未配線"; return false; }
            var originT = m_Head != null && m_Head.space != null && m_Head.space.origin != null
                ? m_Head.space.origin.OriginTransform : null;
            if (originT == null) { why = "アンカー未確定"; return false; }

            ARPlane floor = null;
            int seen = 0;
            var labels = new List<string>();
            foreach (var plane in m_Planes.trackables)
            {
                seen++;
                if (labels.Count < 12) labels.Add(plane.classifications.ToString());
                if ((plane.classifications & PlaneClassifications.Floor) == 0) continue;
                if (floor == null || plane.size.x * plane.size.y > floor.size.x * floor.size.y)
                    floor = plane;
            }

            if (floor == null)
            {
                why = $"床の面が未取得（面 {seen} 件）";
                if (Time.frameCount % 120 == 0)
                    Debug.Log($"[Watch] 床ポリゴン待ち: 面 {seen} 件 分類=[{string.Join(", ", labels)}]");
                return false;
            }

            var boundary = floor.boundary;
            if (boundary.Length < 3) { why = $"床の境界が {boundary.Length} 点"; return false; }

            polygonXZ = new float[boundary.Length * 2];
            double sumY = 0;
            for (int i = 0; i < boundary.Length; i++)
            {
                // 境界は面のローカル 2D。面 → ワールド → 部屋座標へ移す
                var local = new Vector3(boundary[i].x, 0f, boundary[i].y);
                var room = originT.InverseTransformPoint(floor.transform.TransformPoint(local));
                polygonXZ[i * 2] = room.x;
                polygonXZ[i * 2 + 1] = room.z;
                sumY += room.y;
            }
            floorHeight = (float)(sumY / boundary.Length);
            why = null;
            Debug.Log($"[Watch] 床ポリゴン: {boundary.Length} 点 床高={floorHeight:F2}m 分類={floor.classifications}");
            return true;
        }

        void Step()
        {
            L0Sample sample;
            if (Replaying)
            {
                sample = m_Replayed[ReplayCursor % m_Replayed.Count];
                ReplayCursor++;
            }
            else if (m_Head == null || !m_Head.TryRead(Ticker.Tick, out sample))
            {
                return;
            }

            Field.Ingest(sample);

            // 【記録は毎ティック】再生で同じ絵になる必要があるので間引かない。
            // **ファイルへ書く。** logcat へ毎ティック流すと 20Hz を維持できない
            // （2026-08-19 の実機で 57% のティックを落とした）
            if (!Replaying && m_Recorder != null) m_Recorder.WriteLine(L0LogFormat.Format(sample));

            // 【パネルの値はログにも出す】1秒に1回
            if (Ticker.Tick % L0Ticker.HzDefault == 0) LogStatus();
        }

        void OpenRecorder()
        {
            try
            {
                m_Recorder = new StreamWriter(RecordPath, false) { AutoFlush = false };
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[Watch] 記録を開けない: {e.Message}");
            }
        }

        void CloseRecorder()
        {
            m_Recorder?.Flush();
            m_Recorder?.Dispose();
            m_Recorder = null;
        }

        /// <summary>
        /// 再生元を読む。**無ければ「再生元なし」と出す。無反応にしない**
        /// （2026-08-19 の実機で、材料が無いまま切り替えて何も起きなかった）。
        /// </summary>
        void LoadReplay()
        {
            m_Replayed.Clear();
            ReplayCursor = 0;

            if (!File.Exists(RecordPath))
            {
                ReplaySource = "再生元なし";
                Debug.Log($"[Watch] 再生元なし ({RecordPath})");
                return;
            }
            try
            {
                foreach (string line in File.ReadLines(RecordPath))
                {
                    if (L0LogFormat.TryParse(line, out var s)) m_Replayed.Add(s);
                }
            }
            catch (IOException e)
            {
                ReplaySource = "再生元なし";
                Debug.LogWarning($"[Watch] 再生元を読めない: {e.Message}");
                return;
            }

            ReplaySource = m_Replayed.Count > 0 ? $"前回 {m_Replayed.Count}件" : "再生元なし";
            Debug.Log($"[Watch] 再生元: {ReplaySource} ({RecordPath})");
        }

        /// <summary>
        /// 床セルの中心の部屋座標。**床面からわずかに浮かせる** —
        /// 床と同一平面だと z-fighting でちらつく。
        /// </summary>
        public Vector3 CellCenter(int index)
        {
            var f = Field;
            int x = index % f.Width;
            int z = index / f.Width;
            return new Vector3(
                f.OriginX + (x + 0.5f) * f.CellSize,
                f.FloorY(index) + FloorLift,
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
                $"床={f.ScannedCells}セル({FloorArea:F2}m2) " +
                $"段={(m_View != null ? m_View.CurrentName : "未配線")} " +
                $"n={(m_View != null ? m_View.DrawnCells : 0)}/{(m_View != null ? m_View.WantedCells : 0)}" +
                $"{(m_View != null && m_View.Truncated ? " **切り捨て**" : "")} " +
                $"{(Replaying ? $"再生 {ReplayCursor}/{ReplayCount}" : "実時間")} " +
                $"再生元={ReplaySource} " +
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
