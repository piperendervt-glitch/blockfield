using System.Collections.Generic;
using BlockField.SimCore.Ecology;
using BlockField.SimCore.Voxel;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BlockField
{
    /// <summary>
    /// 設置・破壊の操作 (Demo 4 F3)。
    /// 右コントローラの前方レイを VoxelGrid へ DDA（VoxelRaycast）で判定し、
    /// 破壊対象（ヒットセル）を赤系、設置対象（ヒット面の隣接空セル）を白系でハイライト。
    /// トリガー=設置（Stone固定）/ グリップ=破壊。操作は EnqueuePlayerAction に積むのみ
    /// （適用は次Tick先頭）。1Hzの適用遅延を隠すため予約の仮表示を出し、
    /// Tick 適用（チャンク再メッシュ）で本表示に置き換わる — World の真実には触れない。
    /// </summary>
    public sealed class BlockInteractor : MonoBehaviour
    {
        const float k_BlockSize = 0.04f;
        const float k_MaxRayDistanceMeters = 2f;
        const float k_ActionCooldown = 0.3f;
        const float k_PendingTimeout = 2.5f;

        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] Transform m_TrackingSpace;
        [SerializeField] Material m_BreakHighlightMaterial;
        [SerializeField] Material m_PlaceHighlightMaterial;
        [SerializeField] Material m_PendingPlaceMaterial;
        [SerializeField] Material m_PendingBreakMaterial;

        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public Transform trackingSpace { get => m_TrackingSpace; set => m_TrackingSpace = value; }
        public Material breakHighlightMaterial { get => m_BreakHighlightMaterial; set => m_BreakHighlightMaterial = value; }
        public Material placeHighlightMaterial { get => m_PlaceHighlightMaterial; set => m_PlaceHighlightMaterial = value; }
        public Material pendingPlaceMaterial { get => m_PendingPlaceMaterial; set => m_PendingPlaceMaterial = value; }
        public Material pendingBreakMaterial { get => m_PendingBreakMaterial; set => m_PendingBreakMaterial = value; }

        sealed class PendingVisual
        {
            public GameObject go;
            public Int3 cell;
            public bool isPlace; // true=設置予約(セルが非Airになったら消す) / false=破壊予約(Airになったら消す)
            public float startTime;
        }

        InputAction m_PositionAction;
        InputAction m_RotationAction;
        InputAction m_TriggerAction;
        InputAction m_GripAction;
        bool m_PlaceRequested;
        bool m_BreakRequested;
        float m_LastActionTime = float.NegativeInfinity;

        GameObject m_BreakHighlight;
        GameObject m_PlaceHighlight;
        Mesh m_CubeMesh;
        readonly List<PendingVisual> m_Pending = new();

        bool m_HasTarget;
        Int3 m_TargetCell;
        Int3 m_PlaceCell;
        bool m_CanPlace;

        void Awake()
        {
            m_CubeMesh = PrimitiveMeshFactory.CreateCube();

            m_PositionAction = new InputAction("RightHandPosition", InputActionType.Value,
                "<XRController>{RightHand}/devicePosition", expectedControlType: "Vector3");
            m_RotationAction = new InputAction("RightHandRotation", InputActionType.Value,
                "<XRController>{RightHand}/deviceRotation", expectedControlType: "Quaternion");
            m_TriggerAction = new InputAction("PlaceTrigger", InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            m_GripAction = new InputAction("BreakGrip", InputActionType.Button,
                "<XRController>{RightHand}/gripPressed");
            m_TriggerAction.performed += OnTriggerPerformed;
            m_GripAction.performed += OnGripPerformed;

            m_BreakHighlight = CreateHighlight("Break Highlight", 1.03f, m_BreakHighlightMaterial);
            m_PlaceHighlight = CreateHighlight("Place Highlight", 0.98f, m_PlaceHighlightMaterial);
        }

        void OnDestroy()
        {
            m_TriggerAction.performed -= OnTriggerPerformed;
            m_GripAction.performed -= OnGripPerformed;
        }

        void OnEnable()
        {
            m_PositionAction.Enable();
            m_RotationAction.Enable();
            m_TriggerAction.Enable();
            m_GripAction.Enable();
        }

        void OnDisable()
        {
            m_PositionAction.Disable();
            m_RotationAction.Disable();
            m_TriggerAction.Disable();
            m_GripAction.Disable();
        }

        void OnTriggerPerformed(InputAction.CallbackContext _) => m_PlaceRequested = true;
        void OnGripPerformed(InputAction.CallbackContext _) => m_BreakRequested = true;

        void Update()
        {
            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            var origin = m_TerrainField != null && m_TerrainField.origin != null
                ? m_TerrainField.origin.OriginTransform
                : null;

            if (world == null || origin == null || !m_TerrainField.FieldVisible)
            {
                SetTargetVisible(false);
                m_PlaceRequested = false;
                m_BreakRequested = false;
                return;
            }

            UpdateTarget(world, origin);
            UpdateHighlights(origin);
            HandleActions(world);
            UpdatePendingVisuals(world, origin);
        }

        /// <summary>コントローラレイをセル空間へ変換し DDA でヒットセルを求める。</summary>
        void UpdateTarget(World world, Transform origin)
        {
            m_HasTarget = false;

            if (m_RotationAction.activeControl == null)
            {
                return;
            }

            var localPos = m_PositionAction.ReadValue<Vector3>();
            var localRot = m_RotationAction.ReadValue<Quaternion>();
            var worldPos = m_TrackingSpace != null ? m_TrackingSpace.TransformPoint(localPos) : localPos;
            var worldDir = (m_TrackingSpace != null ? m_TrackingSpace.rotation * localRot : localRot) * Vector3.forward;

            // ワールド → 原点ローカル → セル空間
            var lp = origin.InverseTransformPoint(worldPos);
            var ld = origin.InverseTransformDirection(worldDir);
            float offsetX = world.Width * 0.5f * k_BlockSize;
            float offsetZ = world.Depth * 0.5f * k_BlockSize;
            float cxf = (lp.x + offsetX) / k_BlockSize + 0.5f;
            float cyf = lp.y / k_BlockSize;
            float czf = (lp.z + offsetZ) / k_BlockSize + 0.5f;

            if (!VoxelRaycast.Raycast(world.Grid, cxf, cyf, czf, ld.x, ld.y, ld.z,
                    k_MaxRayDistanceMeters / k_BlockSize, out var hitCell, out var hitNormal))
            {
                return;
            }

            m_HasTarget = true;
            m_TargetCell = hitCell;
            m_PlaceCell = hitCell + hitNormal;
            // 法線ゼロ（レイ始点が壁内）や高さ範囲外へは設置不可
            m_CanPlace = hitNormal != new Int3(0, 0, 0)
                && m_PlaceCell.y >= 0 && m_PlaceCell.y < world.Params.maxHeight
                && world.Grid.Get(m_PlaceCell) == BlockId.Air;
        }

        void UpdateHighlights(Transform origin)
        {
            if (!m_HasTarget)
            {
                SetTargetVisible(false);
                return;
            }

            m_BreakHighlight.SetActive(true);
            m_BreakHighlight.transform.position = origin.TransformPoint(CellToLocal(m_TargetCell));
            m_BreakHighlight.transform.rotation = origin.rotation;

            m_PlaceHighlight.SetActive(m_CanPlace);
            if (m_CanPlace)
            {
                m_PlaceHighlight.transform.position = origin.TransformPoint(CellToLocal(m_PlaceCell));
                m_PlaceHighlight.transform.rotation = origin.rotation;
            }
        }

        void HandleActions(World world)
        {
            bool place = m_PlaceRequested;
            bool brk = m_BreakRequested;
            m_PlaceRequested = false;
            m_BreakRequested = false;

            if (!m_HasTarget || Time.unscaledTime - m_LastActionTime < k_ActionCooldown)
            {
                return;
            }

            if (place && m_CanPlace)
            {
                m_LastActionTime = Time.unscaledTime;
                world.EnqueuePlayerAction(SimEventType.PlayerPlace, m_PlaceCell, BlockId.Stone);
                AddPendingVisual(m_PlaceCell, isPlace: true);
                Debug.Log($"[BlockInteractor] 設置予約: {m_PlaceCell}");
            }
            else if (brk)
            {
                m_LastActionTime = Time.unscaledTime;
                world.EnqueuePlayerAction(SimEventType.PlayerBreak, m_TargetCell, BlockId.Air);
                AddPendingVisual(m_TargetCell, isPlace: false);
                Debug.Log($"[BlockInteractor] 破壊予約: {m_TargetCell}");
            }
        }

        void AddPendingVisual(Int3 cell, bool isPlace)
        {
            var go = new GameObject(isPlace ? "Pending Place" : "Pending Break");
            go.transform.localScale = Vector3.one * (k_BlockSize * (isPlace ? 0.98f : 1.04f));
            go.AddComponent<MeshFilter>().sharedMesh = m_CubeMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = isPlace ? m_PendingPlaceMaterial : m_PendingBreakMaterial;
            m_Pending.Add(new PendingVisual { go = go, cell = cell, isPlace = isPlace, startTime = Time.unscaledTime });
        }

        /// <summary>仮表示: Tick適用（グリッド反映）を検知したら破棄。タイムアウトで無効操作分も掃除。</summary>
        void UpdatePendingVisuals(World world, Transform origin)
        {
            for (int i = m_Pending.Count - 1; i >= 0; i--)
            {
                var pending = m_Pending[i];
                bool resolved = pending.isPlace
                    ? world.Grid.Get(pending.cell) != BlockId.Air
                    : world.Grid.Get(pending.cell) == BlockId.Air;

                if (resolved || Time.unscaledTime - pending.startTime > k_PendingTimeout)
                {
                    Destroy(pending.go);
                    m_Pending.RemoveAt(i);
                    continue;
                }

                pending.go.transform.position = origin.TransformPoint(CellToLocal(pending.cell));
                pending.go.transform.rotation = origin.rotation;
            }
        }

        void SetTargetVisible(bool visible)
        {
            if (m_BreakHighlight != null && m_BreakHighlight.activeSelf != visible)
            {
                m_BreakHighlight.SetActive(visible);
            }
            if (m_PlaceHighlight != null && !visible && m_PlaceHighlight.activeSelf)
            {
                m_PlaceHighlight.SetActive(false);
            }
        }

        GameObject CreateHighlight(string name, float scale, Material material)
        {
            var go = new GameObject(name);
            go.transform.localScale = Vector3.one * (k_BlockSize * scale);
            go.AddComponent<MeshFilter>().sharedMesh = m_CubeMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            go.SetActive(false);
            return go;
        }

        Vector3 CellToLocal(Int3 cell)
        {
            var world = m_TerrainField.CurrentWorld;
            float offsetX = world.Width * 0.5f * k_BlockSize;
            float offsetZ = world.Depth * 0.5f * k_BlockSize;
            return new Vector3(
                cell.x * k_BlockSize - offsetX,
                (cell.y + 0.5f) * k_BlockSize,
                cell.z * k_BlockSize - offsetZ);
        }
    }
}
