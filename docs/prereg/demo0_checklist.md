# Demo 0 実機検証チェックリスト

対象: docs/prereg/demo0_metrics.md の M1〜M3。上から順に実施する。
前提: Quest 3 がUSB接続済み、Unity Editor は閉じている。

## 準備
- [ ] 1. デプロイ: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build_quest.ps1` → 終了コード0を確認
- [ ] 2. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\deploy.ps1`
- [ ] 3. 前回のアンカー保存を消して初期状態にする場合:
      `adb shell run-as com.piperender.blockfield rm files/diorama_anchor.json`
      （run-as が使えない場合はアプリのデータ消去: `adb shell pm clear com.piperender.blockfield`。
      この場合 USE_SCENE 権限も消えるので再許可が必要）
- [ ] 4. ヘッドセットを装着（外すとアプリは即バックグラウンド停止する。検証中は着けたまま）
- [ ] 5. USE_SCENE 権限ダイアログが出たら「許可」

## 原点確定（T2）
- [ ] 6. 机の平面が検出されるまで机を見回す（数秒〜十数秒）
- [ ] 7. 右コントローラで机上を指す → リング状レティクルが出る
- [ ] 8. 机の縁の近く（橋が縁を越えて空中に出る位置）で右トリガー → 赤い4cm箱（原点）が固定される
      ※向き: レイの方向が原点の前方(+Z)になり、橋はそちらへ伸びる。机の縁へ向けて確定すること

## M3: オクルージョン判定
- [ ] 9. 青い橋ブロック列が机の縁を越えて空中に伸びていることを確認
- [ ] 10. しゃがんで机の縁より低い視点から橋を見る（腕の長さ程度の距離）
- [ ] 11. 判定: 机より奥のブロックが机に隠れて見えないこと（エッジのちらつきは許容）
      → 結果: 合格 / 不合格（メモ:　　　　　　　　　）
- [ ] 12. 主観メモ（BlockVerseで感じた実在感に近いか）:（　　　　　　　　　）

## M2: 負荷切替（記録のみ・Go/No-Go対象外）
- [ ] 13. 1万個の表示を確認し、頭を動かして体感を確認
- [ ] 14. 右コントローラAボタンで 2万→4万→8万 と切替（logcat に個数が出る）
      切替時に一瞬固まるのはメッシュ再構築のため（計測対象外）
- [ ] 15. カクつきを感じ始めた水準をメモ:（　　　　　個）

## M1: アンカー安定性（再起動2回）
- [ ] 16. 原点の赤い箱の位置に現実のマーカー（テープ）を貼る
- [ ] 17. PC側からアプリを再起動:
      `adb shell am force-stop com.piperender.blockfield`
      `adb shell am start -n com.piperender.blockfield/com.unity3d.player.UnityPlayerGameActivity`
- [ ] 18. 1回目判定: 復元された赤い箱とテープのズレが目視で1ブロック（4cm）以内
      → 結果: 合格 / 不合格（メモ:　　　　　　　　　）
- [ ] 19. 手順17をもう一度実行して再起動
- [ ] 20. 2回目判定: 同上
      → 結果: 合格 / 不合格（メモ:　　　　　　　　　）

## 結果まとめ
- M1（2回とも合格で合格）: 合格 / 不合格
- M2 メモ: （　　　　　）
- M3: 合格 / 不合格
- Go/No-Go（M1合格 かつ M3合格でGo）: **Go / No-Go**
- No-Go の場合の原因1行メモ: （　　　　　　　　　　　　）
- 完了時: `git tag demo-0.1` でフリーズ（demo0_metrics.md 参照）
