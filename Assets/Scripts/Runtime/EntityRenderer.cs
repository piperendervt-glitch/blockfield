using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Voxel;
using UnityEngine;

namespace BlockField
{
    /// <summary>
    /// エンティティ表示 (Demo 2 D5)。World のエンティティリストを毎フレーム描画へ反映する。
    /// シム状態（真実、1Hz更新）と表示（0.3秒の移動・回転補間）を分離し、World 側には触らない。
    /// マテリアルはオクルージョン対応シェーダーのアセットを SceneBootstrap 経由で受け取る
    /// （実行時 Shader.Find は使わない）。
    /// </summary>
    public sealed class EntityRenderer : MonoBehaviour
    {
        const float k_BlockSize = 0.04f;
        const float k_MoveDuration = 0.3f;

        [SerializeField] TerrainField m_TerrainField;
        /// <summary>診断モードの判定に使う（飢餓の色分けは診断時のみ）。</summary>
        [SerializeField] RoomTerrainView m_RoomView;
        [SerializeField] Material m_GrassTuftMaterial;
        [SerializeField] Material m_FlowerMaterial;
        [SerializeField] Material m_SheepMaterial;
        [SerializeField] Material m_PigMaterial;
        [SerializeField] Material m_WolfMaterial;

        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public RoomTerrainView roomView { get => m_RoomView; set => m_RoomView = value; }
        public Material grassTuftMaterial { get => m_GrassTuftMaterial; set => m_GrassTuftMaterial = value; }
        public Material flowerMaterial { get => m_FlowerMaterial; set => m_FlowerMaterial = value; }
        public Material sheepMaterial { get => m_SheepMaterial; set => m_SheepMaterial = value; }
        public Material pigMaterial { get => m_PigMaterial; set => m_PigMaterial = value; }
        public Material wolfMaterial { get => m_WolfMaterial; set => m_WolfMaterial = value; }

        sealed class Visual
        {
            public GameObject root;
            public Int3 lastCell;
            public int lastFacing;
            public Vector3 fromPos;
            public Quaternion fromRot;
            public Vector3 targetPos;
            public Quaternion targetRot;
            public float moveStartTime;
        }

        readonly Dictionary<int, Visual> m_Visuals = new();
        readonly HashSet<int> m_LiveIds = new();
        readonly List<int> m_RemovedIds = new();
        World m_TrackedWorld;
        GameObject m_Root;
        Mesh m_CubeMesh;
        MaterialPropertyBlock m_PropertyBlock;
        bool m_HungerTintApplied;
        bool m_WolfEmphasisApplied;

        /// <summary>診断モードでの狼の拡大率。書き手を見つけやすくするため。</summary>
        const float k_WolfEmphasisScale = 1.6f;
        static readonly int k_BaseColorId = Shader.PropertyToID("_BaseColor");

        void Awake()
        {
            // 面明度差 (E0) を頂点色にベイクしたキューブ（マテリアル側は _VERTEX_COLOR 有効）
            m_CubeMesh = PrimitiveMeshFactory.CreateShadedCube();
        }

        void Update()
        {
            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            // 地形と同じ親に置く。部屋モード (Demo 4.5 G7) ではアンカー相対の部屋ルート、
            // 箱庭モードでは原点配下の箱庭ルートになる
            var root = m_TerrainField != null ? m_TerrainField.TerrainRoot : null;

            if (world == null || root == null)
            {
                return;
            }

            // シード切替などでワールドが差し替わったら表示を全リセット
            // （ルートも作り直されるので親が変わったときも作り直す）
            if (world != m_TrackedWorld || m_Root == null || m_Root.transform.parent != root)
            {
                ResetVisuals(world, root);
            }

            if (m_Root.activeSelf != m_TerrainField.EntitiesVisible)
            {
                m_Root.SetActive(m_TerrainField.EntitiesVisible);
            }

            SyncEntities(world);

            // 飢餓状態の色分けは診断モードのときだけ (Demo 5a)。
            // 通常モードで適用すると公開映像で不自然になるため
            bool diagnostic = m_RoomView != null && m_RoomView.Mode == RoomTerrainView.ViewMode.Diagnostic;
            if (diagnostic || m_HungerTintApplied)
            {
                ApplyHungerTint(world, diagnostic);
                m_HungerTintApplied = diagnostic;
            }

            // 診断モードでは狼を大きくして目立たせる (Demo 8)。
            // 場だけ見せても「誰が書いたか」が分からないため、書き手を見つけやすくする
            if (diagnostic != m_WolfEmphasisApplied)
            {
                ApplyWolfEmphasis(world, diagnostic);
                m_WolfEmphasisApplied = diagnostic;
            }
            else if (diagnostic)
            {
                ApplyWolfEmphasis(world, true); // 新しく湧いた狼にも適用する
            }
        }

