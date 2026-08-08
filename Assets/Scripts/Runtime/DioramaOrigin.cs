using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BlockField
{
    /// <summary>
    /// ジオラマ原点のアンカー固定と復元 (Demo 0 T2)。
    /// 起動時に保存済みアンカーの復元を試み、無ければ右コントローラのレイ＋トリガーで
    /// 検出平面上に原点を確定し、永続アンカー (TrySaveAnchorAsync) として保存する。
    /// </summary>
    public sealed class DioramaOrigin : MonoBehaviour
    {
        const string k_SaveFileName = "diorama_anchor.json";
        const float k_MaxRayDistance = 10f;
        const float k_TriggerThreshold = 0.6f;

        [SerializeField] ARPlaneManager m_PlaneManager;
        [SerializeField] ARAnchorManager m_AnchorManager;
        [SerializeField] Transform m_TrackingSpace;
        [SerializeField] Material m_OriginMaterial;
        [SerializeField] Material m_ReticleMaterial;

        public ARPlaneManager planeManager { get => m_PlaneManager; set => m_PlaneManager = value; }
        public ARAnchorManager anchorManager { get => m_AnchorManager; set => m_AnchorManager = value; }
        /// <summary>コントローラのローカルポーズをワールドへ変換する基準 (Camera Offset)。</summary>
        public Transform trackingSpace { get => m_TrackingSpace; set => m_TrackingSpace = value; }
        public Material originMaterial { get => m_OriginMaterial; set => m_OriginMaterial = value; }
        public Material reticleMaterial { get => m_ReticleMaterial; set => m_ReticleMaterial = value; }

        /// <summary>確定・復元済みの原点。未確定なら null。</summary>
        public Transform OriginTransform { get; private set; }

        InputAction m_PositionAction;
        InputAction m_RotationAction;
        InputAction m_TriggerAction;
        GameObject m_Reticle;
        bool m_PlacementActive;
        bool m_Busy;
        float m_PrevTrigger;

        [Serializable]
        class SaveData
        {
            public string anchorGuid;
        }

        static string SavePath => Path.Combine(Application.persistentDataPath, k_SaveFileName);

        void Awake()
        {
            m_PositionAction = new InputAction("RightHandPosition", InputActionType.Value,
                "<XRController>{RightHand}/devicePosition", expectedControlType: "Vector3");
            m_RotationAction = new InputAction("RightHandRotation", InputActionType.Value,
                "<XRController>{RightHand}/deviceRotation", expectedControlType: "Quaternion");
            m_TriggerAction = new InputAction("RightHandTrigger", InputActionType.Value,
                "<XRController>{RightHand}/trigger", expectedControlType: "Axis");
        }

        void OnEnable()
        {
            m_PositionAction.Enable();
            m_RotationAction.Enable();
            m_TriggerAction.Enable();
        }

        void OnDisable()
        {
            m_PositionAction.Disable();
            m_RotationAction.Disable();
            m_TriggerAction.Disable();
        }

        async void Start()
        {
            try
            {
                bool restored = await TryRestoreAsync();
                if (restored)
                {
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DioramaOrigin] アンカー復元中に例外: {e.Message} — 新規配置モードへ移行。");
            }

            m_PlacementActive = true;
            CreateReticle();
            Debug.Log("[DioramaOrigin] 配置モード開始。右コントローラで平面を指し、トリガーで原点を確定してください。");
        }

        async Awaitable<bool> TryRestoreAsync()
        {
            if (!File.Exists(SavePath))
            {
                Debug.Log("[DioramaOrigin] 保存済みアンカーなし（初回起動）。");
                return false;
            }

            var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
            if (data == null || string.IsNullOrEmpty(data.anchorGuid) || !Guid.TryParse(data.anchorGuid, out var guid))
            {
                Debug.LogWarning("[DioramaOrigin] 保存ファイルが不正。新規配置モードへ移行。");
                return false;
            }

            Debug.Log($"[DioramaOrigin] 保存済みアンカー {data.anchorGuid} の復元を試行中...");
            var result = await m_AnchorManager.TryLoadAnchorAsync(new SerializableGuid(guid));
            if (!result.status.IsSuccess() || result.value == null)
            {
                Debug.LogWarning($"[DioramaOrigin] アンカー復元に失敗 (status: {result.status.statusCode})。新規配置モードへ移行。");
                return false;
            }

            AttachOrigin(result.value);
            Debug.Log("[DioramaOrigin] アンカー復元に成功。原点を復元した。");
            return true;
        }

        void Update()
        {
            if (!m_PlacementActive || m_Busy)
            {
                return;
            }

            if (!TryGetControllerRay(out var ray) || !TryRaycastPlanes(ray, out var hitPose))
            {
                if (m_Reticle != null) m_Reticle.SetActive(false);
                m_PrevTrigger = m_TriggerAction.ReadValue<float>();
                return;
            }

            if (m_Reticle != null)
            {
                m_Reticle.SetActive(true);
                m_Reticle.transform.SetPositionAndRotation(hitPose.position, hitPose.rotation);
            }

            float trigger = m_TriggerAction.ReadValue<float>();
            bool pressedThisFrame = trigger > k_TriggerThreshold && m_PrevTrigger <= k_TriggerThreshold;
            m_PrevTrigger = trigger;

            if (pressedThisFrame)
            {
                PlaceOriginAsync(hitPose);
            }
        }

        bool TryGetControllerRay(out Ray ray)
        {
            ray = default;
            if (m_RotationAction.activeControl == null)
            {
                return false; // 右コントローラ未接続/未トラッキング
            }

            var localPos = m_PositionAction.ReadValue<Vector3>();
            var localRot = m_RotationAction.ReadValue<Quaternion>();
            var origin = m_TrackingSpace != null ? m_TrackingSpace.TransformPoint(localPos) : localPos;
            var rotation = m_TrackingSpace != null ? m_TrackingSpace.rotation * localRot : localRot;
            ray = new Ray(origin, rotation * Vector3.forward);
            return true;
        }

        bool TryRaycastPlanes(Ray ray, out Pose hitPose)
        {
            hitPose = default;
            float bestDistance = float.MaxValue;
            ARPlane bestPlane = null;
            Vector3 bestPoint = default;

            foreach (var plane in m_PlaneManager.trackables)
            {
                if (plane.trackingState != TrackingState.Tracking)
                {
                    continue;
                }

                var mathPlane = new Plane(plane.normal, plane.center);
                if (!mathPlane.Raycast(ray, out float distance) || distance > k_MaxRayDistance || distance >= bestDistance)
                {
                    continue;
                }

                // 平面境界の近似チェック（extents の矩形内か）
                var point = ray.GetPoint(distance);
                var local = plane.transform.InverseTransformPoint(point);
                if (Mathf.Abs(local.x) > plane.extents.x || Mathf.Abs(local.z) > plane.extents.y)
                {
                    continue;
                }

                bestDistance = distance;
                bestPlane = plane;
                bestPoint = point;
            }

            if (bestPlane == null)
            {
                return false;
            }

            // 前方（レイ方向）を平面に投影した向きを原点のforwardにする
            var forward = Vector3.ProjectOnPlane(ray.direction, bestPlane.normal);
            if (forward.sqrMagnitude < 1e-6f)
            {
                forward = Vector3.ProjectOnPlane(Vector3.forward, bestPlane.normal);
            }

            hitPose = new Pose(bestPoint, Quaternion.LookRotation(forward.normalized, bestPlane.normal));
            return true;
        }

        async void PlaceOriginAsync(Pose pose)
        {
            m_Busy = true;
            Debug.Log($"[DioramaOrigin] トリガー入力を検出。原点を確定中... (位置: {pose.position})");
            try
            {
                var addResult = await m_AnchorManager.TryAddAnchorAsync(pose);
                if (!addResult.status.IsSuccess() || addResult.value == null)
                {
                    Debug.LogWarning($"[DioramaOrigin] アンカー生成に失敗 (status: {addResult.status.statusCode})。再度トリガーで試行可能。");
                    return;
                }

                var anchor = addResult.value;
                AttachOrigin(anchor);
                m_PlacementActive = false;
                if (m_Reticle != null)
                {
                    Destroy(m_Reticle);
                    m_Reticle = null;
                }
                Debug.Log("[DioramaOrigin] 原点を確定した。");

                var saveResult = await m_AnchorManager.TrySaveAnchorAsync(anchor);
                if (saveResult.status.IsSuccess())
                {
                    var data = new SaveData { anchorGuid = saveResult.value.guid.ToString() };
                    File.WriteAllText(SavePath, JsonUtility.ToJson(data));
                    Debug.Log($"[DioramaOrigin] アンカーを永続化した (guid: {data.anchorGuid})。");
                }
                else
                {
                    Debug.LogWarning($"[DioramaOrigin] アンカー保存に失敗 (status: {saveResult.status.statusCode})。このセッション中は動作するが再起動後は復元されない。");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DioramaOrigin] 原点確定中に例外: {e.Message}");
            }
            finally
            {
                m_Busy = false;
            }
        }

        void AttachOrigin(ARAnchor anchor)
        {
            // 原点マーカー: 4cm の赤い箱 (M1 のテープ照合用)
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "Diorama Origin";
            Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(anchor.transform, false);
            marker.transform.localScale = Vector3.one * 0.04f;
            if (m_OriginMaterial != null)
            {
                marker.GetComponent<MeshRenderer>().sharedMaterial = m_OriginMaterial;
            }

            OriginTransform = marker.transform;
        }

        void CreateReticle()
        {
            m_Reticle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            m_Reticle.name = "Placement Reticle";
            Destroy(m_Reticle.GetComponent<Collider>());
            m_Reticle.transform.localScale = new Vector3(0.08f, 0.002f, 0.08f);
            if (m_ReticleMaterial != null)
            {
                m_Reticle.GetComponent<MeshRenderer>().sharedMaterial = m_ReticleMaterial;
            }
            m_Reticle.SetActive(false);
        }
    }
}
