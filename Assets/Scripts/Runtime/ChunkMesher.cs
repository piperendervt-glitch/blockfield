using System.Collections.Generic;
using BlockField.SimCore.Voxel;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlockField
{
    /// <summary>
    /// チャンク1つを可視面のみのメッシュに変換する (Demo 1 B1)。
    /// 可視判定は SimCore の FaceVisibility（VoxelGrid 経由なのでチャンク境界の面も正しく消える）。
    /// ブロック種は頂点色で表現し、マテリアルは頂点色対応の BlockField/OcclusionUnlit 1つに統一。
    /// </summary>
    public static class ChunkMesher
    {
        // 面テーブル。インデックスは FaceVisibility.Directions (+X,-X,+Y,-Y,+Z,-Z) と対応。
        // (n,u,v) は cross(u,v) == -n を満たす組（Unityの時計回り表面規則で外向き法線。
        // PrimitiveMeshFactory.CreateCube と同じ検証済み規則）
        static readonly (Vector3 n, Vector3 u, Vector3 v)[] k_Faces =
        {
            (Vector3.right, Vector3.forward, Vector3.up),
            (Vector3.left, Vector3.up, Vector3.forward),
            (Vector3.up, Vector3.right, Vector3.forward),
            (Vector3.down, Vector3.forward, Vector3.right),
            (Vector3.forward, Vector3.up, Vector3.right),
            (Vector3.back, Vector3.right, Vector3.up),
        };

        /// <summary>ブロック種→頂点色。</summary>
        public static Color32 GetBlockColor(BlockId id)
        {
            switch (id)
            {
                case BlockId.Grass: return new Color32(89, 166, 77, 255);
                case BlockId.Dirt: return new Color32(115, 82, 51, 255);
                case BlockId.Stone: return new Color32(140, 140, 148, 255);
                case BlockId.Sand: return new Color32(217, 199, 140, 255);
                default: return new Color32(255, 0, 255, 255);
            }
        }

        /// <summary>
        /// チャンクの可視面メッシュを生成する。ローカル原点はチャンク左下手前
        /// （セル (0,0,0) の中心が (0, 0.5*blockSize, 0)、Demo 0 の整数セル座標系を踏襲）。
        /// 可視面が1つも無ければ null。
        /// </summary>
        public static Mesh BuildChunkMesh(VoxelGrid grid, Int3 chunkCoord, Chunk chunk, float blockSize)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color32>();
            var triangles = new List<int>();

            var baseCell = new Int3(
                chunkCoord.x * Chunk.Size,
                chunkCoord.y * Chunk.Size,
                chunkCoord.z * Chunk.Size);

            float half = blockSize * 0.5f;

            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int y = 0; y < Chunk.Size; y++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        var id = chunk.Get(x, y, z);
                        if (id == BlockId.Air)
                        {
                            continue;
                        }

                        var worldCell = baseCell + new Int3(x, y, z);
                        var center = new Vector3(x * blockSize, (y + 0.5f) * blockSize, z * blockSize);
                        var color = GetBlockColor(id);

                        for (int f = 0; f < FaceVisibility.FaceCount; f++)
                        {
                            if (!FaceVisibility.IsFaceVisible(grid, worldCell, f))
                            {
                                continue;
                            }

                            var (n, u, v) = k_Faces[f];
                            int baseIndex = vertices.Count;

                            vertices.Add(center + (n - u - v) * half);
                            vertices.Add(center + (n - u + v) * half);
                            vertices.Add(center + (n + u + v) * half);
                            vertices.Add(center + (n + u - v) * half);
                            for (int i = 0; i < 4; i++)
                            {
                                normals.Add(n);
                                colors.Add(color);
                            }

                            triangles.Add(baseIndex + 0);
                            triangles.Add(baseIndex + 1);
                            triangles.Add(baseIndex + 2);
                            triangles.Add(baseIndex + 0);
                            triangles.Add(baseIndex + 2);
                            triangles.Add(baseIndex + 3);
                        }
                    }
                }
            }

            if (vertices.Count == 0)
            {
                return null;
            }

            // 16³ 市松模様の最悪ケースで頂点数が 65535 を超えるため UInt32 固定
            var mesh = new Mesh
            {
                name = $"Chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}",
                indexFormat = IndexFormat.UInt32,
            };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }
    }
}
