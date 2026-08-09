using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Terrain;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace BlockField
{
    /// <summary>
    /// 場のオーバーレイ表示 (Demo 8 H4)。診断モード（右手B）のときだけ、
    /// 各セルの最上面に場の値を色で敷く。
    ///
    /// 【切替の割り当て: 左手Y】
    /// 右手B＝診断モード、左手X＝MR/VR切替 で埋まっているため、空いている
    /// 左手Y (secondaryButton) を場の切替に使う。押すたびに
    /// 植生（緑）→ 恐怖（赤）→ 獲物（青）→ 非表示 を巡回する。
    /// 3つ同時に出すと色が混ざって読めないので1つずつ見る。
    ///
    /// 【表示と真実の分離】場を**読むだけ**で World には触らない。
    /// </summary>
    public sealed class FieldOverlayView : MonoBehaviour
    {
        const float k_ToggleCooldown = 0.4f;

        /// <summary>更新間隔（秒）。場は1Hzで動くので毎秒で足りる。</summary>
        const float k_RefreshInterval = 1f;

        /// <summary>面から浮かせる高さ (m)。マーカー(0.002)より上に出す。</summary>
        const float k_Lift = 0.004f;

        /// <summary>この値未満のセルは描かない（薄い所まで塗ると全面が染まって形が見えない）。</summary>
        const float k_MinValue = 0.02f;

        public enum Layer
        {
            None = 0,
            Vegetation = 1,
            Fear = 2,
            Prey = 3,
        }

        [SerializeField] RoomTerrainBuilder m_Builder;
        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] RoomTerrainView m_RoomView;
        [SerializeField] Material m_Material;

        public RoomTerrainBuilder builder { get => m_Builder; set => m_Builder = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public RoomTerrainView roomView { get => m_RoomView; set => m_RoomView = value; }
        public Material material { get => m_Material; set => m_Material = value; }

        /// <summary>現在表示中の場（パネル表示用）。</summary>
        public Layer Current { get; private set; } = Layer.Fear;

        InputAction m_ToggleAction;
        GameObject m_Object;
        Mesh m_Mesh;
        Transform m_TrackedParent;
        bool m_ToggleRequested;
        float m_LastToggleTime = float.NegativeInfinity;
        float m_NextRefresh;

        void Awake()
        {
            m_ToggleAction = new InputAction("FieldOverlayToggle", InputActionType.Button,
                "<XRController>{LeftHand}/secondaryButton");
            m_ToggleAction.performed += OnTogglePerformed;
        }

        void OnDestroy()
        {
            m_ToggleAction.performed -= OnTogglePerformed;
            m_ToggleAction.Dispose();
            Clear();
        }

        void OnEnable() => m_ToggleAction.Enable();
        void OnDisable() => m_ToggleAction.Disable();

        void OnTogglePerformed(InputAction.CallbackContext context)
        {
            // 左手からの入力であることを確かめる（usage 未設定のデバイス対策）
            if (!ControllerHand.IsLeft(context))
            {
                return;
            }
            m_ToggleRequested = true;
        }

        void Update()
        {
            bool requested = m_ToggleRequested;
            m_ToggleRequested = false;
            if (requested && Time.unscaledTime - m_LastToggleTime >= k_ToggleCooldown)
            {
                m_LastToggleTime = Time.unscaledTime;
                Current = (Layer)(((int)Current + 1) % 4);
                m_NextRefresh = 0f; // 次のフレームで描き直す
                Debug.Log($"[FieldOverlay] 表示: {Current}");
                DebugPanel.Notify($"field {Current}");
            }

            bool diagnostic = m_RoomView != null && m_RoomView.Mode == RoomTerrainView.ViewMode.Diagnostic;
            bool shouldShow = diagnostic && Current != Layer.None;

            if (!shouldShow)
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

            Rebuild();
        }

        void Rebuild()
        {
            var observation = m_Builder != null ? m_Builder.Observation : null;
            var parent = m_TerrainField != null ? m_TerrainField.TerrainRoot : null;
            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            if (observation == null || parent == null || world == null)
            {
                return;
            }

            var field = Current switch
            {
                Layer.Vegetation => (ScalarField)world.Vegetation,
                Layer.Fear => world.Fear,
                Layer.Prey => world.Prey,
                _ => null,
            };
            if (field == null)
            {
                return;
            }

            var color = Current switch
            {
                Layer.Vegetation => new Color32(60, 230, 80, 255),
                Layer.Fear => new Color32(240, 60, 50, 255),
                _ => new Color32(70, 130, 245, 255),
            };

            BuildMesh(observation, field, color, world);

            if (m_Object == null)
            {
                m_Object = new GameObject("Field Overlay");
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
            m_Object.SetActive(m_Mesh != null);
        }

        /// <summary>
        /// 値の大きいセルだけを平板で敷く。明るさで濃さを表す
        /// （アルファは使えない — 半透明はパススルーと合成されるため。CLAUDE.md）。
        /// </summary>
        void BuildMesh(RoomObservation observation, ScalarField field, Color32 baseColor, World world)
        {
            float cell = observation.CellSize;
            float half = cell * 0.45f;

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Color32>();
            var triangles = new List<int>();

            // 場の最大値で正規化して、薄い場でも形が見えるようにする
            var (_, max) = EcologyStats.FieldStats(field);
            if (max <= 0f)
            {
                max = 1f;
            }

            int width = Mathf.Min(observation.Width, field.Width);
            int depth = Mathf.Min(observation.Depth, field.Depth);

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float v = field.GetAtColumn(x, z);
                    if (v < k_MinValue)
                    {
                        continue;
                    }
                    int count = observation.GetHitCount(x, z);
                    if (count == 0)
                    {
                        continue;
                    }

                    float worldY = observation.GetHit(x, z, count - 1).worldY;
                    float t = Mathf.Clamp01(v / max);
                    // 0.35〜1.0 の明度に写す。薄い痕跡も見えるが、濃淡の差は残る
                    float b = Mathf.Lerp(0.35f, 1f, t);
                    var c = new Color32(
                        (byte)(baseColor.r * b), (byte)(baseColor.g * b), (byte)(baseColor.b * b), 255);

                    int i0 = vertices.Count;
                    float cx = x * cell;
                    float cz = z * cell;
                    float cy = worldY + k_Lift;

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
            }

            if (m_Mesh == null)
            {
                m_Mesh = new Mesh { name = "FieldOverlay", indexFormat = IndexFormat.UInt32 };
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
