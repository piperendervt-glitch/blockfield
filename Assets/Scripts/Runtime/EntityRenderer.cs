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
        [SerializeField] Material m_GrassTuftMaterial;
        [SerializeField] Material m_FlowerMaterial;
        [SerializeField] Material m_SheepMaterial;
        [SerializeField] Material m_PigMaterial;

        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public Material grassTuftMaterial { get => m_GrassTuftMaterial; set => m_GrassTuftMaterial = value; }
        public Material flowerMaterial { get => m_FlowerMaterial; set => m_FlowerMaterial = value; }
        public Material sheepMaterial { get => m_SheepMaterial; set => m_SheepMaterial = value; }
        public Material pigMaterial { get => m_PigMaterial; set => m_PigMaterial = value; }

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
        World m_TrackedWorld;
        GameObject m_Root;
        Mesh m_CubeMesh;
        float m_OffsetX;
        float m_OffsetZ;

        void Awake()
        {
            m_CubeMesh = PrimitiveMeshFactory.CreateCube();
        }

        void Update()
        {
            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            var origin = m_TerrainField != null && m_TerrainField.origin != null
                ? m_TerrainField.origin.OriginTransform
                : null;

            if (world == null || origin == null)
            {
                return;
            }

            // シード切替などでワールドが差し替わったら表示を全リセット
            if (world != m_TrackedWorld)
            {
                ResetVisuals(world, origin);
            }

            // Bボタン: 地形と一緒にエンティティも非表示
            if (m_Root.activeSelf != m_TerrainField.FieldVisible)
            {
                m_Root.SetActive(m_TerrainField.FieldVisible);
            }

            SyncEntities(world);
        }

        void ResetVisuals(World world, Transform origin)
        {
            if (m_Root != null)
            {
                Destroy(m_Root);
            }
            m_Visuals.Clear();

            m_Root = new GameObject("Entities");
            m_Root.transform.SetParent(origin, false);

            // TerrainField のチャンク配置と同じ「原点中心」オフセット
            m_OffsetX = world.Width * 0.5f * k_BlockSize;
            m_OffsetZ = world.Depth * 0.5f * k_BlockSize;

            m_TrackedWorld = world;
        }

        void SyncEntities(World world)
        {
            var entities = world.Entities;
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

        Visual CreateVisual(Entity e)
        {
            var root = new GameObject($"{e.kind} #{e.id}");
            root.transform.SetParent(m_Root.transform, false);
            root.transform.localPosition = CellToLocal(e.cell);

            if (e.IsPlant)
            {
                // 植物: 0.5ブロック大の立方体をセル中央に静的配置
                var mat = e.kind == EntityKind.GrassTuft ? m_GrassTuftMaterial : m_FlowerMaterial;
                AddCube(root.transform, Vector3.zero, Vector3.one * (k_BlockSize * 0.5f), mat);
            }
            else
            {
                // 動物: 胴＋頭のブロック組合せ (Sheep=白 / Pig=ピンク)
                var mat = e.kind == EntityKind.Sheep ? m_SheepMaterial : m_PigMaterial;
                root.transform.localRotation = FacingToRotation(e.facing);
                AddCube(root.transform, new Vector3(0f, 0f, 0f),
                    new Vector3(k_BlockSize * 0.9f, k_BlockSize * 0.9f, k_BlockSize * 1.4f), mat); // 胴
                AddCube(root.transform, new Vector3(0f, k_BlockSize * 0.35f, k_BlockSize * 0.75f),
                    Vector3.one * (k_BlockSize * 0.5f), mat); // 頭
            }

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

        void AddCube(Transform parent, Vector3 localPos, Vector3 scale, Material material)
        {
            var go = new GameObject("Part");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = m_CubeMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        Vector3 CellToLocal(Int3 cell)
        {
            return new Vector3(
                cell.x * k_BlockSize - m_OffsetX,
                (cell.y + 0.5f) * k_BlockSize,
                cell.z * k_BlockSize - m_OffsetZ);
        }

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