        void ResetVisuals(World world, Transform root)
        {
            if (m_Root != null)
            {
                Destroy(m_Root);
            }
            m_Visuals.Clear();

            m_Root = new GameObject("Entities");
            m_Root.transform.SetParent(root, false);

            m_TrackedWorld = world;
        }

        void SyncEntities(World world)
        {
            var entities = world.Entities;

            // 消滅エンティティ（摂食・捕食・餓死）の表示破棄。
            // Demo 3 で World からの削除が導入されたため、生存 id との突合で Visual を破棄する
            m_LiveIds.Clear();
            for (int i = 0; i < entities.Count; i++)
            {
                m_LiveIds.Add(entities[i].id);
            }
            CollectRemovedVisualIds(m_LiveIds, m_Visuals.Keys, m_RemovedIds);
            foreach (int id in m_RemovedIds)
            {
                Destroy(m_Visuals[id].root);
                m_Visuals.Remove(id);
            }

            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (!m_Visuals.TryGetValue(e.id, out var visual))
                {
                    visual = CreateVisual(e);
                    m_Visuals.Add(e.id, visual);
                    continue;
                }

                if (!e.IsAnimal)
                {
                    continue; // 植物は静的
                }

                // シム状態の変化を検知したら補間を開始
                if (e.cell != visual.lastCell || e.facing != visual.lastFacing)
                {
                    visual.fromPos = visual.root.transform.localPosition;
                    visual.fromRot = visual.root.transform.localRotation;
                    visual.targetPos = CellToLocal(e.cell);
                    visual.targetRot = FacingToRotation(e.facing);
                    visual.moveStartTime = Time.time;
                    visual.lastCell = e.cell;
                    visual.lastFacing = e.facing;
                }

                float t = Mathf.Clamp01((Time.time - visual.moveStartTime) / k_MoveDuration);
                visual.root.transform.localPosition = Vector3.Lerp(visual.fromPos, visual.targetPos, t);
                visual.root.transform.localRotation = Quaternion.Slerp(visual.fromRot, visual.targetRot, t);
            }
        }

        /// <summary>
        /// 表示中の id のうち、生存 id 集合に含まれないもの（破棄対象）を result に集める。
        /// EditMode テスト可能な純関数（回帰テスト対象）。
        /// </summary>
        public static void CollectRemovedVisualIds(HashSet<int> liveIds, IEnumerable<int> visualIds, List<int> result)
        {
            result.Clear();
            foreach (int id in visualIds)
            {
                if (!liveIds.Contains(id))
                {
                    result.Add(id);
                }
            }
        }

        Visual CreateVisual(Entity e)
        {
            var root = new GameObject($"{e.kind} #{e.id}");
            root.transform.SetParent(m_Root.transform, false);
            root.transform.localPosition = CellToLocal(e.cell);

            // 形は EntityShape に集約（エディタプレビューと同じ形を使う）。
            // 上から見た輪郭だけで3種を見分けられることを狙っている
            if (!e.IsPlant)
            {
                root.transform.localRotation = FacingToRotation(e.facing);
            }
            EntityShape.Build(root.transform, e.kind, m_CubeMesh, MaterialFor(e.kind), k_BlockSize);

            return new Visual
            {
                root = root,
                lastCell = e.cell,
                lastFacing = e.facing,
                fromPos = root.transform.localPosition,
                fromRot = root.transform.localRotation,
                targetPos = root.transform.localPosition,
                targetRot = root.transform.localRotation,
                moveStartTime = Time.time - k_MoveDuration, // 補間済み扱い
            };
        }

