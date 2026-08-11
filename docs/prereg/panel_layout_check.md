# DebugPanel レイアウトの確認手順（エディタ Game ビュー）

対象: `docs/prereg/demo85_checklist.md` 既知の不具合 (b)
「FPS を含む上部3行が個体数グラフに隠れる」の修正確認。

所要 2〜3分。実機ビルドは不要。

## 前提

- シーンは再生成済みであること。していなければ:
  ```powershell
  Remove-Item Assets\Scenes\Main.unity -Force
  & "C:\Program Files\Unity\Hub\Editor\6000.3.11f1\Editor\Unity.exe" `
      -batchmode -quit -projectPath (Get-Location).Path `
      -executeMethod SceneBootstrap.CreateMainScene -logFile Logs\scene_regen.log
  ```
  **`SceneBootstrap.CreateMainScene` は既存シーンがあると黙ってスキップする**ため、
  先に削除すること（これを忘れて実機に `GrassView` が入っていない事故があった）。

## 手順1: Hierarchy で親子関係を確認（10秒）

`Main.unity` を開き、Hierarchy を展開する。

**期待:**
```
Main Camera
└─ Debug Panel
   ├─ Background
   ├─ Text
   └─ Population Graph      ← Debug Panel の子であること
      └─ Graph Image
```

**NG:** `Population Graph` が `Main Camera` の直下（`Debug Panel` と兄弟）に
ある場合は、旧レイアウトのまま＝シーンが再生成されていない。

## 手順2: Inspector で上端アンカーを確認（20秒）

`Population Graph` を選択し、Rect Transform を見る。

| 項目 | 期待値 |
|---|---|
| Anchors Min | X 0.5 / Y 1 |
| Anchors Max | X 0.5 / Y 1 |
| Pivot | X 0.5 / Y 0 |
| Pos Y | 10 |
| Width / Height | 430 / 143 |

Pivot Y が 0 であることが要点。**グラフは自分の下端をパネルの上端に
合わせて上へ伸びる**ので、パネルが何行増えても潜り込まない。

## 手順3: Game ビューで重なりを見る（30秒）

Play を押す。Game ビューの下部にパネル、その真上にグラフが出る。

**合格の条件（すべて満たすこと）:**

1. **1行目が `FPS: xx.x   Blocks: nnnnn   Field: ON/OFF` で、
   完全に読める**（数字が欠けたり、グラフの黒い矩形に覆われていない）
2. グラフの下辺とパネルの上辺の間に**隙間が見える**（10px ぶん）
3. パネルの最終行 `Last: ...` まで Game ビューに収まっている
4. グラフの3本の線（緑=草 / 白=草食獣 / 赤=狼）が描かれている

**手順3の1が本題。** 修正前は1〜3行目（`USE_SCENE` / `Origin` /
`Blocks・Field・**FPS**`）がグラフの下に隠れていた。

## 手順4: 行を増やしても壊れないことを確認（60秒、任意）

構造的な修正になっているかを確かめる。再発防止の本質はここ。

1. `Assets/Scripts/Runtime/DebugPanel.cs` の `BuildText()` の戻り値に
   ダミー行を3行足す（例: `"AAA\nBBB\nCCC\n" +` を先頭に）
2. Play する

**期待:** パネルが上に伸び、**グラフも一緒に押し上げられる**。
テキストは1行も隠れない（画面外に出ることはあり得る）。

**NG:** グラフが元の位置に留まり、増えた行がグラフの下に潜る
→ 親子付けかアンカー設定が効いていない。

3. 確認したらダミー行を消す（**コミットしないこと**）

## 手順5: 実機での追認（次の実機セッション時）

エディタの Game ビューは**カメラのアスペクト比が実機と違う**ため、
FOV に収まるかは実機でしか判定できない。

パネル下端は −41.0°、グラフ上端は +1.8°（0.6m 前方、垂直FOV ±48°）
の計算だが、次のセッションで以下を確認する:

- 正面を見たまま、視線を下げずに**グラフの上端が視野に入るか**
- 軽く見下ろして**パネル最終行まで読めるか**
- FPS が1行目で読めるか

読めない場合は `SceneBootstrap.cs` の `canvasGo.transform.localPosition.y`
（現在 −0.305）で全体を上下できる。グラフは子なので追従する。

## 記録

同じ数値は30秒ごとに logcat にも出る（`[DebugPanel] FPS=`）。
**パネルでしか読めない数値を作らない**のは CLAUDE.md の規約。
今回の不具合は「ログには出ていたので判定はできたが、装着中に
リアルタイムで読めなかった」という形で規約に助けられている。
