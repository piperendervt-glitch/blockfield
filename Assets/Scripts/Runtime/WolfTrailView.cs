using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Voxel;
using UnityEngine;
using UnityEngine.Rendering;

namespace BlockField
{
    /// <summary>
    /// 狼の直近の軌跡 (Demo 8 H4)。診断モードのときだけ、狼が最近いたセルを
    /// 明るい印で示す。新しいほど明るく、古いほどくすむ。
    ///
    /// 【なぜ必要か — 実機で判明した問題】
    /// 場の**最終状態**だけを見せても「なぜここが赤いのか」が分からない。
    /// 恐怖場は狼が通った痕跡なので、「今まさに書かれた場所」が見えないと
    /// 因果が追えず、けもの道かどうかも判断できなかった。
    ///
    /// 事前登録の案A（直近Nティックの deposit 位置に短寿命マーカー）と
    /// 案C（狼の軌跡を線で描く）は、恐怖場の deposit 位置＝狼の位置なので同じものになる。
    /// 位置履歴を表示側で持つだけで済み、World にもシムにも一切触らないため
    /// この形を選んだ（案Bの「最後に書かれてからの経過ティック」は場と同じ大きさの
    /// 別配列を毎ティック更新する必要があり重い）。
    /// </summary>
    public sealed class WolfTrailView : MonoBehaviour
    {
        /// <summary>残す軌跡の長さ（ティック数）。恐怖場の減衰0.03で痕跡が薄れる時間に合わせる。</summary>
        const int k_TrailTicks = 40;

        const float k_RefreshInterval = 0.5f;

        /// <summary>印の大きさ（セルサイズに対する比）。場のオーバーレイより小さくして重ねて見せる。</summary>
        const float k_MarkerRatio = 0.55f;

        /// <summary>地表から浮かせる高さ (m)。場のオーバーレイ(0.004)より上に出す。</summary>
        const float k_Lift = 0.006f;

        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] RoomTerrainView m_RoomView;
        [SerializeField] Material m_Material;

        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public RoomTerrainView roomView { get => m_RoomView; set => m_RoomView = value; }
        public Material material { get => m_Material; set => m_Material = value; }

        readonly struct Step
        {
            public readonly long tick;
            public readonly Int3 cell;

            public Step(long tick, Int3 cell)
            {
                this.tick = tick;
                this.cell = cell;
            }
        }

        readonly List<Step> m_Steps = new();
        readonly Dictionary<int, Int3> m_LastCellById = new();
        GameObject m_Object;
        Mesh m_Mesh;
        Transform m_TrackedParent;
        long m_LastSampledTick = -1;
        float m_NextRefresh;

        void OnDestroy() => Clear();

        void Update()
        {
            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            if (world == null)
            {
                return;
            }

            SampleWolfPositions(world);

            bool show = m_RoomView != null && m_RoomView.Mode == RoomTerrainView.ViewMode.Diagnostic;
            if (!show)
            {
                if (m_Object != null && m_Object.activeSelf)
                {
                    m_Object.SetActive(false);
                }
                return;
            }

            if (Time.unscaledTime < m_NextRefresh)
            {
                return;
            }
            m_NextRefresh = Time.unscaledTime + k_RefreshInterval;
            Rebuild(world);
        }

        /// <summary>
        /// 狼が動いたセルを記録する。**シムには触れず**、毎ティック1回だけ位置を読む。
        /// 診断モード以外でも記録し続けるのは、モードを切り替えた瞬間から
        /// 直近の軌跡が見えるようにするため。
        /// </summary>
        void SampleWolfPositions(World world)
        {
            if (world.TickCount == m_LastSampledTick)
            {
                return;
            }
            m_LastSampledTick = world.TickCount;

            foreach (var e in world.Entities)
            {
                if (e.kind != EntityKind.Wolf)
                {
                    continue;
                }
                if (m_LastCellById.TryGetValue(e.id, out var last) && last == e.cell)
                {
                    continue; // 動いていないセルは重ねて記録しない
                }
                m_LastCellById[e.id] = e.cell;
                m_Steps.Add(new Step(world.TickCount, e.cell));
            }

            // 古い記録を捨てる
            long cutoff = world.TickCount - k_TrailTicks;
            int drop = 0;
            while (drop < m_Steps.Count && m_Steps[drop].tick < cutoff)
            {
                drop++;
            }
            if (drop > 0)
            {
                m_Steps.RemoveRange(0, drop);
            }
        }

        void Rebuild(World world)
        {
            var parent = m_TerrainField.TerrainRoot;
            if (parent == null)
            {
                return;
            }

            BuildMesh(world);

            if (m_Object == null)
            {
                m_Object = new GameObject("Wolf Trail");
                m_Object.AddComponent<MeshFilter>();
                m_Object.AddComponent<MeshRenderer>().sharedMaterial = m_Material;
            }
            if (m_TrackedParent != parent)
            {
                m_Object.transform.SetParent(parent, false);
                m_Object.transform.localPosition = Vector3.zero;
                m_TrackedParent = parent;
            }
            m_Object.GetComponent<MeshFilter>().sharedMesh = m_Mesh;
            m_Object.SetActive(m_Mesh != null && m_Mesh.vertexCount > 0);
        }

        void BuildMesh(World world)
        {
            const float cell = 0.04f;
            float half = cell * k_MarkerRatio * 0.5f;

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color32>();
            var triangles = new List<int>();

            long now = world.TickCount;
            foreach (var step in m_Steps)
            {
                // 新しいほど明るい白 → 古いほど濃いオレンジへ落とす
                float age = k_TrailTicks > 0 ? Mathf.Clamp01((now - step.tick) / (float)k_TrailTicks) : 0f;
                var c = new Color32(
                    255,
                    (byte)Mathf.Lerp(255f, 90f, age),
                    (byte)Mathf.Lerp(255f, 40f, age),
                    255);

                float cx = step.cell.x * cell;
                float cz = step.cell.z * cell;
                float cy = step.cell.y * cell + k_Lift;

                int i0 = vertices.Count;
                vertices.Add(new Vector3(cx - half, cy, cz - half));
                vertices.Add(new Vector3(cx - half, cy, cz + half));
                vertices.Add(new Vector3(cx + half, cy, cz + half));
                vertices.Add(new Vector3(cx + half, cy, cz - half));
                for (int i = 0; i < 4; i++)
                {
                    normals.Add(Vector3.up);
                    colors.Add(c);
                }
                triangles.Add(i0 + 0); triangles.Add(i0 + 1); triangles.Add(i0 + 2);
                triangles.Add(i0 + 0); triangles.Add(i0 + 2); triangles.Add(i0 + 3);
            }

            if (m_Mesh == null)
            {
                m_Mesh = new Mesh { name = "WolfTrail", indexFormat = IndexFormat.UInt32 };
            }
            m_Mesh.Clear();
            if (vertices.Count == 0)
            {
                return;
            }
            m_Mesh.SetVertices(vertices);
            m_Mesh.SetNormals(normals);
            m_Mesh.SetColors(colors);
            m_Mesh.SetTriangles(triangles, 0);
        }

        void Clear()
        {
            if (m_Object != null)
            {
                Destroy(m_Object);
                m_Object = null;
            }
            if (m_Mesh != null)
            {
                Destroy(m_Mesh);
                m_Mesh = null;
            }
        }
    }
}
