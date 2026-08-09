using System.Collections.Generic;
using BlockField.SimCore.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlockField
{
    /// <summary>
    /// 検出した積もり面を色分けの平板マーカーとして1枚のメッシュに焼く (Demo 4.5 G3 診断表示)。
    ///
    /// 面1枚につき平板2枚を重ねる:
    ///   - 外側（セル全面, 4cm角）= **ラベル色の枠**
    ///   - 内側（60%, 2.4cm角）  = 採用/不採用の**塗り色**（緑=採用した最上面 / 青=2面目以降）
    /// 枠と塗りを別メッシュにせず重ね塗りで表現することで、面あたり8頂点に収める。
    ///
    /// MR合成制約（CLAUDE.md）: すべて不透明（alpha=255）。半透明はパススルーと合成されるため使わない。
    /// マーカーの高さには <see cref="SurfaceHit.worldY"/> を使う。worldY は**表示専用**の参考値で、
    /// 地形合成（SnowfallComposer）は整数 cellY のみを読む — M4 の保証はそちらで担保される。
    /// </summary>
    public static class SurfaceMarkerMesher
    {
        /// <summary>採用した最上面（実際に積もらせた面）の塗り色。</summary>
        static readonly Color32 k_AdoptedFill = new Color32(60, 220, 90, 255);

        /// <summary>2面目以降（積もらせなかった面）の塗り色。</summary>
        static readonly Color32 k_UnusedFill = new Color32(60, 120, 240, 255);

        /// <summary>内側の塗りがセル幅に占める比率。</summary>
        const float k_FillRatio = 0.6f;

        /// <summary>面から浮かせる高さ (m)。枠→塗りの順に重ねるので Z ファイトしない。</summary>
        const float k_FrameLift = 0.001f;
        const float k_FillLift = 0.002f;

        /// <summary>ラベル別の枠色。Unknown を赤にして「正体不明の面」が目視で目立つようにする。</summary>
        public static Color32 GetLabelColor(SurfaceLabel label)
        {
            switch (label)
            {
                case SurfaceLabel.Floor: return new Color32(235, 235, 235, 255);  // 白
                case SurfaceLabel.Table: return new Color32(245, 150, 40, 255);   // 橙
                case SurfaceLabel.Couch: return new Color32(60, 220, 220, 255);   // シアン
                case SurfaceLabel.Other: return new Color32(195, 90, 225, 255);   // 紫
                case SurfaceLabel.Unknown: return new Color32(230, 55, 55, 255);  // 赤
                // 除外済みなので通常は現れない。現れたら黒で異常と分かるようにする
                case SurfaceLabel.Ceiling:
                case SurfaceLabel.WallFace:
                default: return new Color32(25, 25, 25, 255);
            }
        }

        /// <summary>
        /// マーカーメッシュを生成する。ローカル原点はセル (0,0) の中心・ワールドY=0 に対応する
        /// （RoomTerrainView がチャンクと同じ基準に置く）。面が無ければ null。
        /// </summary>
        public static Mesh Build(RoomObservation observation, float cellSize)
        {
            if (observation == null)
            {
                return null;
            }

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color32>();
            var triangles = new List<int>();

            float frameHalf = cellSize * 0.5f;
            float fillHalf = cellSize * k_FillRatio * 0.5f;

            for (int z = 0; z < observation.Depth; z++)
            {
                for (int x = 0; x < observation.Width; x++)
                {
                    int count = observation.GetHitCount(x, z);
                    for (int i = 0; i < count; i++)
                    {
                        var hit = observation.GetHit(x, z, i);

                        // リストは cellY 昇順。末尾が最上面 = SnowfallComposer が採用した面
                        bool adopted = i == count - 1;

                        float cx = x * cellSize;
                        float cz = z * cellSize;

                        AddQuad(vertices, normals, colors, triangles,
                            cx, hit.worldY + k_FrameLift, cz, frameHalf, GetLabelColor(hit.label));
                        AddQuad(vertices, normals, colors, triangles,
                            cx, hit.worldY + k_FillLift, cz, fillHalf,
                            adopted ? k_AdoptedFill : k_UnusedFill);
                    }
                }
            }

            if (vertices.Count == 0)
            {
                return null;
            }

            var mesh = new Mesh
            {
                name = "SurfaceMarkers",
                indexFormat = IndexFormat.UInt32, // 面数×8頂点で 65535 を容易に超える
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        /// <summary>上向きの正方形平板を追加する（時計回り表面規則 = ChunkMesher の +Y 面と同じ巻き順）。</summary>
        static void AddQuad(
            List<Vector3> vertices, List<Vector3> normals, List<Color32> colors, List<int> triangles,
            float cx, float cy, float cz, float half, Color32 color)
        {
            int b = vertices.Count;

            // ChunkMesher の +Y 面: n=up, u=right, v=forward、頂点順 [n-u-v, n-u+v, n+u+v, n+u-v]
            vertices.Add(new Vector3(cx - half, cy, cz - half));
            vertices.Add(new Vector3(cx - half, cy, cz + half));
            vertices.Add(new Vector3(cx + half, cy, cz + half));
            vertices.Add(new Vector3(cx + half, cy, cz - half));

            for (int i = 0; i < 4; i++)
            {
                normals.Add(Vector3.up);
                colors.Add(color);
            }

            triangles.Add(b + 0);
            triangles.Add(b + 1);
            triangles.Add(b + 2);
            triangles.Add(b + 0);
            triangles.Add(b + 2);
            triangles.Add(b + 3);
        }
    }
}
