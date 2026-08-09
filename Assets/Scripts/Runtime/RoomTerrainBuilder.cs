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
    /// 本コンポーネントは**表示を行わない**（雪積もり地形の合成は G3）。
    /// 出力される RoomObservation はセル単位の整数高さであり、これが M4 の保証対象となる
    /// リプレイ入力になる（生メッシュのアーカイブは M4 対象外 — RoomScanner のコメント参照）。
    /// </summary>
    public sealed class RoomTerrainBuilder : MonoBehaviour
    {
        [SerializeField] RoomScanner m_Scanner;
        [SerializeField] TerrainField m_TerrainField;

        public RoomScanner scanner { get => m_Scanner; set => m_Scanner = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }

        /// <summary>構築された観測データ。未構築なら null。</summary>
        public RoomObservation Observation { get; private set; }

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
            var stopwatch = Stopwatch.StartNew();

            float cellSize = RoomScanner.CellSize;
            int width = Mathf.Min(RoomScanner.MaxGridSide, Mathf.CeilToInt(scan.Bounds.size.x / cellSize) + 1);
            int depth = Mathf.Min(RoomScanner.MaxGridSide, Mathf.CeilToInt(scan.Bounds.size.z / cellSize) + 1);

            Observation = MultiLayerHeightmap.Build(
                scan.Vertices, scan.Triangles, cellSize,
                scan.Bounds.min.x, scan.Bounds.min.z,
                width, depth,
                scan.LabelResolver);

            stopwatch.Stop();

            int cellsWithHits = Observation.CountCellsWithHits();
            int totalHits = Observation.CountHits();
            Debug.Log($"[RoomTerrain] ハイトマップ構築: {stopwatch.ElapsedMilliseconds}ms " +
                $"grid={width}x{depth} (cell={cellSize}m) " +
                $"面ありセル={cellsWithHits}/{width * depth} 総面数={totalHits} " +
                $"平均面数={(cellsWithHits > 0 ? (float)totalHits / cellsWithHits : 0f):F2}");

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

            // 観測をイベントログへ記録（リプレイ入力。地形合成そのものは G3）
            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            if (world != null)
            {
                world.RecordObservation(Observation);
                Debug.Log($"[RoomTerrain] 観測を EventLog へ記録 (payloadIndex={world.EventLog.Observations.Count - 1}, " +
                    $"hash={Observation.ComputeContentHash():X16})");
            }
            else
            {
                Debug.Log("[RoomTerrain] World 未生成のため EventLog への記録はスキップ（原点未確定）");
            }
        }
    }
}
