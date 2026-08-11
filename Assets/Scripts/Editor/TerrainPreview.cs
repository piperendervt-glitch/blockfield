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

    /// <summary>
    /// 表示する場 (Demo 8)。実機に行く前に PC で「場と生き物の位置が合うか」を
    /// 目視できるようにするための切替。
    /// </summary>
    enum FieldLayer
    {
        Vegetation = 0, Fear = 1, Prey = 2, Death = 3, Trample = 4,
        // コロニー場 (Demo 8 第4段 K1)。痕跡が薄いので、他の場と同じつもりで
        // 見ると「何も出ていない」と誤読しやすい。1,500ティック回して
        // ようやく数セル〜数十セル立つ
        ColonySheep = 5, ColonyPig = 6, ColonyWolf = 7,
    }

    FieldLayer m_FieldLayer = FieldLayer.Vegetation;

    World m_World;
    GameObject m_Root;
    GameObject m_EntityRoot;
    GameObject m_GrassRoot;
    readonly Material[] m_GrassMaterials = new Material[BlockField.GrassView.StepCount];
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

        bool showVeg = EditorGUILayout.Toggle("場を表示 (E1 / Demo 8)", m_ShowVegetation);
        var layer = (FieldLayer)EditorGUILayout.EnumPopup("表示する場", m_FieldLayer);
        if (showVeg != m_ShowVegetation || layer != m_FieldLayer)
        {
            m_ShowVegetation = showVeg;
            m_FieldLayer = layer;
            if (m_World != null)
            {
                UpdateVegetationOverlay();
                // 草の表示・非表示も切り替わる（オーバーレイ中は草を隠す）
                UpdateGrassDisplay();
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
            EditorGUILayout.LabelField($"Grass: {m_World.VegetationTotal:F0}  Sheep: {m_World.SheepCount}  Pigs: {m_World.PigCount}  Wolves: {m_World.WolfCount}");
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
        Debug.Log($"[TerrainPreview] {ticks}ティック実行 → Tick={m_World.TickCount}, 草={m_World.VegetationTotal:F0}, " +
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

            // 実機と**同じ形**を使う（EntityShape に集約）。
            // 上から見た輪郭で3種を見分けられるかを、ここで実機に行く前に確認する
            go.transform.localRotation = BlockField.EntityShape.FacingToRotation(e.facing);
            BlockField.EntityShape.Build(
                go.transform, e.kind, m_CubeMesh, m_EntityMaterials[e.kind], k_BlockSize);
        }

        UpdateGrassDisplay();
    }

    /// <summary>
    /// 草の表示 (Demo 8.5 K3)。植生場の値から直接、高さ3段階で描く。
    /// 閾値と高さは <see cref="BlockField.GrassView"/> から共用しており、
    /// **実機と同じ見え方**になる（ここで別の基準を使うと、
    /// エディタで確認した内容が実機の判断につながらない）。
    /// </summary>
    void UpdateGrassDisplay()
    {
        if (m_GrassRoot != null)
        {
            DestroyImmediate(m_GrassRoot);
        }

        // 場のオーバーレイ表示中は草を描かない (Demo 8.5 段階4)。
        // 草の房（幅0.55・高さ最大0.75ブロック）がオーバーレイの平板（幅0.9）の
        // 中央を覆い、真上から見ると場が細い枠にしか見えなくなるため
        if (m_ShowVegetation)
        {
            return;
        }

        m_GrassRoot = new GameObject("Grass") { hideFlags = HideFlags.DontSave };
        m_GrassRoot.transform.SetParent(m_Root.transform, false);

        for (int z = 0; z < m_World.Depth; z++)
        {
            for (int x = 0; x < m_World.Width; x++)
            {
                int step = BlockField.GrassView.StepFor(m_World.Vegetation.GetAtColumn(x, z));
                if (step < 0)
                {
                    continue;
                }
                int surfaceY = m_World.GetSurfaceHeight(x, z);
                var (_, height, brightness) = BlockField.GrassView.Step(step);

                var go = new GameObject($"Grass {x},{z}") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(m_GrassRoot.transform, false);
                // 【地表の高さ】表層ブロックの上面は surfaceY * k_BlockSize にある
                // （FieldOverlayView も同じ規約。エンティティは中心が
                // (cell.y + 0.5) にあり、その 0.5 ブロック下が地表）。
                // 立方体メッシュは原点中心なので、地面に「乗せる」には
                // 高さの半分だけ上げる。ここを間違えて 0.5 ブロック下げており、
                // 草が完全に地中に埋まって1つも見えなかった
                go.transform.localPosition = new Vector3(
                    x * k_BlockSize,
                    (surfaceY + height * 0.5f) * k_BlockSize,
                    z * k_BlockSize);

                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = m_CubeMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = GrassMaterial(step);
                go.transform.localScale = new Vector3(
                    k_BlockSize * 0.55f, k_BlockSize * height, k_BlockSize * 0.55f);
                _ = brightness;
            }
        }
    }

    Material GrassMaterial(int step)
    {
        if (m_GrassMaterials[step] == null)
        {
            var (_, _, brightness) = BlockField.GrassView.Step(step);
            var mat = new Material(Shader.Find("BlockField/OcclusionUnlit"))
            {
                name = $"GrassMat{step}",
                hideFlags = HideFlags.DontSave,
            };
            mat.SetColor("_BaseColor", new Color(0.27f * brightness, 0.78f * brightness, 0.24f * brightness));
            m_GrassMaterials[step] = mat;
            m_Generated.Add(mat);
        }
        return m_GrassMaterials[step];
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

        // 表示する場を選ぶ。エンティティと同じ高さ規約（表層高さ）で敷くので、
        // 場と生き物の位置が合っているかを PC 上で目視できる
        ScalarField field = m_FieldLayer switch
        {
            FieldLayer.Fear => m_World.Fear,
            FieldLayer.Prey => m_World.Prey,
            FieldLayer.Death => m_World.Death,
            FieldLayer.Trample => m_World.Trample,
            FieldLayer.ColonySheep => m_World.ColonySheep,
            FieldLayer.ColonyPig => m_World.ColonyPig,
            FieldLayer.ColonyWolf => m_World.ColonyWolf,
            _ => m_World.Vegetation,
        };
        Color32 baseColor = m_FieldLayer switch
        {
            FieldLayer.Fear => new Color32(240, 60, 50, 0),
            FieldLayer.Prey => new Color32(70, 130, 245, 0),
            // マゼンタ。当初は紫 (175,70,235) にしたが、暗いうえに赤(恐怖)と
            // 見分けにくかった。青成分を最大まで振って色相を赤から離す
            FieldLayer.Death => new Color32(230, 40, 255, 0),
            FieldLayer.Trample => new Color32(190, 120, 45, 0), // 茶色（土が見えた道）
            // コロニー場はその種の色。狼は暗い灰のままだと沈むので明度を上げる
            // （FieldOverlayView / SimRunner の Heatmap と同じ色）
            FieldLayer.ColonySheep => new Color32(255, 245, 200, 0),
            FieldLayer.ColonyPig => new Color32(250, 140, 180, 0),
            FieldLayer.ColonyWolf => new Color32(170, 170, 210, 0),
            // 植生場はシアン。緑にすると地形の草ブロックと同系色で
            // 図と地が分離しない（FieldOverlayView と同じ理由・同じ色）
            _ => new Color32(60, 230, 220, 0),
        };

        // 場ごとに値の桁が違うので、表示の濃さは場ごとの基準値で正規化する。
        // 生値をそのまま不透明度にすると、死の場（中央値0.037）は
        // 不透明度3%になって地形が透け、灰色に見える
        float displayScale = EcologyStats.FieldDisplayScale(field.Name);

        for (int z = 0; z < m_World.Depth; z++)
        {
            for (int x = 0; x < m_World.Width; x++)
            {
                float v = field.GetAtColumn(x, z);
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

                // 下限90: 描かれたセルは必ず色として認識できる濃さにする。
                // 「薄すぎて地形と区別できない」より「濃淡が飽和する」方がまし
                float intensity = EcologyStats.FieldDisplayIntensity(v, displayScale);
                var color = new Color32(baseColor.r, baseColor.g, baseColor.b,
                    (byte)(90f + 165f * intensity));
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
        m_GrassRoot = null;
        for (int i = 0; i < m_GrassMaterials.Length; i++)
        {
            m_GrassMaterials[i] = null;
        }
        m_VegetationOverlay = null;
        m_VegetationMaterial = null;
    }

    void OnDisable()
    {
        DestroyPreview();
    }
}
