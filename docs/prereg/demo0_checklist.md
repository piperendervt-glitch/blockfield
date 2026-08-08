# Demo 0 実機検証チェックリスト（v2）

対象: docs/prereg/demo0_metrics.md の M1〜M3。
前提: HMD連続稼働は最小限にする。**装着中のPC操作はなし**（PC操作はすべて非装着時）。
HMD内のデバッグパネル（カメラ前下方の黒パネル）で状態を確認できる:
`USE_SCENE / Planes / RayHit / Origin / AnchorSaved / Voxels / FPS / Last`

## 事前（PC、非装着）
- [ ] 1. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\reset_anchor.ps1`
      （アンカー保存を初期化。**権限も初期化されるので、起動時に USE_SCENE ダイアログが再表示される**）
- [ ] 2. `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\deploy.ps1`
      （HMDスリープ中は起動コマンドが保留される。装着すると起動する）

## セッションA（装着1回、目標5分以内）
- [ ] 3. 装着 → アプリ起動 → USE_SCENE 権限ダイアログを**許可**
- [ ] 4. パネルの `Planes` を確認。**0のままなら中断**: 外して Quest の設定 >
      環境設定 > スペース設定（部屋のスキャン）を実施してから手順3へ戻る
- [ ] 5. 右コントローラで机を指す → 緑のリングが机上に出る（パネル `RayHit: Y`）
- [ ] 6. **机の縁から30cm内側**、橋（+Z）が**縁の方を向く**向きでトリガー → 原点確定
      （パネル: `Origin: Placed` → `AnchorSaved: Y` を確認）
- [ ] 7. M3判定: 青い橋が縁を越えて空中に出ている →
      しゃがんで縁より低い視点・腕の長さの距離から、机より奥のブロックが隠れるか
      → 結果: 合格 / 不合格（メモ:　　　　　　　）
- [ ] 8. M2: Aボタンで切替（1秒クールダウンあり）。各水準で5秒静止しパネルのFPSを読む
      - 1万個: FPS（　　）
      - 2万個: FPS（　　）
      - 4万個: FPS（　　）
      - 8万個: FPS（　　）
- [ ] 9. 原点の赤い箱の位置にテープを貼る → HMDを外す

## セッションB（M1: 装着2回、各1分）
- [ ] 10. PC: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restart_app.ps1`
- [ ] 11. 装着: パネルが `Origin: Restored` になるまで待つ →
       赤い箱とテープのズレが目視で1ブロック（4cm）以内か
       → 1回目: 合格 / 不合格（メモ:　　　　　　　）→ 外す
- [ ] 12. PC: restart_app.ps1 をもう一度実行
- [ ] 13. 装着: 2回目判定（同上）
       → 2回目: 合格 / 不合格（メモ:　　　　　　　）→ 外す

## 結果まとめ
- M1（2回とも合格で合格）: 合格 / 不合格
- M2 FPS: 1万（　）/ 2万（　）/ 4万（　）/ 8万（　）、カクつきを感じた水準:（　　）
- M3: 合格 / 不合格
- 実在感メモ（BlockVerse比）:（　　　　　　　　　　　　）
- Go/No-Go（M1合格 かつ M3合格でGo）: **Go / No-Go**
- No-Go の場合の原因1行メモ:（　　　　　　　　　　　　）
- 完了時: `git tag demo-0.1` でフリーズ（demo0_metrics.md 参照）

## トラブル時
- アプリがすぐ閉じる/起動しない: HMDがスリープしている。装着してから restart_app.ps1
- パネルの `Last:` 行に直近イベント（origin placed / anchor saved / 失敗など）が出る
- ログ確認（PC）: `adb logcat -s Unity` で `[DioramaOrigin]` 等のタグを追う
