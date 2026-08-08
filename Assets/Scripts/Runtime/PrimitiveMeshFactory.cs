using UnityEngine;

namespace BlockField
{
    /// <summary>
    /// コード生成のプリミティブメッシュ。
    /// GameObject.CreatePrimitive はビルトインの Default-Material (Standardシェーダー、
    /// URPビルド非含有) を初期割当てして実行時エラーを出すため使用しない。
    /// </summary>
    public static class PrimitiveMeshFactory
    {
        /// <summary>1辺1・中心原点の24頂点キューブ。</summary>
        public static Mesh CreateCube()
        {
            // 各面: 法線n・接線u,v は cross(u,v) == -n を満たす組（Unityの時計回り表面規則で外向き法線になる）
            var faces = new (Vector3 n, Vector3 u, Vector3 v)[]
            {
                (Vector3.up, Vector3.right, Vector3.forward),
                (Vector3.down, Vector3.forward, Vector3.right),
                (Vector3.right, Vector3.forward, Vector3.up),
                (Vector3.left, Vector3.up, Vector3.forward),
                (Vector3.forward, Vector3.up, Vector3.right),
                (Vector3.back, Vector3.right, Vector3.up),
            };

            var vertices = new Vector3[24];
            var normals = new Vector3[24];
            var triangles = new int[36];
            for (int f = 0; f < 6; f++)
            {
                var (n, u, v) = faces[f];
                int i = f * 4;
                vertices[i + 0] = (n - u - v) * 0.5f;
                vertices[i + 1] = (n - u + v) * 0.5f;
                vertices[i + 2] = (n + u + v) * 0.5f;
                vertices[i + 3] = (n + u - v) * 0.5f;
                normals[i + 0] = normals[i + 1] = normals[i + 2] = normals[i + 3] = n;

                int t = f * 6;
                triangles[t + 0] = i + 0;
                triangles[t + 1] = i + 1;
                triangles[t + 2] = i + 2;
                triangles[t + 3] = i + 0;
                triangles[t + 4] = i + 2;
                triangles[t + 5] = i + 3;
            }

            var mesh = new Mesh { name = "Cube (generated)" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = triangles;
            return mesh;
        }

        /// <summary>XZ平面のリング（外径1・内径 innerRatio、両面）。レティクル用。</summary>
        public static Mesh CreateRing(float innerRatio = 0.6f, int segments = 32)
        {
            float outer = 0.5f;
            float inner = 0.5f * Mathf.Clamp01(innerRatio);

            var vertices = new Vector3[segments * 2];
            var normals = new Vector3[segments * 2];
            for (int i = 0; i < segments; i++)
            {
                float angle = Mathf.PI * 2f * i / segments;
                float c = Mathf.Cos(angle);
                float s = Mathf.Sin(angle);
                vertices[i * 2] = new Vector3(c * outer, 0f, s * outer);
                vertices[i * 2 + 1] = new Vector3(c * inner, 0f, s * inner);
                normals[i * 2] = normals[i * 2 + 1] = Vector3.up;
            }

            // 両面 (表裏の巻き順を両方登録し、視点によらず見えるようにする)
            var triangles = new int[segments * 12];
            for (int i = 0; i < segments; i++)
            {
                int o0 = i * 2;
                int i0 = i * 2 + 1;
                int o1 = ((i + 1) % segments) * 2;
                int i1 = ((i + 1) % segments) * 2 + 1;

                int t = i * 12;
                // 表
                triangles[t + 0] = o0; triangles[t + 1] = i0; triangles[t + 2] = o1;
                triangles[t + 3] = i0; triangles[t + 4] = i1; triangles[t + 5] = o1;
                // 裏
                triangles[t + 6] = o0; triangles[t + 7] = o1; triangles[t + 8] = i0;
                triangles[t + 9] = i0; triangles[t + 10] = o1; triangles[t + 11] = i1;
            }

            var mesh = new Mesh { name = "Ring (generated)" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = triangles;
            return mesh;
        }
    }
}
