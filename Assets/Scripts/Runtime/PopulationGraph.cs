using BlockField.SimCore.Ecology;
using UnityEngine;
using UnityEngine.UI;

namespace BlockField
{
    /// <summary>
    /// 個体数の時系列グラフ (Demo 5a の診断表示)。
    /// 植物・草食獣・狼の3本を直近 <see cref="k_Window"/> ティックぶん折れ線で描く。
    ///
    /// 【実装方式: テクスチャ書き込み】
    /// LineRenderer 3本（各300点＝900頂点を毎秒更新）より、
    /// 300x100 の Texture2D へ直接書く方が軽く、World Space Canvas の
    /// RawImage に貼るだけで済む。1回の更新は 30,000 画素のクリアと
    /// 3本ぶんの縦線描画だけで、毎秒1回しか走らない。
    ///
    /// 【表示と真実の分離】PopulationLog を**読むだけ**で World には触らない。
    /// </summary>
    public sealed class PopulationGraph : MonoBehaviour
    {
        /// <summary>横軸の幅（ティック数）。1Hz なので300ティック＝5分相当。</summary>
        const int k_Window = 300;

        /// <summary>テクスチャ解像度。横は1ティック=1画素。</summary>
        const int k_Width = k_Window;
        const int k_Height = 100;

        /// <summary>更新間隔（秒）。シムが1Hzなので毎秒で十分。</summary>
        const float k_RefreshInterval = 1f;

        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] RawImage m_Image;

        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public RawImage image { get => m_Image; set => m_Image = value; }

        static readonly Color32 k_Background = new Color32(12, 14, 20, 255);
        static readonly Color32 k_Grid = new Color32(45, 50, 62, 255);
        static readonly Color32 k_Plants = new Color32(90, 220, 90, 255);
        static readonly Color32 k_Herbivores = new Color32(240, 240, 240, 255);
        static readonly Color32 k_Wolves = new Color32(230, 120, 120, 255);

        Texture2D m_Texture;
        Color32[] m_Pixels;
        float m_NextRefresh;
        long m_LastDrawnTick = -1;

        void Awake()
        {
            m_Texture = new Texture2D(k_Width, k_Height, TextureFormat.RGBA32, false)
            {
                name = "PopulationGraph",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            m_Pixels = new Color32[k_Width * k_Height];

            if (m_Image != null)
            {
                m_Image.texture = m_Texture;
            }
        }

        void OnDestroy()
        {
            if (m_Texture != null)
            {
                Destroy(m_Texture);
            }
        }

        void Update()
        {
            if (Time.unscaledTime < m_NextRefresh)
            {
                return;
            }
            m_NextRefresh = Time.unscaledTime + k_RefreshInterval;

            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            if (world == null || m_Image == null)
            {
                return;
            }

            var log = world.PopulationLog;
            if (log.Count == 0 || world.TickCount == m_LastDrawnTick)
            {
                return;
            }
            m_LastDrawnTick = world.TickCount;

            Draw(log);
        }

        void Draw(PopulationLog log)
        {
            Clear();

            int count = log.Count;
            int first = count > k_Window ? count - k_Window : 0;
            int n = count - first;

            // 縦軸は最大値に自動スケール。3系列の最大を共通の基準にして
            // 相対的な多さがそのまま見えるようにする
            int max = 1;
            for (int i = first; i < count; i++)
            {
                var s = log.GetSample(i);
                if (s.plants > max) max = s.plants;
                if (s.herbivores > max) max = s.herbivores;
                if (s.wolves > max) max = s.wolves;
            }

            // 目盛り: 上端（最大値）と中央
            DrawHorizontalLine(k_Height - 1, k_Grid);
            DrawHorizontalLine(k_Height / 2, k_Grid);
            DrawHorizontalLine(0, k_Grid);

            int prevP = -1, prevH = -1, prevW = -1;
            for (int i = 0; i < n; i++)
            {
                var s = log.GetSample(first + i);

                // 直近が右端に来るよう、点数が窓に満たないうちは左詰めで描く
                int x = i;
                int yP = ToY(s.plants, max);
                int yH = ToY(s.herbivores, max);
                int yW = ToY(s.wolves, max);

                PlotSegment(x, prevP, yP, k_Plants);
                PlotSegment(x, prevH, yH, k_Herbivores);
                PlotSegment(x, prevW, yW, k_Wolves);

                prevP = yP; prevH = yH; prevW = yW;
            }

            m_Texture.SetPixels32(m_Pixels);
            m_Texture.Apply(false);
        }

        void Clear()
        {
            for (int i = 0; i < m_Pixels.Length; i++)
            {
                m_Pixels[i] = k_Background;
            }
        }

        static int ToY(int value, int max)
        {
            int y = (int)((long)value * (k_Height - 1) / max);
            if (y < 0) y = 0;
            if (y >= k_Height) y = k_Height - 1;
            return y;
        }

        /// <summary>前の点と繋いで縦に埋める（1画素刻みなので縦線で十分）。</summary>
        void PlotSegment(int x, int prevY, int y, Color32 color)
        {
            if (x < 0 || x >= k_Width)
            {
                return;
            }
            int from = prevY < 0 ? y : prevY;
            int lo = from < y ? from : y;
            int hi = from < y ? y : from;
            for (int py = lo; py <= hi; py++)
            {
                m_Pixels[py * k_Width + x] = color;
            }
        }

        void DrawHorizontalLine(int y, Color32 color)
        {
            if (y < 0 || y >= k_Height)
            {
                return;
            }
            int row = y * k_Width;
            for (int x = 0; x < k_Width; x += 4) // 破線にして系列と紛れないようにする
            {
                m_Pixels[row + x] = color;
            }
        }
    }
}
