using System.Collections.Generic;
using BlockField;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Rng;
using BlockField.SimCore.Terrain;
using BlockField.SimCore.Voxel;
using UnityEditor;
using UnityEngine;

/// <summary>
/// SimCore のエディタ内プレビュー (Demo 1 C1 / Demo 2 D6)。
/// 地形は ChunkMesher（実機と同一の面カリング＋面明度差）で表示し、
/// シムティックを回してエンティティの配置を Scene ビューで確認できる。
/// 全オブジェクトは HideFlags.DontSave（保存・ビルド対象外）。
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
    bool m_FaceShading = true;
    bool m_AmbientOcclusion = true;
    bool m_ShowVegetation;

    World m_World;
    GameObject m_Root;
    GameObject m_EntityRoot;
    GameObject m_VegetationOverlay;
    Mesh m_CubeMesh;
    Material m_TerrainMaterial;
    Material m_VegetationMaterial;
    readonly Dictionary<EntityKind, Material> m_EntityMaterials = new Dictionary<EntityKind, Material>();
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
        m_FaceShading = EditorGUILayout.Toggle("面明度差 (D0)", m_FaceShading);
        m_AmbientOcclusion = EditorGUILayout.Toggle("頂点AO (E0)", m_AmbientOcclusion);

        bool showVeg = EditorGUILayout.Toggle("植生場表示 (E1)", m_ShowVegetation);
        if (showVeg != m_ShowVegetation)
        {
            m_ShowVegetation = showVeg;
            if (m_World != null)
            {
                UpdateVegetationOverlay();
            }
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Generate"))
        {
            GeneratePreview();
        }

        using (new EditorGUI.DisabledScope(m_World == null))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Simulate 10 ticks"))
            {
                Simulate(10);
            }
            if (GUILayout.Button("Simulate 100 ticks"))
            {
                Simulate(100);
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Reset (同一シードで再生成)"))
            {
                GeneratePreview();
            }

            // Demo 4 F5: イベント注入（次の Simulate の先頭ティックで適用される）
            if (GUILayout.Button("Inject 10 random place/break"))
            {
                InjectRandomActions(10);
            }
        }

        if (GUILayout.Button("Clear"))
        {
            DestroyPreview();
        }

        EditorGUILayout.Space();
        if (m_World != null)
        {
            EditorGUILayout.LabelField($"Tick: {m_World.TickCount}");
            EditorGUILayout.LabelField($"Plants: {m_World.PlantCount}  Sheep: {m_World.SheepCount}  Pigs: {m_World.PigCount}  Wolves: {m_World.WolfCount}");
            EditorGUILayout.LabelField($"累計 — 餓死: {m_World.StarvationCount}  捕食: {m_World.PredationCount}  出生: {m_World.BirthCount}");

            if (GUILayout.Button("Export CSV (Logs/population_preview.csv)"))
            {
                System.IO.Directory.CreateDirectory("Logs");
                System.IO.File.WriteAllText("Logs/population_preview.csv", m_World.PopulationLog.ToCsv());
                Debug.Log($"[TerrainPreview] CSV出力: Logs/population_preview.csv ({m_World.PopulationLog.Count}行)");
            }
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

        m_World = World.Create(p);

        m_Root = new GameObject(k_RootName) { hideFlags = HideFlags.DontSave };
        m_Generated.Add(m_Root);

        m_CubeMesh = PrimitiveMeshFactory.CreateCube();
        m_CubeMesh.hideFlags = HideFlags.DontSave;
        m_Generated.Add(m_CubeMesh);

        // 実機と同じ頂点色シェーダー（オクルージョンはエディタでは不活性）
        m_TerrainMaterial = new Material(Shader.Find("BlockField/OcclusionUnlit"))
        {
            name = "TerrainPreviewMat",
            hideFlags = HideFlags.DontSave,
        };
        m_TerrainMaterial.SetFloat("_UseVertexColor", 1f);
        m_TerrainMaterial.EnableKeyword("_VERTEX_COLOR");
        m_Generated.Add(m_TerrainMaterial);

        foreach (var pair in m_World.Grid.Chunks)
        {
            // Player 出所ブロックは青灰色で視認（エディタプレビューのみ）
            var mesh = ChunkMesher.BuildChunkMesh(m_World.Grid, pair.Key, pair.Value, k_BlockSize,
                m_FaceShading, m_AmbientOcclusion, tintPlayerBlocks: true);
            if (mesh == null)
            {
                continue;
            }
            mesh.hideFlags = HideFlags.DontSave;
            m_Generated.Add(mesh);

            var go = new GameObject($"Chunk {pair.Key}") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(m_Root.transform, false);
            go.transform.localPosition = new Vector3(
                pair.Key.x * Chunk.Size * k_BlockSize,
                pair.Key.y * Chunk.Size * k_BlockSize,
                pair.Key.z * Chunk.Size * k_BlockSize);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m_TerrainMaterial;
        }

        m_EntityRoot = new GameObject("Entities") { hideFlags = HideFlags.DontSave };
        m_EntityRoot.transform.SetParent(m_Root.transform, false);

        // 植生場オーバーレイ用の透過・頂点色マテリアル
        m_VegetationMaterial = new Material(Shader.Find("BlockField/OcclusionUnlit"))
        {
            name = "VegetationOverlayMat",
            hideFlags = HideFlags.DontSave,
            renderQueue = 3000,
        };
        m_VegetationMaterial.SetFloat("_UseVertexColor", 1f);
        m_VegetationMaterial.EnableKeyword("_VERTEX_COLOR");
        m_VegetationMaterial.SetFloat("_Surface", 1f);
        m_VegetationMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m_VegetationMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m_VegetationMaterial.SetFloat("_ZWrite", 0f);
        m_VegetationMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m_Generated.Add(m_VegetationMaterial);

        CreateEntityMaterials();
        UpdateEntityDisplay();
        UpdateVegetationOverlay();

        float w = p.width * k_BlockSize;
        var bounds = new Bounds(new Vector3(w * 0.5f, k_MaxHeight * k_BlockSize * 0.5f, w * 0.5f),
            new Vector3(w, k_MaxHeight * k_BlockSize, w));
        SceneView.lastActiveSceneView?.Frame(bounds, false);

        Debug.Log($"[TerrainPreview] 生成完了: seed={m_Seed}, {p.width}x{p.depth}, 面明度差={(m_FaceShading ? "ON" : "OFF")}");
    }

    /// <summary>
    /// ランダムな Place/Break を注入 (Demo 4 F5)。Place は表層の上（多くは有効）、
    /// Break は表層ブロック。次の Simulate で適用され、Player ブロックは青灰色で描かれる。
    /// </summary>
    void InjectRandomActions(int count)
    {
        var rng = new Mulberry32((uint)System.DateTime.Now.Ticks);
        for (int i = 0; i < count; i++)
        {
            int x = rng.Range(0, m_World.Width);
            int z = rng.Range(0, m_World.Depth);
            int h = m_World.GetSurfaceHeight(x, z);

            if (rng.Range(0, 2) == 0)
            {
                m_World.EnqueuePlayerAction(SimEventType.PlayerPlace, new Int3(x, h, z), BlockId.Stone);
            }
            else
            {
                m_World.EnqueuePlayerAction(SimEventType.PlayerBreak, new Int3(x, h - 1, z), BlockId.Air);
            }
        }
        Debug.Log($"[TerrainPreview] {count}件の Place/Break を注入した。Simulate で適用される。");
    }

    /// <summary>DirtyChunks を反映して地形メッシュを再構築（エディタは全再構築で簡略化）。</summary>
    void RefreshTerrainMeshes()
    {
        var dirtyBuffer = new List<Int3>();
        if (!m_World.ConsumeDirtyChunks(dirtyBuffer))
        {
            return;
        }

        // 既存チャンク表示を破棄して作り直す（プレビューは規模が小さいので全再構築で十分）
        for (int i = m_Root.transform.childCount - 1; i >= 0; i--)
        {
            var child = m_Root.transform.GetChild(i).gameObject;
            if (child.name.StartsWith("Chunk "))
            {
                DestroyImmediate(child);
            }
        }
        foreach (var pair in m_World.Grid.Chunks)
        {
            var mesh = ChunkMesher.BuildChunkMesh(m_World.Grid, pair.Key, pair.Value, k_BlockSize,
                m_FaceShading, m_AmbientOcclusion, tintPlayerBlocks: true);
            if (mesh == null)
            {
                continue;
            }
            mesh.hideFlags = HideFlags.DontSave;
            m_Generated.Add(mesh);

            var go = new GameObject($"Chunk {pair.Key}") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(m_Root.transform, false);
            go.transform.localPosition = new Vector3(
                pair.Key.x * Chunk.Size * k_BlockSize,
                pair.Key.y * Chunk.Size * k_BlockSize,
                pair.Key.z * Chunk.Size * k_BlockSize);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m_TerrainMaterial;
        }
    }

    void Simulate(int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            Simulation.Tick(m_World, m_World.Rng);
        }
        UpdateEntityDisplay();
        UpdateVegetationOverlay();
        RefreshTerrainMeshes();
        Debug.Log($"[TerrainPreview] {ticks}ティック実行 → Tick={m_World.TickCount}, 植物={m_World.PlantCount}, " +
            $"羊={m_World.SheepCount}, 豚={m_World.PigCount}, 狼={m_World.WolfCount}, " +
            $"餓死={m_World.StarvationCount}, 捕食={m_World.PredationCount}, 出生={m_World.BirthCount}");
        Repaint();
    }

    void UpdateEntityDisplay()
    {
        // 毎回作り直す（数百個程度なのでエディタ用途では十分軽い）
        for (int i = m_EntityRoot.transform.childCount - 1; i >= 0; i--)
        {
            DestroyImmediate(m_EntityRoot.transform.GetChild(i).gameObject);
        }

        foreach (var e in m_World.Entities)
        {
            var go = new GameObject($"{e.kind} #{e.id}") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(m_EntityRoot.transform, false);
            go.transform.localPosition = new Vector3(
                e.cell.x * k_BlockSize,
                (e.cell.y + 0.5f) * k_BlockSize,
                e.cell.z * k_BlockSize);

            if (e.IsPlant)
            {
                go.transform.localScale = Vector3.one * (k_BlockSize * 0.5f);
            }
            else
            {
                // 動物: 直方体＋facing で向きを可視化 (0..3 = +X,+Z,-X,-Z)
                go.transform.localScale = new Vector3(k_BlockSize, k_BlockSize * 0.7f, k_BlockSize * 1.3f);
                float yaw = e.facing switch { 0 => 90f, 1 => 0f, 2 => 270f, _ => 180f };
                go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            }

            go.AddComponent<MeshFilter>().sharedMesh = m_CubeMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m_EntityMaterials[e.kind];
        }
    }

    /// <summary>植生場の値を地表の半透明緑オーバーレイで可視化する（値→アルファ）。</summary>
    void UpdateVegetationOverlay()
    {
        if (m_VegetationOverlay != null)
        {
            DestroyImmediate(m_VegetationOverlay);
            m_VegetationOverlay = null;
        }

        if (!m_ShowVegetation || m_World == null)
        {
            return;
        }

        var vertices = new List<Vector3>();
        var colors = new List<Color32>();
        var triangles = new List<int>();
        const float lift = 0.003f; // 地表とのZファイティング回避

        for (int z = 0; z < m_World.Depth; z++)
        {
            for (int x = 0; x < m_World.Width; x++)
            {
                float v = m_World.Vegetation.Values.Get(x, z);
                if (v < 0.02f)
                {
                    continue;
                }

                float y = m_World.GetSurfaceHeight(x, z) * k_BlockSize + lift;
                float half = k_BlockSize * 0.5f;
                float cx = x * k_BlockSize;
                float cz = z * k_BlockSize;
                int b = vertices.Count;

                vertices.Add(new Vector3(cx - half, y, cz - half));
                vertices.Add(new Vector3(cx - half, y, cz + half));
                vertices.Add(new Vector3(cx + half, y, cz + half));
                vertices.Add(new Vector3(cx + half, y, cz - half));

                var color = new Color32(30, 220, 60, (byte)(Mathf.Clamp01(v) * 200f));
                for (int i = 0; i < 4; i++)
                {
                    colors.Add(color);
                }

                triangles.Add(b + 0); triangles.Add(b + 1); triangles.Add(b + 2);
                triangles.Add(b + 0); triangles.Add(b + 2); triangles.Add(b + 3);
            }
        }

        if (vertices.Count == 0)
        {
            return;
        }

        var mesh = new Mesh
        {
            name = "VegetationOverlay",
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            hideFlags = HideFlags.DontSave,
        };
        mesh.SetVertices(vertices);
        mesh.SetColors(colors);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        m_Generated.Add(mesh);

        m_VegetationOverlay = new GameObject("VegetationOverlay") { hideFlags = HideFlags.DontSave };
        m_VegetationOverlay.transform.SetParent(m_Root.transform, false);
        m_VegetationOverlay.AddComponent<MeshFilter>().sharedMesh = mesh;
        m_VegetationOverlay.AddComponent<MeshRenderer>().sharedMaterial = m_VegetationMaterial;
    }

    void CreateEntityMaterials()
    {
        m_EntityMaterials.Clear();
        AddEntityMaterial(EntityKind.GrassTuft, new Color(0.25f, 0.8f, 0.25f));
        AddEntityMaterial(EntityKind.Flower, new Color(0.95f, 0.85f, 0.25f));
        AddEntityMaterial(EntityKind.Sheep, new Color(0.95f, 0.95f, 0.95f));
        AddEntityMaterial(EntityKind.Pig, new Color(0.95f, 0.65f, 0.7f));
        AddEntityMaterial(EntityKind.Wolf, new Color(0.55f, 0.55f, 0.6f));
    }

    void AddEntityMaterial(EntityKind kind, Color color)
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"))
        {
            name = $"EntityPreview_{kind}",
            color = color,
            hideFlags = HideFlags.DontSave,
        };
        m_EntityMaterials[kind] = mat;
        m_Generated.Add(mat);
    }

    void DestroyPreview()
    {
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
        m_EntityMaterials.Clear();
        m_World = null;
        m_Root = null;
        m_EntityRoot = null;
        m_VegetationOverlay = null;
        m_VegetationMaterial = null;
    }

    void OnDisable()
    {
        DestroyPreview();
    }
}
