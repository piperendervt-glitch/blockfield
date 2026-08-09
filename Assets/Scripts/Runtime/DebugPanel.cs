using System.Collections.Generic;
using System.IO;
using BlockField.SimCore.Ecology;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace BlockField
{
    /// <summary>
    /// HMD内デバッグパネル (Demo 0 テストUI)。
    /// カメラ前下方に固定した World Space Canvas に検証用の状態を毎秒表示する。
    /// 注: 内蔵フォント (LegacyRuntime.ttf) は日本語グリフを持たないため表示は英語。
    /// </summary>
    public sealed class DebugPanel : MonoBehaviour
    {
        const float k_RefreshInterval = 1f;

        /// <summary>
        /// FPS を logcat へ出す間隔（秒）。
        /// Demo 4.5 の M6 は画面表示だけを見る運用にしていたため、セッション後に
        /// ログから転記できず未取得になった。以降は capture_session が拾えるようにする。
        /// </summary>
        const float k_FpsLogInterval = 30f;

        [SerializeField] DioramaOrigin m_Diorama;
        [SerializeField] TerrainField m_TerrainField;
        [SerializeField] ARPlaneManager m_PlaneManager;
        [SerializeField] RoomTerrainBuilder m_RoomBuilder;
        [SerializeField] RoomTerrainView m_RoomView;
        [SerializeField] VrModeController m_VrMode;
        [SerializeField] RoomShellView m_Shell;
        [SerializeField] FieldOverlayView m_FieldOverlay;
        [SerializeField] Text m_Text;

        public DioramaOrigin diorama { get => m_Diorama; set => m_Diorama = value; }
        public TerrainField terrainField { get => m_TerrainField; set => m_TerrainField = value; }
        public ARPlaneManager planeManager { get => m_PlaneManager; set => m_PlaneManager = value; }
        public RoomTerrainBuilder roomBuilder { get => m_RoomBuilder; set => m_RoomBuilder = value; }
        public RoomTerrainView roomView { get => m_RoomView; set => m_RoomView = value; }
        public VrModeController vrMode { get => m_VrMode; set => m_VrMode = value; }
        public RoomShellView shell { get => m_Shell; set => m_Shell = value; }
        public FieldOverlayView fieldOverlay { get => m_FieldOverlay; set => m_FieldOverlay = value; }
        public Text text { get => m_Text; set => m_Text = value; }

        static string s_LastEvent = "-";

        float m_SmoothedDeltaTime;
        float m_NextRefresh;
        float m_NextFpsLog;

        /// <summary>
        /// 摂食成功率の窓（直近 k_FeedWindowTicks ティック）を取るためのスナップショット。
        /// 窓は表示側の責務にして World には累計だけ持たせている。
        /// </summary>
        const int k_FeedWindowTicks = 100;
        readonly Queue<(long tick, int attempts, int successes)> m_FeedHistory = new();
        long m_LastFeedSampleTick = -1;

        /// <summary>各コンポーネントが直近イベントを1行で通知する。</summary>
        public static void Notify(string message)
        {
            s_LastEvent = message;
        }

        void Update()
        {
            // FPS: unscaledDeltaTime の指数移動平均
            m_SmoothedDeltaTime = Mathf.Lerp(m_SmoothedDeltaTime, Time.unscaledDeltaTime, 0.05f);

            if (Time.unscaledTime < m_NextRefresh || m_Text == null)
            {
                return;
            }
            m_NextRefresh = Time.unscaledTime + k_RefreshInterval;

            m_Text.text = BuildText();

            if (Time.unscaledTime >= m_NextFpsLog)
            {
                m_NextFpsLog = Time.unscaledTime + k_FpsLogInterval;
                float fps = m_SmoothedDeltaTime > 0.0001f ? 1f / m_SmoothedDeltaTime : 0f;
                string mode = m_RoomView != null && m_RoomView.IsComposed
                    ? (m_RoomView.Mode == RoomTerrainView.ViewMode.Normal ? "NORMAL" : "DIAG")
                    : "-";
                Debug.Log($"[DebugPanel] FPS={fps:F1} mode={mode} " +
                    $"vr={(m_VrMode != null ? (m_VrMode.IsVrMode ? "VR" : "MR") : "-")} " +
                    $"blocks={(m_TerrainField != null ? m_TerrainField.BlockCount : 0)}");

                // 【規約】パネルに出す指標は必ずログにも出す（CLAUDE.md 実機テスト運用）。
                // M6 の FPS と Demo 5a の密度指標で、同じ「パネルにしか無くて
                // セッション後に転記できない」漏れを2回起こしている
                var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
                if (world != null)
                {
                    Debug.Log($"[DebugPanel] 生態: tick={world.TickCount} " +
                        $"植物={world.PlantCount} 草食={world.SheepCount + world.PigCount} 狼={world.WolfCount} " +
                        $"適性セル={world.SuitableCellCount}");
                    Debug.Log($"[DebugPanel] 密度: 植物={EcologyStats.PlantDensity(world) * 100:F2}% " +
                        $"動物={EcologyStats.AnimalDensity(world) * 100:F2}% " +
                        $"摂食{k_FeedWindowTicks}={UpdateAndGetFeedRate(world) * 100:F1}% " +
                        $"餓死/個体/1000t={EcologyStats.StarvationPerAnimalPerKiloTick(world):F2} " +
                        $"| 参照(箱庭3000t) 植物={EcologyStats.DioramaReference.PlantDensity * 100:F2}% " +
                        $"動物={EcologyStats.DioramaReference.AnimalDensity * 100:F2}% " +
                        $"摂食={EcologyStats.DioramaReference.FeedSuccessRate * 100:F1}% " +
                        $"餓死={EcologyStats.DioramaReference.StarvationPerAnimalPerKiloTick:F2}");
                    Debug.Log($"[DebugPanel] 累計: 餓死={world.StarvationCount} 捕食={world.PredationCount} " +
                        $"出生={world.BirthCount} 摂食成功={world.FeedSuccessCount}/{world.FeedAttemptCount}");

                    // Demo 8 H4: 場の状態（規約どおりパネルとログの両方に出す）
                    var (fMean, fMax) = EcologyStats.FieldStats(world.Fear);
                    var (pMean, pMax) = EcologyStats.FieldStats(world.Prey);
                    var (vMean, vMax) = EcologyStats.FieldStats(world.Vegetation);
                    Debug.Log($"[DebugPanel] 場: 植生 平均={vMean:F4} 最大={vMax:F3} / " +
                        $"恐怖 平均={fMean:F4} 最大={fMax:F3} / 獲物 平均={pMean:F4} 最大={pMax:F3}");
                    Debug.Log($"[DebugPanel] 追跡: 狼の歩数={world.WolfStepCount} " +
                        $"捕食/1000歩={EcologyStats.PredationPerKiloWolfStep(world):F1} " +
                        $"草食獣の恐怖曝露={EcologyStats.HerbivoreFearExposure(world):F2}（1.0未満で薄い所にいる）");
                }

                // 座標系ズレ（メタボタン長押しの再センタリング）の切り分け用。
                // 再センタリングが起きるとアンカーのワールドポーズが動く。地形ルートは
                // アンカーの子なので local は不変のまま world だけが追従するのが正しい姿。
                // world が動かない／local が変わるなら親子付けが壊れている
                var anchor = m_Diorama != null ? m_Diorama.OriginTransform : null;
                var root = m_TerrainField != null ? m_TerrainField.TerrainRoot : null;
                if (anchor != null && root != null)
                {
                    Debug.Log($"[DebugPanel] 座標系: アンカー world={anchor.position:F3} " +
                        $"rotY={anchor.rotation.eulerAngles.y:F1} / " +
                        $"地形ルート local={root.localPosition:F3} world={root.position:F3}");
                }
            }
        }

        string BuildText()
        {
            string perm;
#if UNITY_ANDROID && !UNITY_EDITOR
            perm = Permission.HasUserAuthorizedPermission(ScenePermissionGate.ScenePermission) ? "OK" : "NG";
#else
            perm = "n/a";
#endif

            int planes = 0;
            if (m_PlaneManager != null)
            {
                foreach (var _ in m_PlaneManager.trackables) planes++;
            }

            string rayHit = m_Diorama != null && m_Diorama.HasPlaneHit ? "Y" : "N";
            string origin = m_Diorama != null ? m_Diorama.State.ToString() : "-";
            bool anchorSaved = File.Exists(Path.Combine(Application.persistentDataPath, "diorama_anchor.json"));
            int blocks = m_TerrainField != null ? m_TerrainField.BlockCount : 0;
            uint seed = m_TerrainField != null ? m_TerrainField.CurrentSeed : 0;
            long genMs = m_TerrainField != null ? m_TerrainField.GenerationMs : 0;
            float fps = m_SmoothedDeltaTime > 0.0001f ? 1f / m_SmoothedDeltaTime : 0f;

            string field = m_TerrainField != null && m_TerrainField.FieldVisible ? "ON" : "OFF";

            var world = m_TerrainField != null ? m_TerrainField.CurrentWorld : null;
            long tick = world?.TickCount ?? 0;
            int plants = world?.PlantCount ?? 0;
            int animals = world?.AnimalCount ?? 0;
            int wolves = world?.WolfCount ?? 0;
            int starved = world?.StarvationCount ?? 0;
            int predated = world?.PredationCount ?? 0;
            int births = world?.BirthCount ?? 0;

            return
                $"USE_SCENE: {perm}   Planes: {planes}   RayHit: {rayHit}\n" +
                $"Origin: {origin}   AnchorSaved: {(anchorSaved ? "Y" : "N")}\n" +
                $"Blocks: {blocks}   Field: {field}   FPS: {fps:F1}\n" +
                $"Seed: {seed}   Gen: {genMs}ms\n" +
                BuildRoomText() +
                $"Tick: {tick}  Plants: {plants}  Animals: {animals}  Wolves: {wolves}\n" +
                $"Starve: {starved}  Pred: {predated}  Birth: {births}\n" +
                BuildHealthText(world) +
                $"Last: {s_LastEvent}";
        }

        /// <summary>
        /// 生態系の健全性 (Demo 5a)。目測に頼らず数値で判定するための指標。
        /// 括弧内は箱庭 (Demo 3 相当) を3,000ティック走らせた実測の参照値。
        /// 5分のセッション（約300ティック）はまだ立ち上がり途中なので、
        /// 参照値より低く出るのが正常（箱庭の t300 実測は 植物1.71% / 摂食0.025）。
        /// </summary>
        string BuildHealthText(World world)
        {
            if (world == null)
            {
                return "Dens: -   Feed: -   Starve/1k: -\n";
            }

            float plantDensity = EcologyStats.PlantDensity(world);
            float animalDensity = EcologyStats.AnimalDensity(world);
            float starvePerK = EcologyStats.StarvationPerAnimalPerKiloTick(world);
            float feedRate = UpdateAndGetFeedRate(world);

            // Demo 8 H4: 場の状態と、狼の追跡効率・草食獣の危険曝露
            var (fearMean, fearMax) = EcologyStats.FieldStats(world.Fear);
            var (preyMean, preyMax) = EcologyStats.FieldStats(world.Prey);
            var (vegMean, vegMax) = EcologyStats.FieldStats(world.Vegetation);

            return
                $"Dens P/A: {plantDensity * 100:F2}%/{animalDensity * 100:F2}% " +
                $"(ref {EcologyStats.DioramaReference.PlantDensity * 100:F2}/" +
                $"{EcologyStats.DioramaReference.AnimalDensity * 100:F2})\n" +
                $"Feed{k_FeedWindowTicks}: {feedRate * 100:F1}% " +
                $"(ref {EcologyStats.DioramaReference.FeedSuccessRate * 100:F1})   " +
                $"Starve/1k: {starvePerK:F2} " +
                $"(ref {EcologyStats.DioramaReference.StarvationPerAnimalPerKiloTick:F2})\n" +
                $"Fld avg/max V:{vegMean:F3}/{vegMax:F2} " +
                $"F:{fearMean:F3}/{fearMax:F2} P:{preyMean:F3}/{preyMax:F2}\n" +
                $"Pred/1k step: {EcologyStats.PredationPerKiloWolfStep(world):F1}   " +
                $"FearExpo: {EcologyStats.HerbivoreFearExposure(world):F2}   " +
                $"Ovl[Y]: {(m_FieldOverlay != null ? m_FieldOverlay.Current.ToString() : "-")}\n";
        }

        /// <summary>
        /// 直近 <see cref="k_FeedWindowTicks"/> ティックの摂食成功率。
        /// World は累計しか持たないので、ここで過去のスナップショットとの差分を取る。
        /// </summary>
        float UpdateAndGetFeedRate(World world)
        {
            long tick = world.TickCount;

            // Queue.Peek() は最古なので、直近に積んだティックは別に覚えて重複を防ぐ
            if (m_FeedHistory.Count == 0 || tick != m_LastFeedSampleTick)
            {
                m_FeedHistory.Enqueue((tick, world.FeedAttemptCount, world.FeedSuccessCount));
                m_LastFeedSampleTick = tick;
            }

            // 窓より古いスナップショットは、1つだけ残して捨てる（それが差分の基準になる）
            while (m_FeedHistory.Count > 1)
            {
                var oldest = m_FeedHistory.Peek();
                if (tick - oldest.tick <= k_FeedWindowTicks)
                {
                    break;
                }
                m_FeedHistory.Dequeue();
            }

            var baseline = m_FeedHistory.Peek();
            return EcologyStats.FeedSuccessRateDelta(
                world.FeedSuccessCount - baseline.successes,
                world.FeedAttemptCount - baseline.attempts);
        }

        /// <summary>部屋地形 (Demo 4.5 G3) の常時表示項目。実機で状況を判断できるようにする。</summary>
        string BuildRoomText()
        {
            string mode = m_RoomView != null && m_RoomView.IsComposed
                ? (m_RoomView.Mode == RoomTerrainView.ViewMode.Normal ? "0 NORMAL" : "1 DIAG")
                : "-";

            int surfaces = m_RoomBuilder != null ? m_RoomBuilder.TotalHits : 0;
            int cells = m_RoomBuilder != null ? m_RoomBuilder.CellsWithHits : 0;
            float avg = cells > 0 ? (float)surfaces / cells : 0f;

            var stats = m_RoomBuilder != null ? m_RoomBuilder.Stats : null;
            string dist = stats != null
                ? $"{stats.CellsWith1}/{stats.CellsWith2}/{stats.CellsWith3Plus}"
                : "-/-/-";

            var composed = m_TerrainField != null ? m_TerrainField.RoomComposed : null;
            int snowBlocks = composed?.BlockCount ?? 0;
            long composeMs = m_TerrainField != null ? m_TerrainField.GenerationMs : 0;

            var biome = composed?.BiomeHistogram;
            string biomeText = biome != null && biome.Length >= 3
                ? $"{biome[0]}/{biome[1]}/{biome[2]}"
                : "-/-/-";
            int walls = composed != null ? composed.WallCellCount : 0;

            // Demo 4.5b: MR/VR モードと外殻ブロック数
            string vr = m_VrMode != null ? (m_VrMode.IsVrMode ? "VR" : "MR") : "-";
            int shellBlocks = m_Shell != null ? m_Shell.BlockCount : 0;

            return
                $"Room[B]: {mode}   Surf: {surfaces} (avg {avg:F2})\n" +
                $"Dist1/2/3+: {dist}   Snow: {snowBlocks} ({composeMs}ms)\n" +
                $"Biome P/H/M: {biomeText}   Wall: {walls}\n" +
                $"Mode[X]: {vr}   Shell: {shellBlocks}\n";
        }
    }
}
