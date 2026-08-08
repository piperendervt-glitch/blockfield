using UnityEngine;
using UnityEngine.XR.ARFoundation;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace BlockField
{
    /// <summary>
    /// com.oculus.permission.USE_SCENE のランタイム権限フロー (Demo 0 T1)。
    /// 許可されたら AROcclusionManager を有効化する。
    /// Meta OpenXR 1.11+ はパッケージ側で権限要求しないため、アプリが自前で要求する必要がある
    /// （com.unity.xr.meta-openxr 同梱サンプル PermissionsCheck.cs と同方式）。
    /// </summary>
    public sealed class ScenePermissionGate : MonoBehaviour
    {
        public const string ScenePermission = "com.oculus.permission.USE_SCENE";

        [SerializeField]
        AROcclusionManager m_OcclusionManager;

        /// <summary>権限許可後に有効化する AROcclusionManager（シーン生成時に設定される）。</summary>
        public AROcclusionManager occlusionManager
        {
            get => m_OcclusionManager;
            set => m_OcclusionManager = value;
        }

        void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                Debug.Log("[ScenePermissionGate] USE_SCENE は既に許可済み。");
                EnableOcclusion();
                return;
            }

            Debug.Log("[ScenePermissionGate] USE_SCENE 未許可。権限ダイアログを表示して応答を待機中...");
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += OnPermissionGranted;
            callbacks.PermissionDenied += OnPermissionDenied;
            Permission.RequestUserPermission(ScenePermission, callbacks);
#else
            Debug.Log("[ScenePermissionGate] Android 実機以外のため USE_SCENE 権限フローをスキップ（オクルージョンは無効のまま）。");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void OnPermissionGranted(string permission)
        {
            Debug.Log($"[ScenePermissionGate] 権限が許可された: {permission}");
            EnableOcclusion();
        }

        void OnPermissionDenied(string permission)
        {
            // デモのためリトライUIは無し。オクルージョン無効のまま続行する。
            Debug.LogWarning($"[ScenePermissionGate] 権限が拒否された: {permission} — オクルージョン無効のまま続行。");
        }
#endif

        void EnableOcclusion()
        {
            if (m_OcclusionManager == null)
            {
                Debug.LogWarning("[ScenePermissionGate] AROcclusionManager が未設定のため有効化できない。");
                return;
            }

            m_OcclusionManager.enabled = true;
            Debug.Log("[ScenePermissionGate] AROcclusionManager を有効化した。");
        }
    }
}
