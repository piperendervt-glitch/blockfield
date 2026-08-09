using BlockField.SimCore.Ecology;
using UnityEngine;

namespace BlockField
{
    /// <summary>
    /// エンティティの見た目（ブロックの組み合わせ）を作る (Demo 8)。
    ///
    /// 【なぜ形で区別するか】色は照明・場のオーバーレイ・飢餓表現と競合し、
    /// 識別子として機能しなかった（実機で全個体が同じに見えて種の判別ができなかった）。
    /// **上から見た輪郭**だけで3種を見分けられる形にする。色分けは併用するが、
    /// 色が使えない状況でも形で分かることを優先する。
    ///
    /// 実機 (EntityRenderer) とエディタプレビュー (TerrainPreview) で
    /// 同じ形を使うため、ここに集約して両方から呼ぶ。
    /// </summary>
    public static class EntityShape
    {
        /// <summary>
        /// エンティティの部品を <paramref name="parent"/> の下に作る。
        /// 位置・向きは呼び出し側が parent に設定する。
        /// </summary>
        public static void Build(
            Transform parent, EntityKind kind, Mesh cubeMesh, Material material, float blockSize)
        {
            switch (kind)
            {
                case EntityKind.GrassTuft:
                case EntityKind.Flower:
                    // 植物: セル中央の小さな立方体（静的）
                    AddCube(parent, cubeMesh, material, Vector3.zero, Vector3.one * (blockSize * 0.5f));
                    break;

                case EntityKind.Sheep:
                    BuildSheep(parent, cubeMesh, material, blockSize);
                    break;

                case EntityKind.Pig:
                    BuildPig(parent, cubeMesh, material, blockSize);
                    break;

                case EntityKind.Wolf:
                    BuildWolf(parent, cubeMesh, material, blockSize);
                    break;
            }
        }

        /// <summary>羊: 基準形。ほぼ正方形に近い胴＋高い位置の頭。上から見ると「中くらいの長方形」。</summary>
        static void BuildSheep(Transform parent, Mesh cube, Material mat, float b)
        {
            AddCube(parent, cube, mat, Vector3.zero, new Vector3(b * 0.95f, b * 0.95f, b * 1.35f));
            // 頭は高く、胴の前方に少しだけ出る
            AddCube(parent, cube, mat, new Vector3(0f, b * 0.45f, b * 0.72f), Vector3.one * (b * 0.55f));
        }

        /// <summary>
        /// 豚: ずんぐり。胴を低く太くし、鼻先を低く前へ突き出す。
        /// 上から見ると「幅の広い長方形＋前に小さな鼻」。羊より明らかに太い。
        /// </summary>
        static void BuildPig(Transform parent, Mesh cube, Material mat, float b)
        {
            AddCube(parent, cube, mat, Vector3.zero, new Vector3(b * 1.25f, b * 0.7f, b * 1.2f));
            // 鼻先: 低い位置に短く突き出す
            AddCube(parent, cube, mat, new Vector3(0f, -b * 0.05f, b * 0.75f),
                new Vector3(b * 0.5f, b * 0.45f, b * 0.45f));
        }

        /// <summary>
        /// 狼: 細長い胴＋低く前に出た頭＋尾＋耳。
        /// 上から見ると「細長い線に尾の突起」で、羊・豚と輪郭が明確に違う。
        /// 恐怖場を書いている個体なので、群れの中から一目で見つけられることを最優先にした。
        /// </summary>
        static void BuildWolf(Transform parent, Mesh cube, Material mat, float b)
        {
            AddCube(parent, cube, mat, Vector3.zero, new Vector3(b * 0.6f, b * 0.6f, b * 1.9f));
            // 頭: 低く前へ
            AddCube(parent, cube, mat, new Vector3(0f, b * 0.1f, b * 1.05f), Vector3.one * (b * 0.55f));
            // 耳2つ: 上から見て頭の左右に飛び出す
            AddCube(parent, cube, mat, new Vector3(-b * 0.22f, b * 0.42f, b * 1.0f), Vector3.one * (b * 0.2f));
            AddCube(parent, cube, mat, new Vector3(b * 0.22f, b * 0.42f, b * 1.0f), Vector3.one * (b * 0.2f));
            // 尾: 後方へ長く伸ばす（上から見た輪郭の決め手）
            AddCube(parent, cube, mat, new Vector3(0f, b * 0.3f, -b * 1.15f),
                new Vector3(b * 0.22f, b * 0.22f, b * 0.7f));
        }

        static void AddCube(Transform parent, Mesh cube, Material material, Vector3 localPos, Vector3 scale)
        {
            var go = new GameObject("Part") { hideFlags = parent.gameObject.hideFlags };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = cube;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        /// <summary>facing (0..3 = +X,+Z,-X,-Z) → yaw 回転。前方は +Z。</summary>
        public static Quaternion FacingToRotation(int facing)
        {
            float yaw = facing switch
            {
                0 => 90f,
                1 => 0f,
                2 => 270f,
                _ => 180f,
            };
            return Quaternion.Euler(0f, yaw, 0f);
        }
    }
}