        /// <summary>
        /// 動物の色を hunger で変調する (Demo 5a、診断モード限定)。
        /// 満腹＝通常色 / 空腹＝暗く / 餓死寸前＝赤みを帯びる。
        ///
        /// MaterialPropertyBlock で個体ごとに _BaseColor を差し替えるだけなので、
        /// 共有マテリアルは変更されず、World にも一切触らない（表示と真実の分離）。
        /// </summary>
        void ApplyHungerTint(World world, bool enabled)
        {
            m_PropertyBlock ??= new MaterialPropertyBlock();

            foreach (var e in world.Entities)
            {
                if (!e.IsAnimal || !m_Visuals.TryGetValue(e.id, out var visual) || visual.root == null)
                {
                    continue;
                }

                foreach (var renderer in visual.root.GetComponentsInChildren<MeshRenderer>())
                {
                    if (!enabled)
                    {
                        // 【重要】白を書き込むのではなく**プロパティブロックごと外す**。
                        // 白を入れるとマテリアルの _BaseColor（種の色）を上書きしてしまい、
                        // 通常モードで全個体が白くなる（実機で発生した退行）
                        renderer.SetPropertyBlock(null);
                        continue;
                    }

                    // 種の色に飢餓の係数を**乗算**する。色を置き換えると種が分からなくなる
                    var baseColor = renderer.sharedMaterial != null
                        ? renderer.sharedMaterial.GetColor(k_BaseColorId)
                        : Color.white;
                    var tint = HungerToColor(e.hunger);
                    renderer.GetPropertyBlock(m_PropertyBlock);
                    m_PropertyBlock.SetColor(k_BaseColorId, new Color(
                        baseColor.r * tint.r,
                        baseColor.g * tint.g,
                        baseColor.b * tint.b,
                        baseColor.a));
                    renderer.SetPropertyBlock(m_PropertyBlock);
                }
            }
        }

        /// <summary>
        /// 診断モードで狼だけを拡大する (Demo 8)。
        /// 恐怖場を書いているのが誰かを目で追えるようにするための表示だけの変更で、
        /// 当たり判定もシムの状態も一切変わらない。
        /// </summary>
        void ApplyWolfEmphasis(World world, bool enabled)
        {
            float scale = enabled ? k_WolfEmphasisScale : 1f;
            foreach (var e in world.Entities)
            {
                if (e.kind != EntityKind.Wolf)
                {
                    continue;
                }
                if (!m_Visuals.TryGetValue(e.id, out var visual) || visual.root == null)
                {
                    continue;
                }
                visual.root.transform.localScale = Vector3.one * scale;
            }
        }

        /// <summary>
        /// hunger (0=満腹, 1=餓死) → 乗算色。
        /// 0.0〜0.5 は明度を 1.0→0.5 へ落とし、0.5〜1.0 でさらに緑青を削って赤へ寄せる。
        /// </summary>
        public static Color HungerToColor(float hunger)
        {
            float h = Mathf.Clamp01(hunger);
            float brightness = Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(h * 2f));
            float redness = Mathf.Clamp01((h - 0.5f) * 2f);
            return new Color(
                brightness,
                brightness * (1f - 0.75f * redness),
                brightness * (1f - 0.85f * redness),
                1f);
        }

        /// <summary>種→マテリアル。飢餓の色分けはこの色に**乗算**する。</summary>
        Material MaterialFor(EntityKind kind) => kind switch
        {
            EntityKind.GrassTuft => m_GrassTuftMaterial,
            EntityKind.Flower => m_FlowerMaterial,
            EntityKind.Sheep => m_SheepMaterial,
            EntityKind.Wolf => m_WolfMaterial,
            _ => m_PigMaterial,
        };

        void AddCube(Transform parent, Vector3 localPos, Vector3 scale, Material material)
        {
            var go = new GameObject("Part");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = m_CubeMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        /// <summary>
        /// セル座標 → ローカル位置。地形チャンクと同じ写像を使う必要があるため
        /// TerrainField に委譲する（箱庭は原点中心オフセットあり、部屋はオフセットなし）。
        /// </summary>
        Vector3 CellToLocal(Int3 cell) => m_TerrainField.CellToLocal(cell);

        /// <summary>facing (0..3 = +X,+Z,-X,-Z) → yaw 回転。</summary>
        static Quaternion FacingToRotation(int facing)
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
