using System.Diagnostics;
using BlockField.SimCore.Fluid;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BlockField.Aquarium
{
    /// <summary>
    /// 部屋を水で満たす流れ場の本体 (系列2 Phase B)。
    ///
    /// 【役割の分界】計算は全部 SimCore（<see cref="FlowField"/>）にある。
    /// このクラスがやるのは「Unity から取れたものを SimCore の形に直して渡す」
    /// ことと「固定ティックで進める」ことだけ。UnityEngine 型は SimCore へ渡さない。
    ///
    /// 【固定ティックで進める】ψ のノイズ項は縞に分けて更新するので、
    /// フレーム駆動にするとフレームレート依存になり決定論が壊れる。
    /// <see cref="k_TickHz"/> の固定間隔で進め、描画側は補間して読む
    /// （既存の「表示と真実の分離」と同形）。
    /// </summary>
    public sealed class AquariumFlow : MonoBehaviour
    {
        /// <summary>流れ場を進める固定周波数 (Hz)。フレームレートとは独立。</summary>
        const float k_TickHz = 20f;

        /// <summary>計測ログを出す間隔（秒）。パネルに出す値はログにも出す（CLAUDE.md）。</summary>
        const float k_LogInterval = 1f;

        /// <summary>
        /// 実機で切り替えるセルサイズ (m)。
        /// 8cm で十分見えるなら 5.5cm を選ぶ理由がない、という判断のための選択肢。
        /// </summary>
        public static readonly float[] CellSizeChoices = { 0.08f, 0.065f, 0.055f };

        /// <summary>
        /// 実機で切り替える目標流速 (m/s)。
        ///
        /// 2026-08-16 の実機セッションで「速すぎて流れに見えない」となった
        /// （実測は中央 14.5 m/s、1フレーム 20cm）。一発で当てるより
        /// セッション中に振れる方が早いので3段階にした。
        /// 既定は **0.03**（Phase C）。クラゲの目標遊泳 0.04 m/s に対し、
        /// 0.08 だと流れが2倍速くほぼ流されるだけになる。0.03 ならクラゲがやや上回り、
        /// 「流されている」のか「泳いでいる」のかが見分けられる。
        /// Phase B（流れだけを見せる段）では 0.08 が良かった。
        /// </summary>
        public static readonly float[] TargetSpeedChoices = { 0.03f, 0.08f, 0.15f };

        [SerializeField] RoomScanner m_Scanner;
        [SerializeField] DioramaOrigin m_Origin;
        // 既定は 8cm（選択肢0）。2026-08-16 の実機で 8cm / 6.5cm / 5.5cm の
        // 見た目に差が無いことを確認し、ヘッドレスでも流速分布が一致する
        // （中央値は3種とも厳密に 0.0800、p90 の差 3%）ことを確かめたうえで、
        // 最も軽い 8cm を採った（ティック 4.41ms、72FPS 予算の 32%）
        [SerializeField] int m_CellSizeIndex;
        [SerializeField] int m_SpeedIndex;
        [SerializeField] FlowParticleView m_Particles;
        [SerializeField] AquariumJellyfish m_Jelly;

        public RoomScanner scanner { get => m_Scanner; set => m_Scanner = value; }
        public DioramaOrigin origin { get => m_Origin; set => m_Origin = value; }
        /// <summary>粒子の描画数をログへ出すためだけの参照（View には干渉しない）。</summary>
        public FlowParticleView particles { get => m_Particles; set => m_Particles = value; }
        /// <summary>クラゲの状態をログへ出すための参照。</summary>
        public AquariumJellyfish jelly { get => m_Jelly; set => m_Jelly = value; }

        /// <summary>現在のセルサイズの選択肢番号（0 = 8cm, 1 = 6.5cm, 2 = 5.5cm）。</summary>
        public int CellSizeIndex => m_CellSizeIndex;

        public float CellSize => CellSizeChoices[m_CellSizeIndex];

        /// <summary>現在の目標流速 (m/s)。</summary>
        public float TargetSpeed => TargetSpeedChoices[m_SpeedIndex];

        /// <summary>目標流速の選択肢番号（パネルに「今/全体」を出すため）。</summary>
        public int SpeedIndex => m_SpeedIndex;

        /// <summary>流れ場。焼き込みが済むまで null。</summary>
        public FlowField Field { get; private set; }

        // --- 計測（すべてログにも出す） ---
        public long BakeMs { get; private set; }
        /// <summary>
        /// 固体セルの総数（メッシュ由来 + 外周シール）。
        ///
        /// 【内訳を分けて持つ理由】以前は <see cref="FlowBoundaryBaker.BakeSolid"/> の
        /// 戻り値（= メッシュ由来だけ）を「固体」として出していたのに、高さ分布の
        /// 固体率は外周シールを含む値を出していた。**同じログの中で基準が違う**ため、
        /// 「空中の固体率が4割は多すぎないか」を確かめるのに端末からメッシュを
        /// 引き出して焼き直す必要があった（2026-08-16）。基準を揃えて両方出す。
        /// </summary>
        public int SolidCells { get; private set; }

        /// <summary>メッシュ（実際の壁・家具）由来の固体セル数。</summary>
        public int MeshSolidCells { get; private set; }

        /// <summary>格子の外周を塞いだぶんの固体セル数（水槽の縁。1セル厚のリング）。</summary>
        public int BorderSolidCells => SolidCells - MeshSolidCells;

        /// <summary>
        /// **外周シールを掛ける前**の固体マスク（＝現実の壁・家具だけ）。
        /// デバッグ表示が「焼き込んだ壁が現実の壁と重なっているか」を描くのに使う。
        /// 縁を混ぜると部屋の外側に層が出て、ずれているように見えてしまう。
        /// </summary>
        public bool[] MeshSolidMask { get; private set; }

        /// <summary>焼き直しの通し番号。表示側がキャッシュを作り直す合図に使う。</summary>
        public long BakeSerial { get; private set; }
        public double TickMs { get; private set; }
        public double MaxSpeed { get; private set; }
        public string Status { get; private set; } = "スキャン待ち";

        /// <summary>
        /// 格子を部屋の主軸へ合わせるために回したヨー角 (度)。
        /// **描画側はこの回転を掛けてから格子座標を使うこと**
        /// （格子はこの回転後の「部屋座標」で持っている）。
        /// </summary>
        public float RoomYawDegrees { get; private set; }

        float m_TickAccumulator;
        float m_NextLog;
        readonly Stopwatch m_Watch = new Stopwatch();

        void Update()
        {
            if (Field == null)
            {
                TryBake();
                return;
            }

            // 固定ティック。1フレームで複数ティック進むこともある（描画が遅れたとき）
            m_TickAccumulator += Time.deltaTime;
            float step = 1f / k_TickHz;
            int ticked = 0;
            m_Watch.Restart();
            while (m_TickAccumulator >= step && ticked < 4)
            {
                Field.Tick();
                m_TickAccumulator -= step;
                ticked++;
            }
            m_Watch.Stop();
            if (ticked > 0)
            {
                TickMs = m_Watch.Elapsed.TotalMilliseconds / ticked;
            }

            if (Time.unscaledTime >= m_NextLog)
            {
                m_NextLog = Time.unscaledTime + k_LogInterval;
                LogMetrics();
            }
        }

        /// <summary>
        /// セルサイズを切り替えて焼き直す。実機でコストと見え方を比べるための操作。
        /// </summary>
        public void CycleCellSize()
        {
            m_CellSizeIndex = (m_CellSizeIndex + 1) % CellSizeChoices.Length;
            Field = null;
            Status = $"セルサイズ {CellSize * 100f:F1}cm で焼き直し";
            Debug.Log($"[Aquarium] セルサイズを {CellSize * 100f:F1}cm に切り替え。焼き直す");
        }

        /// <summary>
        /// 目標流速を切り替える。**格子は焼き直さない**（境界は変わらないので）。
        /// 場の正規化だけをやり直す。焼き込みが済んでいなければ次回のバケに反映される。
        /// </summary>
        public void CycleTargetSpeed()
        {
            m_SpeedIndex = (m_SpeedIndex + 1) % TargetSpeedChoices.Length;
            if (Field != null)
            {
                var p = FlowParams.Default;
                p.TargetSpeed = TargetSpeed;
                var rebuilt = new FlowField(Field.Grid, p);
                rebuilt.RebuildAll();
                Field = rebuilt;
            }
            Debug.Log($"[Aquarium] 目標流速を {TargetSpeed:F3} m/s に切り替え " +
                $"(1フレーム {TargetSpeed / 72f * 100f:F2}cm @72FPS)");
        }

        void TryBake()
        {
            if (m_Scanner == null || !m_Scanner.IsComplete)
            {
                Status = "スキャン待ち";
                return;
            }
            var scan = m_Scanner.Result;
            if (scan?.Vertices == null || scan.Vertices.Length < 9)
            {
                Status = "メッシュが空";
                return;
            }

            m_Watch.Restart();

            // 頂点はワールド座標なので、アンカーローカルへ移す。
            // アンカー基準で持てば、再センタリングやアンカー復元で格子がずれない
            var toLocal = AnchorWorldToLocal(scan);
            int vertexCount = scan.Vertices.Length / 3;
            var local = new float[scan.Vertices.Length];
            for (int v = 0; v < vertexCount; v++)
            {
                int i = v * 3;
                var p = toLocal.MultiplyPoint3x4(
                    new Vector3(scan.Vertices[i], scan.Vertices[i + 1], scan.Vertices[i + 2]));
                local[i] = p.x; local[i + 1] = p.y; local[i + 2] = p.z;
            }

            // 【格子を部屋の主軸に合わせる】アンカーは設置時のヨー角を持つので
            // （実測 rotY=127°）、アンカーの軸に合わせた箱は部屋が斜めに入って膨らむ。
            // 実測 3.19x2.07x2.58m の部屋に 3.96x2.08x3.99m の格子ができていた
            // （水平面積で約1.8倍、増えた分はほぼ部屋の外）。
            // 水平面の外接箱が最小になるヨー角を探し、そこへ回してから焼く
            RoomYawDegrees = FindBestYaw(local);
            RotateAroundY(local, -RoomYawDegrees);

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int v = 0; v < vertexCount; v++)
            {
                int i = v * 3;
                float px = local[i], py = local[i + 1], pz = local[i + 2];
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                if (pz < minZ) minZ = pz; if (pz > maxZ) maxZ = pz;
            }

            // 部屋の外周にも水を置く余地を1セル分足す（壁面を格子の内側に収める）
            float cell = CellSize;
            var grid = FlowGrid.FromBounds(
                minX - cell, minY - cell, minZ - cell,
                maxX + cell, maxY + cell, maxZ + cell, cell);

            MeshSolidCells = FlowBoundaryBaker.BakeSolid(grid, local, scan.Triangles);

            // 縁を塞ぐ前に、現実の壁・家具だけのマスクを控える（デバッグ表示用）
            MeshSolidMask = new bool[grid.CellCount];
            for (int i = 0; i < grid.CellCount; i++) MeshSolidMask[i] = grid.IsSolidAt(i);
            BakeSerial++;

            FlowBoundaryBaker.SealBorders(grid);
            FlowBoundaryBaker.BakeDistance(grid);

            SolidCells = 0;
            for (int i = 0; i < grid.CellCount; i++) if (grid.IsSolidAt(i)) SolidCells++;

            var fp = FlowParams.Default;
            fp.TargetSpeed = TargetSpeed;
            var field = new FlowField(grid, fp);
            field.RebuildAll();
            Field = field;

            m_Watch.Stop();
            BakeMs = m_Watch.ElapsedMilliseconds;
            Status = "流れ場を構築した";

            float anchorTilt = AnchorTiltDegrees(scan);
            Debug.Log($"[Aquarium] 焼き込み完了: セル {cell * 100f:F1}cm / " +
                $"格子 {grid.Width}x{grid.Height}x{grid.Depth}={grid.CellCount} / " +
                $"固体 {SolidCells}(メッシュ {MeshSolidCells} + 縁 {BorderSolidCells}) / " +
                $"所要 {BakeMs}ms / " +
                $"バウンズ({maxX - minX:F2}x{maxY - minY:F2}x{maxZ - minZ:F2}m) / " +
                $"主軸ヨー {RoomYawDegrees:F1}° / アンカー傾き {anchorTilt:F2}° / " +
                $"床={minY:F2}m 天井={maxY:F2}m 格子Y={grid.OriginY:F2}〜" +
                $"{grid.OriginY + grid.Height * cell:F2}m (アンカー基準)");

            LogHeightProfile(grid, field, minY);
            LogWorldPlacement(scan, grid);
            LogPlaneVsMesh(scan);
        }

        /// <summary>
        /// 水槽が**世界座標のどこに置かれたか**を出す。
        ///
        /// 【なぜ要るか】これまでのログはすべてアンカー基準で、内部座標系どうしの
        /// 整合しか確かめられなかった。世界座標（XROrigin は Floor モードなので Y=0 が床）
        /// に直しておけば、床・天井との対応を後から数値で追える。
        /// ただし**これも内部の値**である。焼き込んだ壁が現実の壁と重なっているかは
        /// <see cref="AquariumDebugView"/> でパススルー越しに見るしかない。
        /// </summary>
        static void LogWorldPlacement(RoomScanner.ScanResult scan, FlowGrid g)
        {
            if (!scan.HasOriginPose)
            {
                Debug.Log("[Aquarium] 世界座標: アンカー未確定（ワールド直置き）");
                return;
            }
            var pose = scan.OriginPoseAtScan;
            var toWorld = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
            var lo = toWorld.MultiplyPoint3x4(new Vector3(g.OriginX, g.OriginY, g.OriginZ));
            var hi = toWorld.MultiplyPoint3x4(new Vector3(
                g.OriginX + g.Width * g.CellSize,
                g.OriginY + g.Height * g.CellSize,
                g.OriginZ + g.Depth * g.CellSize));

            Debug.Log($"[Aquarium] 世界座標 (Y=0 が床): アンカー pos={pose.position:F3} " +
                $"rot={pose.rotation.eulerAngles:F1} / 水槽の高さ範囲 " +
                $"{Mathf.Min(lo.y, hi.y):F3}〜{Mathf.Max(lo.y, hi.y):F3}m / " +
                $"部屋メッシュ {scan.Bounds.min.y:F3}〜{scan.Bounds.max.y:F3}m");
        }

        /// <summary>
        /// **平面データ (Space Setup) とシーンメッシュを突き合わせる。**
        ///
        /// 【なぜ要るか】焼き込みはシーンメッシュ (ARMeshManager) だけを使っている。
        /// 平面 (ARPlaneManager) は利用者が Space Setup で引いた部屋の実体で、
        /// **メッシュとは独立した出所**である。両者がずれていれば、焼き込んだ壁が
        /// 現実の壁とずれている可能性が高い。内部整合の確認ではないが、
        /// 目視の前に機械で分かる範囲を潰しておく。
        /// </summary>
        static void LogPlaneVsMesh(RoomScanner.ScanResult scan)
        {
            var v = scan.Vertices;
            if (v == null || v.Length < 3) return;

            var sb = new System.Text.StringBuilder();
            sb.Append("[Aquarium] 平面 vs メッシュ:");

            if (scan.HasCeiling)
            {
                sb.Append($" 天井 平面={scan.CeilingWorldY:F3}m メッシュ最上={scan.Bounds.max.y:F3}m " +
                    $"差={(scan.Bounds.max.y - scan.CeilingWorldY) * 100f:F1}cm;");
            }
            else
            {
                sb.Append(" 天井平面なし;");
            }

            if (scan.Walls == null || scan.Walls.Count == 0)
            {
                sb.Append(" 壁平面なし");
                Debug.Log(sb.ToString());
                return;
            }

            for (int w = 0; w < scan.Walls.Count; w++)
            {
                var seg = scan.Walls[w];
                // 壁は鉛直。XZ 平面で直線からの符号付き距離を測る
                float nx = -seg.dirZ, nz = seg.dirX;
                var offsets = new System.Collections.Generic.List<float>();
                for (int i = 0; i + 2 < v.Length; i += 3)
                {
                    float dx = v[i] - seg.centerX, dz = v[i + 2] - seg.centerZ;
                    float along = dx * seg.dirX + dz * seg.dirZ;
                    if (Mathf.Abs(along) > seg.halfLength) continue;
                    float perp = dx * nx + dz * nz;
                    if (Mathf.Abs(perp) > 0.30f) continue;     // 壁の近傍だけ見る
                    offsets.Add(perp);
                }
                if (offsets.Count < 20) { sb.Append($" 壁{w}:頂点不足;"); continue; }
                offsets.Sort();
                float median = offsets[offsets.Count / 2];
                sb.Append($" 壁{w}: ずれ中央値={median * 100f:F1}cm (n={offsets.Count});");
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// アンカーの鉛直軸が重力方向からどれだけ傾いているか (度)。
        ///
        /// 【なぜ出すか】焼き込みはアンカーローカルで行い、主軸合わせは
        /// **ヨーしか直さない**。アンカーにピッチ／ロールがあると、格子ごと
        /// 部屋に対して傾き、その上に載る全部（クラゲの姿勢を含む）が傾く。
        /// 2026-08-16 のセッションでクラゲの傾きを疑ったとき、この値がログに無くて
        /// バウンズの高さから間接的に推定するしかなかった（0.4°未満と分かった）。
        /// </summary>
        static float AnchorTiltDegrees(RoomScanner.ScanResult scan)
        {
            if (!scan.HasOriginPose) return 0f;
            return Vector3.Angle(scan.OriginPoseAtScan.rotation * Vector3.up, Vector3.up);
        }

        /// <summary>
        /// 高さ方向の分布を 25cm 帯ごとに出す。
        ///
        /// 【なぜ要るか】2026-08-16 の実機で「部屋の高さ 1.5m 付近までしか水流がない」
        /// と報告されたが、ログには格子のセル数しか無く、**どの高さで流れが
        /// 死んでいるのかを事後に確かめる手段が無かった**。
        /// 固体率（=焼き込みが壁だと判断した割合）と平均流速を並べれば、
        /// 「境界ランプで消えている」のか「そもそも固体になっている」のかが分かる。
        /// 焼き込みと同じく観測時に1回だけ走る。
        /// </summary>
        static void LogHeightProfile(FlowGrid g, FlowField field, float roomFloorY)
        {
            int band = Mathf.Max(1, Mathf.RoundToInt(0.25f / g.CellSize));
            var sb = new System.Text.StringBuilder();
            sb.Append("[Aquarium] 高さ分布 (床からの高さ: 固体率(うち縁) 平均流速m/s):");

            for (int y0 = 0; y0 < g.Height; y0 += band)
            {
                int y1 = Mathf.Min(g.Height, y0 + band);
                int cells = 0, solid = 0, border = 0;
                double sum = 0.0;
                for (int y = y0; y < y1; y++)
                    for (int z = 0; z < g.Depth; z++)
                        for (int x = 0; x < g.Width; x++)
                        {
                            cells++;
                            if (g.IsSolid(x, y, z))
                            {
                                solid++;
                                // 外周リング（水槽の縁）か、メッシュ由来かを分ける。
                                // 縁は1セル厚なので層あたりの割合はほぼ一定になる
                                if (x == 0 || y == 0 || z == 0 || x == g.Width - 1
                                    || y == g.Height - 1 || z == g.Depth - 1) border++;
                                continue;
                            }
                            field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                            sum += Mathf.Sqrt(vx * vx + vy * vy + vz * vz);
                        }

                int fluid = cells - solid;
                float h = g.OriginY + y0 * g.CellSize - roomFloorY;
                sb.Append($" [{h:F2}m {(solid * 100f / cells):F0}%({(border * 100f / cells):F0}%) " +
                    $"{(fluid > 0 ? sum / fluid : 0.0):F3}]");
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// 水平面の外接箱が最小になるヨー角 (度) を探す。
        ///
        /// 部屋は直方体に近いので、90° 周期で対称になる。0〜90° を粗く刻んでから
        /// 最良点の周りを細かく詰める（総当たりで十分速い。頂点16k で数 ms）。
        /// **観測時に1回だけ走る**ので、リプレイ経路には入らない
        /// （焼き込み結果と同じ扱い。追記の決定論の議論を参照）。
        /// </summary>
        internal static float FindBestYaw(float[] verts)
        {
            float best = 0f, bestArea = float.MaxValue;
            for (float deg = 0f; deg < 90f; deg += 2f)
            {
                float a = HorizontalArea(verts, deg);
                if (a < bestArea) { bestArea = a; best = deg; }
            }
            for (float deg = best - 2f; deg <= best + 2f; deg += 0.25f)
            {
                float a = HorizontalArea(verts, deg);
                if (a < bestArea) { bestArea = a; best = deg; }
            }
            return best;
        }

        /// <summary>ヨー角 deg だけ回したときの水平面の外接箱の面積。</summary>
        internal static float HorizontalArea(float[] verts, float deg)
        {
            float rad = deg * Mathf.Deg2Rad;
            float c = Mathf.Cos(-rad), s = Mathf.Sin(-rad);
            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                float x = verts[i] * c - verts[i + 2] * s;
                float z = verts[i] * s + verts[i + 2] * c;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }
            return (maxX - minX) * (maxZ - minZ);
        }

        /// <summary>頂点群を Y 軸まわりに deg だけ回す（その場で書き換える）。</summary>
        internal static void RotateAroundY(float[] verts, float deg)
        {
            float rad = deg * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad), s = Mathf.Sin(rad);
            for (int i = 0; i + 2 < verts.Length; i += 3)
            {
                float x = verts[i], z = verts[i + 2];
                verts[i] = x * c - z * s;
                verts[i + 2] = x * s + z * c;
            }
        }

        /// <summary>
        /// ワールド → アンカーローカルの行列。
        /// スキャン時のアンカーポーズを使う（現在のポーズではない）。
        /// 観測時の座標系で焼き込むことで、後から原点が動いても格子が部屋に貼り付く。
        /// </summary>
        Matrix4x4 AnchorWorldToLocal(RoomScanner.ScanResult scan)
        {
            if (scan.HasOriginPose)
            {
                return Matrix4x4.TRS(scan.OriginPoseAtScan.position,
                                     scan.OriginPoseAtScan.rotation, Vector3.one).inverse;
            }
            var t = m_Origin != null ? m_Origin.OriginTransform : null;
            return t != null ? t.worldToLocalMatrix : Matrix4x4.identity;
        }

        void LogMetrics()
        {
            if (Field == null)
            {
                Debug.Log($"[Aquarium] {Status}");
                return;
            }
            var g = Field.Grid;

            // 最大流速は表示のスケール決めにも使うので毎秒測る
            double max = 0;
            for (int z = 1; z < g.Depth - 1; z += 2)
            {
                for (int y = 1; y < g.Height - 1; y += 2)
                {
                    for (int x = 1; x < g.Width - 1; x += 2)
                    {
                        Field.VelocityAt(x, y, z, out float vx, out float vy, out float vz);
                        double s = Mathf.Sqrt(vx * vx + vy * vy + vz * vz);
                        if (s > max) max = s;
                    }
                }
            }
            MaxSpeed = max;

            // 【パネルに出す値はログにも出す】前回、粒子の描画数がパネルにしか
            // 出ておらず、装着中のユーザーに読み上げてもらう羽目になった
            Debug.Log($"[Aquarium] 格子: セル {CellSize * 100f:F1}cm " +
                $"{g.Width}x{g.Height}x{g.Depth}={g.CellCount} " +
                $"固体={SolidCells}(メッシュ {MeshSolidCells} + 縁 {BorderSolidCells}) " +
                $"焼き込み={BakeMs}ms / ティック={TickMs:F2}ms ({k_TickHz:F0}Hz) " +
                $"目標流速={TargetSpeed:F3}m/s 最大流速={MaxSpeed:F4}m/s " +
                $"粒子={(m_Particles != null ? m_Particles.DrawnParticles : -1)}" +
                $"({(m_Particles != null ? m_Particles.Current.Name : "-")}) " +
                $"tick={Field.TickCount} FPS={1f / Mathf.Max(1e-4f, Time.smoothDeltaTime):F1}");

            var body = m_Jelly != null ? m_Jelly.Body : null;
            if (body != null)
            {
                // 【平均を出す】瞬時値は拍動の位相で 0.19〜0.0004 m/s まで振れ、
                // 1秒間隔の標本化は拍動周期と一致するので位相が固定される。
                // 2026-08-16 のログは常に減衰しきった値だけを載せていた
                Debug.Log($"[Jelly] 傘={body.BellDiameter * 100f:F0}cm 拍動={body.PulseCount} " +
                    $"遊泳={m_Jelly.SwimSpeedMean:F4}m/s 流れ={m_Jelly.DriftSpeedMean:F4}m/s " +
                    $"比={m_Jelly.SwimToFlowRatio:F2} " +
                    $"(瞬時 遊泳={body.SwimSpeed:F4} 流れ={m_Jelly.FlowAt.magnitude:F4}) " +
                    $"位置=({body.X:F2}, {body.Y:F2}, {body.Z:F2}) step={body.StepCount}");
            }
        }
    }
}
