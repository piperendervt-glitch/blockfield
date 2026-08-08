using System.Collections.Generic;
using BlockField;
using BlockField.SimCore.Rng;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// SimCore 地形生成のエディタ内プレビュー (Demo 1 C1)。
/// Scene ビューに一時オブジェクトとしてメッシュを生成する（保存・ビルド対象外）。
/// メッシュ生成は簡易実装（隠面ブロックのみスキップするキューブ並べ）。
/// B1 の面カリングメッシャー実装時に置き換える。
/// </summary>
public class TerrainPreview : EditorWindow
{
    const string k_RootName = "TerrainPreview(EditorOnly)";
    const float k_BlockSize = 0.04f;
    const int k_MaxHeight = 16;

    static readonly int[] k_Sizes = { 50, 100 };

    int m_Seed = 12345;
    int m_SizeIndex = 1;
    float m_ReliefScale = 24f;
    float m_MountainAmplitude = 1f;

    readonly List<Object> m_Generated = new List<Object>();

    [MenuItem("Tools/BlockField/Terrain Preview")]
    static void Open()
    {
        GetWindow<TerrainPreview>("Terrain Preview");
    }

    void OnGUI()
    {
        EditorGUILayout.BeginHorizontal();
        m_Seed = EditorGUILayout.IntField("Seed", m_Seed);
        if (GUILayout.Button("Random", GUILayout.Width(70)))
        {
            // CLAUDE.md: RNG は Mulberry32 のみ（System.Random / UnityEngine.Random 禁止）
            var rng = new Mulberry32((uint)System.DateTime.Now.Ticks);
            m_Seed = (int)(rng.NextUInt() & 0x7FFFFFFFu);
        }
        EditorGUILayout.EndHorizontal();

        m_SizeIndex = GUILayout.Toolbar(m_SizeIndex, new[] { "50x50", "100x100" });
        m_ReliefScale = EditorGUILayout.Slider("起伏スケール", m_ReliefScale, 8f, 64f);
        m_MountainAmplitude = EditorGUILayout.Slider("山振幅", m_MountainAmplitude, 0.2f, 1f);

        EditorGUILayout.Space();
        if (GUILayout.Button("Generate"))
        {
            GeneratePreview();
        }
        if (GUILayout.Button("Clear"))
        {
            DestroyPreview();
        }
    }

    void GeneratePreview()
    {
        DestroyPreview();

        var p = TerrainParams.Default;
        p.seed = (uint)m_Seed;
        p.width = k_Sizes[m_SizeIndex];
        p.depth = k_Sizes[m_SizeIndex];
        p.maxHeight = k_MaxHeight;
        p.reliefScale = m_ReliefScale;
        p.mountainAmplitude = m_MountainAmplitude;

        var grid = TerrainGenerator.Generate(p);

        var root = new GameObject(k_RootName) { hideFlags = HideFlags.DontSave };
        m_Generated.Add(root);

        var cube = PrimitiveMeshFactory.CreateCube();
        m_Generated.Add(cube);

        // ブロック種ごとの結合リスト（露出ブロックのみ。完全に埋まったブロックはスキップ）
        var combines = new Dictionary<BlockId, List<CombineInstance>>
        {
            { BlockId.Grass, new List<CombineInstance>() },
            { BlockId.Dirt, new List<CombineInstance>() },
            { BlockId.Stone, new List<CombineInstance>() },
            { BlockId.Sand, new List<CombineInstance>() },
        };

        var scale = Vector3.one * k_BlockSize;
        for (int z = 0; z < p.depth; z++)
        {
            for (int x = 0; x < p.width; x++)
            {
                for (int y = 0; y < p.maxHeight; y++)
                {
                    var cell = new Int3(x, y, z);
                    var id = grid.Get(cell);
                    if (id == BlockId.Air || !IsExposed(grid, cell))
                    {
                        continue;
                    }

                    var pos = new Vector3(x * k_BlockSize, (y + 0.5f) * k_BlockSize, z * k_BlockSize);
                    combines[id].Add(new CombineInstance
                    {
                        mesh = cube,
                        transform = Matrix4x4.TRS(pos, Quaternion.identity, scale),
                    });
                }
            }
        }

        int blockCount = 0;
        foreach (var pair in combines)
        {
            if (pair.Value.Count == 0)
            {
                continue;
            }
            blockCount += pair.Value.Count;

            var mesh = new Mesh
            {
                name = $"TerrainPreview_{pair.Key}",
                indexFormat = IndexFormat.UInt32,
                hideFlags = HideFlags.DontSave,
            };
            mesh.CombineMeshes(pair.Value.ToArray(), true, true);
            m_Generated.Add(mesh);

            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"))
            {
                name = $"TerrainPreview_{pair.Key}",
                color = GetBlockColor(pair.Key),
                hideFlags = HideFlags.DontSave,
            };
            m_Generated.Add(mat);

            var go = new GameObject(pair.Key.ToString()) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // Sceneビューでプレビュー全体をフレーミング
        float w = p.width * k_BlockSize;
        var bounds = new Bounds(new Vector3(w * 0.5f, k_MaxHeight * k_BlockSize * 0.5f, w * 0.5f),
            new Vector3(w, k_MaxHeight * k_BlockSize, w));
        SceneView.lastActiveSceneView?.Frame(bounds, false);

        Debug.Log($"[TerrainPreview] 生成完了: seed={m_Seed}, {p.width}x{p.depth}, 露出ブロック {blockCount} 個");
    }

    /// <summary>6近傍のいずれかが Air なら露出ブロック。</summary>
    static bool IsExposed(VoxelGrid grid, Int3 cell)
    {
        return grid.Get(new Int3(cell.x + 1, cell.y, cell.z)) == BlockId.Air
            || grid.Get(new Int3(cell.x - 1, cell.y, cell.z)) == BlockId.Air
            || grid.Get(new Int3(cell.x, cell.y + 1, cell.z)) == BlockId.Air
            || grid.Get(new Int3(cell.x, cell.y - 1, cell.z)) == BlockId.Air
            || grid.Get(new Int3(cell.x, cell.y, cell.z + 1)) == BlockId.Air
            || grid.Get(new Int3(cell.x, cell.y, cell.z - 1)) == BlockId.Air;
    }

    static Color GetBlockColor(BlockId id)
    {
        switch (id)
        {
            case BlockId.Grass: return new Color(0.35f, 0.65f, 0.30f);
            case BlockId.Dirt: return new Color(0.45f, 0.32f, 0.20f);
            case BlockId.Stone: return new Color(0.55f, 0.55f, 0.58f);
            case BlockId.Sand: return new Color(0.85f, 0.78f, 0.55f);
            default: return Color.magenta;
        }
    }

    void DestroyPreview()
    {
        // ウィンドウ再起動などで参照が切れている場合に備え、名前でも探して破棄
        var stale = GameObject.Find(k_RootName);
        if (stale != null)
        {
            DestroyImmediate(stale);
        }

        foreach (var obj in m_Generated)
        {
            if (obj != null)
            {
                DestroyImmediate(obj);
            }
        }
        m_Generated.Clear();
    }

    void OnDisable()
    {
        DestroyPreview();
    }
}
