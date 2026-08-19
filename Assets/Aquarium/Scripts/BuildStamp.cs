using UnityEngine;

namespace BlockField.Aquarium
{
    /// <summary>
    /// ビルド時に刻んだ「どのシーン・どのコミットの APK か」。
    ///
    /// 【なぜ要るか】2026-08-19 に `-Aquarium` を付け忘れて本編シーンを実機へ入れ、
    /// **水槽の実機セッションのつもりで生態系を起動した**。実機側には
    /// 「いま何が動いているか」を示すものが何も無く、ユーザーが見て初めて分かった。
    /// パッケージ名が共通で APK も同名だったため、PC 側からも区別できなかった。
    ///
    /// APK 名は分けた（blockfield_aquarium.apk / blockfield_main.apk）が、
    /// **実機に入ってしまえば名前は残らない**ので、画面に出す。
    /// </summary>
    public static class BuildStamp
    {
        const string k_ResourceName = "BuildStamp";
        static string s_Text;

        /// <summary>「Aquarium | feat/aquarium@646e621 | 08-19 17:59」の形。未刻印なら注記を返す。</summary>
        public static string Text
        {
            get
            {
                if (s_Text != null) return s_Text;
                var asset = Resources.Load<TextAsset>(k_ResourceName);
                s_Text = asset != null && !string.IsNullOrWhiteSpace(asset.text)
                    ? asset.text.Trim()
                    : "(刻印なし: Editor 実行かビルド前)";
                return s_Text;
            }
        }
    }
}
