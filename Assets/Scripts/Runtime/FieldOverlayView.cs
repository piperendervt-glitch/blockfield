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
    /// マーカー → 植生（緑）→ 恐怖（赤）→ 獲物（青）→ 死（紫）→ 非表示 を巡回する。
    /// 同時に出すと色が混ざって読めないので1つずつ見る。
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

        /// <summary>
        /// 診断モード中に見せるもの。左手Yで巡回する。
        ///
        /// <see cref="Markers"/> は積もり面の色分け（緑=採用面 / 青=2面目 / 枠=ラベル）で、
        /// これが**場と同時に出ていると緑どうしが混ざって場が読めない**。
        /// 実機で「非表示にしても緑の枠が残る」と報告されたのはこれが原因だったので、
        /// マーカーも巡回の1状態にして、場を見るときは必ず消えるようにした。
        /// </summary>
        public enum Layer
        {
            /// <summary>積もり面のマーカーのみ（Demo 4.5 の診断表示）。</summary>
            Markers = 0,
            Vegetation = 1,
            Fear = 2,
            Prey = 3,

            /// <summary>死の場 (Demo 8 第2段)。紫。</summary>
            Death = 4,

            /// <summary>何も出さない（地形と生き物だけを見る）。</summary>
            None = 5,
        }

        const int k_LayerCount = 6;

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
                Current = (Layer)(((int)Current + 1) % k_LayerCount);
                m_NextRefresh = 0f; // 次のフレームで描き直す
                Debug.Log($"[FieldOverlay] 表示: {Current}");
                DebugPanel.Notify($"field {Current}");
            }

            bool diagnostic = m_RoomView != null && m_RoomView.Mode == RoomTerrainView.ViewMode.Diagnostic;

            // 積もり面マーカーは「マーカー」状態のときだけ出す。
            // 場を見ているあいだ緑のマーカーが残っていると場が読めない
            if (m_RoomView != null)
            {
                m_RoomView.SetMarkersVisible(diagnostic && Current == Layer.Markers);
            }

            bool shouldShow = diagnostic && Current != Layer.None && Current != Layer.Markers;

            if (!shouldShow)
            {
                // 非アクティブにするだけでなくメッシュも空にする。
                // 「非表示」なのに前の描画が見えている、という状態を作らないため
                if (m_Object != null)
                {
                    if (m_Object.activeSelf)
                    {
                        m_Object.SetActive(false);
                    }
                    if (m_Mesh != null && m_Mesh.vertexCount > 0)
                    {
                        m_Mesh.Clear();
                    }
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
                Layer.Death => world.Death,
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
                // 死の場はマゼンタ。当初は紫 (175,70,235) にしたが、エディタ確認で
                // 暗く沈んで灰色に見え、赤（恐怖）とも見分けにくかった。
                // 青成分を最大まで振って色相を赤から離す
                Layer.Death => new Color32(230, 40, 255, 255),
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

            // 【最大値では正規化しない】死の場は飽和した数セル(0.955)と
            // 大多数の薄いセル(0.037)の差が25倍あり、最大で割ると大多数が
            // 明度0.35付近に潰れて黒く見える（エディタ確認で「灰色に見える」と
            // 報告された症状）。場ごとに決めた基準値で正規化する
            float displayScale = EcologyStats.FieldDisplayScale(field.Name);

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
                    // 【重要】高さは**ワールドの表層**を使う。観測面の worldY を使うと、
                    // その上に積もった雪地形(1〜4ブロック)のぶんだけ下にずれ、
                    // 地表に立つ動植物と一致して見えない（実機で「場と植物が合わない」
                    // と報告された原因）。エンティティと同じ CellToLocal の規約に合わせる
                    int surfaceY = world.GetSurfaceHeight(x, z);
                    if (surfaceY == World.NoSurfaceHeight)
                    {
                        continue;
                    }

                    float localY = surfaceY * cell;
                    float t = EcologyStats.FieldDisplayIntensity(v, displayScale);
                    // 下限0.55: 描かれたセルは必ず色として認識できる明るさにする。
                    // MRではアルファが使えない（パススルーと合成される）ので
                    // 濃さは明度だけで表す。暗い側に振ると黒＝背景と区別できない
                    float b = Mathf.Lerp(0.55f, 1f, t);
                    var c = new Color32(
                        (byte)(baseColor.r * b), (byte)(baseColor.g * b), (byte)(baseColor.b * b), 255);

                    int i0 = vertices.Count;
                    float cx = x * cell;
                    float cz = z * cell;
                    float cy = localY + k_Lift;

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
