using System.Collections.Generic;
using System.Diagnostics;
using BlockField.SimCore.Terrain;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BlockField
{
    /// <summary>
    /// 多層ハイトマップ化の Runtime 接続 (Demo 4.5 G2)。
    /// RoomScanner のスキャン完了を検知し、MultiLayerHeightmap で RoomObservation を構築して
    /// 統計を [RoomTerrain] タグでログ出力する。
    ///
    /// 本コンポーネントは**表示を行わない**（雪積もり地形の合成と表示は RoomTerrainView / G3）。
    /// 出力される RoomObservation はセル単位の整数高さであり、これが M4 の保証対象となる
    /// リプレイ入力になる（生メッシュのアーカイブは M4 対象外 — RoomScanner のコメント参照）。
    /// </summary>
    public sealed class RoomTerrainBuilder : MonoBehaviour
    {
        [SerializeField] RoomScanner m_Scanner;
        [SerializeField] TerrainField m_TerrainField;

        public RoomScanner scanner { get => m_Scanner; set => m_Scanner = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }

        /// <summary>
        /// 構築された観測データ。未構築なら null。
        ///
        /// 【保持の契約】一度作ったら破棄しない。TerrainField がシード巡回のたびに
        /// これを読み直して地形を作り直すほか、VRモード (Demo 4.5b) では現実の部屋を
        /// 丸ごとボクセル化する入力として再利用する。
        /// </summary>
        public RoomObservation Observation { get; private set; }

        /// <summary>
        /// スキャン結果（ワールド座標の生メッシュとアンカーポーズ）。未スキャンなら null。
        ///
        /// 【保持の契約】ARMeshManager はスキャン後に停止するが、頂点・三角形は
        /// マネージド配列へコピー済みなので参照は生き続ける。
        /// VRモードで観測グリッドより細かい／粗いボクセル化をやり直す際の入力になるため、
        /// **CPU データを破棄しない**。scanner を辿らずに済むようここから公開する。
        /// </summary>
        public RoomScanner.ScanResult Scan { get; private set; }

        /// <summary>構築統計（面数分布・除外内訳）。未構築なら null。パネル表示に使う。</summary>
        public MultiLayerHeightmap.BuildStats Stats { get; private set; }

        /// <summary>面を持つセル数（パネル表示用）。</summary>
        public int CellsWithHits { get; private set; }

        /// <summary>検出した面の総数（パネル表示用）。</summary>
        public int TotalHits { get; private set; }

        bool m_Built;

        void Update()
        {
            if (m_Built || m_Scanner == null || !m_Scanner.IsComplete)
            {
                return;
            }
            m_Built = true;
            Build(m_Scanner.Result);
        }

        void Build(RoomScanner.ScanResult scan)
        {
            Scan = scan;
            var stopwatch = Stopwatch.StartNew();

            float cellSize = RoomScanner.CellSize;
            int width = Mathf.Min(RoomScanner.MaxGridSide, Mathf.CeilToInt(scan.Bounds.size.x / cellSize) + 1);
            int depth = Mathf.Min(RoomScanner.MaxGridSide, Mathf.CeilToInt(scan.Bounds.size.z / cellSize) + 1);

            Observation = MultiLayerHeightmap.Build(
                scan.Vertices, scan.Triangles, cellSize,
                scan.Bounds.min.x, scan.Bounds.min.z,
                width, depth,
                scan.LabelResolver,
                out var stats);

            // 壁の Boundary 化 (G4)。観測データ側に通行不可セルとして立てる
            // （セル単位の bool なので ContentHash に入れても M4 の保証を壊さない）
            //
            // 平面由来の壁は部屋内部の仕切りに効くが、それだけでは閉じない
            // （実測: WallFace 平面が4枚しかなく、窓・ドア・家具の陰で切れ目ができた）。
            // M2 の目的は「動物が部屋の外に漏れない」ことなので、外周そのものも柵にする。
            int planeWallCells = WallRasterizer.Rasterize(Observation, scan.Walls);
            int perimeterCells = WallRasterizer.SealPerimeter(Observation);
            int wallCells = planeWallCells + perimeterCells;

            // 天井の高さ (Demo 4.5b V2)。観測データにはセル単位の整数で持たせる
            if (scan.HasCeiling)
            {
                int ceilingCellY = Mathf.FloorToInt(scan.CeilingWorldY / cellSize);
                Observation.SetCeiling(ceilingCellY);
            }

            stopwatch.Stop();

            Stats = stats;
            int cellsWithHits = Observation.CountCellsWithHits();
            int totalHits = Observation.CountHits();
            CellsWithHits = cellsWithHits;
            TotalHits = totalHits;
            Debug.Log($"[RoomTerrain] ハイトマップ構築: {stopwatch.ElapsedMilliseconds}ms " +
                $"grid={width}x{depth} (cell={cellSize}m) " +
                $"面ありセル={cellsWithHits}/{width * depth} 総面数={totalHits} " +
                $"平均面数={(cellsWithHits > 0 ? (float)totalHits / cellsWithHits : 0f):F2}");

            // 過検出の切り分け用（面数分布・除外理由の内訳・巻き順の計測）
            Debug.Log($"[RoomTerrain] 内訳: {stats}");

            Debug.Log($"[RoomTerrain] 壁の Boundary 化 (G4): 壁平面={scan.Walls?.Count ?? 0} " +
                $"平面由来={planeWallCells} 外周={perimeterCells} 合計={wallCells} " +
                $"(厚み={WallRasterizer.ThicknessMeters}m) " +
                $"天井={(Observation.HasCeiling ? $"cellY={Observation.CeilingCellY} ({scan.CeilingWorldY:F2}m)" : "未取得")}");

            // 巻き順の確定: 符号ありが極端に少なければメッシュの巻き順が逆
            if (stats.UpwardSignedHits == 0 && stats.UpwardAbsHits > 0)
            {
                Debug.LogWarning($"[RoomTerrain] 符号ありの上向き面が0件、絶対値では{stats.UpwardAbsHits}件。" +
                    "メッシュの巻き順が逆の可能性が高い（法線の符号反転が必要）。");
            }
            else if (stats.UpwardAbsHits > 0)
            {
                float ratio = (float)stats.UpwardSignedHits / stats.UpwardAbsHits;
                Debug.Log($"[RoomTerrain] 巻き順の計測: 符号あり/絶対値 = {stats.UpwardSignedHits}/{stats.UpwardAbsHits} " +
                    $"({ratio:P0})。おおむね半分なら巻き順は正しく、上向き面のみが採用されている。");
            }

            // 代表セル（最多面数）の面高さ — 多層化が効いているかを実機ログで確認する
            var most = Observation.FindCellWithMostHits();
            if (most.count > 0)
            {
                var parts = new List<string>();
                for (int i = 0; i < most.count; i++)
                {
                    var h = Observation.GetHit(most.x, most.z, i);
                    parts.Add($"cellY={h.cellY} worldY={h.worldY:F2} floorId={h.floorId} label={h.label}");
                }
                Debug.Log($"[RoomTerrain] 最多面セル ({most.x},{most.z}) = {most.count}面: {string.Join(" | ", parts)}");
            }
            else
            {
                Debug.LogWarning("[RoomTerrain] 積もり面が1つも見つからなかった（上向き面の検出に失敗している可能性）");
            }

            // 観測の EventLog への記録は TerrainField が行う。
            // 部屋モード (G7) では World そのものがこの観測から作られるため、
            // World の生成前にここで記録することはできない。
            Debug.Log($"[RoomTerrain] 観測ハッシュ={Observation.ComputeContentHash():X16}（記録は TerrainField）");
        }
    }
}
