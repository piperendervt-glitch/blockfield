using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace BlockField
{
    /// <summary>
    /// 偵察コード (Demo 3 E6 / M7) — 恒久機能ではない。
    /// Demo 4.5 (Room Terrain) の技術リスク前倒し検証として、ARMeshManager の
    /// グローバルメッシュを起動から30秒間・5秒ごとにログ出力する（見た目には何も出さない）。
    /// 30秒経過後は ARMeshManager ごと停止し、性能への影響を残さない。
    /// </summary>
    public sealed class MeshRecon : MonoBehaviour
    {
        const float k_LogInterval = 5f;
        const float k_Duration = 30f;

        [SerializeField] ARMeshManager m_MeshManager;
        [SerializeField] DioramaOrigin m_Diorama;

        public ARMeshManager meshManager { get => m_MeshManager; set => m_MeshManager = value; }
        public DioramaOrigin diorama { get => m_Diorama; set => m_Diorama = value; }

        float m_Elapsed;
        float m_NextLogTime = k_LogInterval;

        void Update()
        {
            m_Elapsed += Time.deltaTime;

            if (m_Elapsed >= k_Duration)
            {
                Debug.Log("[MeshRecon] 30秒経過 — 偵察終了。ARMeshManager を停止する。");
                if (m_MeshManager != null)
                {
                    m_MeshManager.enabled = false;
                }
                enabled = false;
                return;
            }

            if (m_Elapsed < m_NextLogTime)
            {
                return;
            }
            m_NextLogTime += k_LogInterval;
            LogSnapshot();
        }

        void LogSnapshot()
        {
            var meshes = m_MeshManager != null ? m_MeshManager.meshes : null;
            int count = meshes?.Count ?? 0;
            long totalVertices = 0;
            var bounds = new Bounds();
            bool hasBounds = false;

            if (meshes != null)
            {
                foreach (var mf in meshes)
                {
                    if (mf == null || mf.sharedMesh == null)
                    {
                        continue;
                    }
                    var mesh = mf.sharedMesh;
                    totalVertices += mesh.vertexCount;

                    // ワールド空間バウンディングボックス（8隅を変換して包含）
                    var b = mesh.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        var corner = new Vector3(
                            (c & 1) == 0 ? b.min.x : b.max.x,
                            (c & 2) == 0 ? b.min.y : b.max.y,
                            (c & 4) == 0 ? b.min.z : b.max.z);
                        var world = mf.transform.TransformPoint(corner);
                        if (!hasBounds)
                        {
                            bounds = new Bounds(world, Vector3.zero);
                            hasBounds = true;
                        }
                        else
                        {
                            bounds.Encapsulate(world);
                        }
                    }
                }
            }

            // 座標系の基準: XROrigin(親)・ヘッド・ジオラマアンカーのワールド位置を併記
            var originPos = m_MeshManager != null ? m_MeshManager.transform.parent.position : Vector3.zero;
            var camPos = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            var anchorPos = m_Diorama != null && m_Diorama.OriginTransform != null
                ? m_Diorama.OriginTransform.position
                : Vector3.negativeInfinity;

            Debug.Log($"[MeshRecon] t={m_Elapsed:F0}s meshes={count} totalVerts={totalVertices} " +
                $"bounds(center={FormatV3(bounds.center)}, size={FormatV3(bounds.size)}, valid={hasBounds}) " +
                $"xrOriginPos={FormatV3(originPos)} headPos={FormatV3(camPos)} anchorPos={FormatV3(anchorPos)}");
        }

        static string FormatV3(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
    }
}
