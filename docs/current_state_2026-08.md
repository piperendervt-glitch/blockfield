# 現状スナップショット（2026-08時点）

**本文書は 2026-08 時点のスナップショットであり、以降更新されない可能性がある。**
数値・構成は執筆時点の実測であり、参照する際は現行コードと突き合わせること。

作成経緯: PC側フィールドサーバによる場更新のオフロード（exp/remote-field）を
検討するための事前調査（Phase 0）として実施。調査結果を受けて**オフロードは
見送り**となったが、調査内容自体は現状把握として有用なため記録として残す。
見送りの判断と再検討トリガは docs/design/roadmap.md の Demo 6 節を参照。

事実と推測を区別して記述する。推測には「推測」と明記する。

---

## 1. スティグマジー場の実装状況

**事実。** 場は2種類、エージェントは5種類が実装済み。

| 場 | 型 | 性質 | 実装 |
|---|---|---|---|
| `Suitability` | `Field` | **静的**（ワールド生成時に一度計算、ブロック変更時に自セル＋4近傍のみ局所再計算） | `World.ComputeSuitability` / `RecomputeSuitabilityAt` |
| `Vegetation` | `VegetationField` | **動的**（毎tick 堆積→拡散→減衰） | `SimCore/Ecology/VegetationField.cs` |

エージェント: `EntityKind = { GrassTuft, Flower, Sheep, Pig, Wolf }`。
hunger / breedCooldown を持ち、スポーン・徘徊・摂食・捕食・繁殖・餓死が動作。
草食獣は植生場の勾配を読んで移動方向を決める（`FindVegetationGradientFacing`）
＝場読み行動が成立している。

「場が繁殖の主体」も実装済み:
植物スポーン確率 = `suitability × max(vegetation, vegetationFloor)`。
Demo 3 M4 でクラスタ化 0.74〜0.85（5シード）を定量確認済み。

---

## 2. 拡散項の有無 — 確定: 拡散は存在する

**事実。** コードから確定できる。推測ではない。

`VegetationField.Update`:

```csharp
// 拡散: 4近傍平均への lerp（ダブルバッファ、順序非依存）
float avg = count > 0 ? sum / count : v;
m_Scratch[x + width * z] = v + (avg - v) * diffuseRate;
// 減衰しつつ書き戻し
float keep = 1f - decayRate;
Values.Set(x, z, m_Scratch[x + width * z] * keep);
```

- 更新式: **φ' = (φ + (avg₄ − φ)·r) · (1 − decay)** — 全セル走査の線形ステンシル演算
- 既定値: **vegetationDiffuse = 0.15 / vegetationDecay = 0.02**
- 呼び出しは無条件・**毎tick**（`Simulation.Tick` → `UpdateVegetation` → `Vegetation.Update`）

### 設計文書との関係（事実）

docs/design/stigmergy_vision.md §7 は「最初の最小実装は『場2〜3種＋減衰＋閾値
スポーン』で十分」と書き、**拡散には言及していない**。拡散は Demo 3 (E1) で
M4（クラスタ化）を成立させるために実装側が追加した。文書と実装は矛盾しないが、
文書は拡散を想定していない。

### 規模との関係（この調査の結論を決めた点）

拡散があるとローカル再現は全セル走査になる。**走査が必要なのは事実だが、
現行スケールではコストが問題にならない。**

- 場のセル数 = 50 × 50 = **2,500セル**。1セルあたり4近傍読み＋2回の乗加算
- tick レート = **1 Hz**
- → 1秒あたり約1万回の float 演算。Quest 3 の CPU では計測不能なレベル

「拡散があるから compute shader / PC オフロード」という分岐は、
**50×50 では成立しない**。部屋スケールでも 1〜10Hz なら CPU で足りる。

（2026-08-09 追記・実測で確定）Demo 4.5 の部屋スケールは仮置きの
200×200 = 4万セルではなく、**81×67 = 5,427セル**だった。
部屋バウンズの実測は **3.19×2.07×2.60m**（4cmセル）。
以前 demo-3.1 で記録した「4.5×2.1×4.2m」は誤りで、MeshRecon が
ローカル AABB の8隅を変換して包含していたための過大値だった
（回転を含むと膨らむ）。RoomScanner の実頂点計算が正しい。頂点数16,088は両者一致。
つまり部屋スケールは箱庭（2,500セル）の約2倍にすぎず、
compute shader / PC オフロードの分岐はさらに遠のいた。

**推測**: compute shader が必要になるのは 10⁶ セル級か、tick が 60Hz 級に
なった場合。

---

## 3. 場のデータ構造

**事実。**

| 項目 | 値 |
|---|---|
| 次元 | **2次元 (x, z)**。高さ y は持たない |
| セル数 | Width × Depth = 50 × 50 = **2,500** |
| 型 | **`float[]` 密・平坦**（`index = x + Width*z`） |
| 疎/密 | 密。疎表現・シリアライズは未実装 |
| サイズ | 1場あたり 2,500 × 4B = **約 9.8 KiB** / **2場で約 20 KiB** |

`Field` のクラスコメントに「将来は susuwatari-mirror 方式（スパースシリアライズ
＋スキーマバージョニング）へ拡張予定」と明記（roadmap 横断原則1）。

**場への書き込み箇所は4つのみ**（grep で網羅確認）:

1. `Vegetation.Deposit` — 植物存在セル（毎tick、最大200セル）
2. `Vegetation.Update` — 拡散＋減衰（毎tick、全セル）
3. `World.ApplyPendingActions` — `PlayerBreakPlant` 時 ×0.5（1セル）
4. `World.ApplyBlockChangeFeedback` — 地形 Break 時 ×0.5（1セル）

**概算（推測）**: 疎な堆積項のみを送る場合のサイズは、植物200個 ×
(varint index 2B + 量子化値 1〜2B) ≈ **600〜800 B/tick** が上限。
実際は植物数に比例する。

---

## 4. tick 構造

**事実。**

- **レート: 1 Hz**（`TerrainField.k_TickInterval = 1f`）
- **駆動**: Unity メインスレッドの `TerrainField.Update()` 内で `Time.deltaTime` を
  積算し `while` ループで消化（フレーム落ち時はキャッチアップ）。**別スレッドではない**
- **tick 内の順序は固定**（決定論の根拠）:

  ```
  ApplyPendingActions（プレイヤー操作、RNG非消費）
  → SpawnPlants → SpawnAnimals → UpdateVegetation
  → UpdateHerbivores → UpdateWolves → Breed
  → PopulationLog.Record → TickCount++
  ```

- **描画との関係**:
  - 地形: `World.DirtyChunks` に積まれた変更チャンクのみ再メッシュ（実測 0〜5ms）
  - エンティティ: `EntityRenderer` が 0.3秒の位置・回転補間で表示。
    シム状態（真実）と表示の分離は Demo 2 D5 で確立済み

---

## 5. SimCore の asmdef 構成と外部依存

**事実。**

```json
{ "name": "BlockField.SimCore", "references": [], "noEngineReferences": true }
```

- **外部依存ゼロ**。UnityEngine 非依存が asmdef で強制済み
- 使用している BCL API: `System.Collections.Generic`、`System.Math`/`MathF`、
  `System.BitConverter`、`System.Text.StringBuilder`
- **PC 側からの参照実績（事実）**: 2026-08 時点で、
  `<Compile Include="Assets/Scripts/SimCore/**/*.cs" />` のみの .NET 9 コンソール
  プロジェクトから SimCore をヘッドレス実行し、Demo 1/3/4 の数値検証を実施した
  実績がある。コードのコピーなしで PC 側から参照する構成は検証済み

他の asmdef: `BlockField.Runtime`（SimCore, ARFoundation, ARSubsystems,
InputSystem, UnityEngine.UI）、`BlockField.Editor`、`BlockField.Tests.EditMode`。
**ネットワーク関連コードは存在しない。**

---

## 6. scripts/ 配下

**事実。** 全て UTF-8 BOM 付き PowerShell（BOM なしだと 5.1 が ANSI 誤認し
パースエラーになる）。

| スクリプト | 役割 |
|---|---|
| `run_tests.ps1` | EditMode テスト（batchmode）。Lockfile 検出で exit 10。終了コード透過。pre-push フックが呼ぶ |
| `build_quest.ps1` | Android APK ビルド → `Builds/blockfield.apk` |
| `deploy.ps1` | Unity 同梱 adb で install -r → monkey 起動 |
| `restart_app.ps1` | force-stop → am start（M1 アンカー復元検証用） |
| `reset_anchor.ps1` | `pm clear`（アンカー＋権限初期化） |

`.githooks/pre-push` が `run_tests.ps1` を呼び、`core.hooksPath=.githooks` 設定済み。

---

## 7. Demo 進捗と git tag

**事実。**

- タグ: `scaffold-0.1` → `env-verified-0.1` → `demo-0.1` → `demo-1.1` →
  `demo-2.1` → `demo-3.1` → **`demo-4.1`（最新）**
- EditMode テスト **45件**（全パス）
- roadmap 上の次: **Demo 4.5（Room Terrain）**。
  PC 側バッチ更新は **Demo 6** に配置

---

## 8. 再利用可能な既存資産

**事実。** 分散実行・永続化を将来検討する際に使える実装が既にある。

| 要件 | 既存実装 |
|---|---|
| 場のハッシュ | `World.ComputeContentHash()` — 地形＋出所＋適性場＋植生場＋エンティティ（hunger/breedCooldown 含む）＋tick を FNV-1a 64bit で畳み込み |
| f(シード, イベントログ) 決定論 | `World.Replay(terrainParams, simParams, events, ticks)`。Demo 4 M3 テストで Place/Break/BreakPlant 混在のリプレイ bit-exact 一致を確認済み |
| イベント形式 | `SimEvent { tick, type, cell, blockId, applied }`（プレーン構造、JSON 化容易）＋ `EventLog`。`EnqueuePlayerAction` で次tick先頭適用 |
| 差分通知の前例 | `World.DirtyChunks` / `ConsumeDirtyChunks(buffer)` — 変更セル→チャンク単位の通知とクリアの口 |
| 表示と真実の分離 | `EntityRenderer` の 0.3秒補間、`BlockInteractor` の楽観的仮表示（Tick適用で本表示に交代） |
| 固定レイヤー原則 | `BlockOrigin.Player` セルは `TrySetBlockEcology` が false を返して不変更。ただし現状の生態系コードは地形を書き換えないため、この保護が実際に効くのは将来の木の成長等から |

**設計文書との整合（事実）**: stigmergy_vision.md §4「処理の分担」は
「ワールド状態・場の管理、スティグマジーのバッチ更新 = ローカルPC」
「表示・トラッキング・操作 = Quest3」と記述している。§4 は実測より大きい規模を
想定した記述であり、設計の終着点としては維持される。実施時期の判断は
roadmap の Demo 6 節に記録した。

---

## 9. 確定できなかった / 未検証の事項

将来同種の検討を行う際に、最初に潰すべき論点。

### (1) x64 PC と ARM64 Quest 間の float bit-exact 一致

- 場の更新は +, −, ×, ÷ のみ（IEEE754 で決定論的）なので**理論上は一致するはず**
  だが、これは**推測**であり未検証
- リスク要因: IL2CPP(ARM64) と CoreCLR(x64) で **FMA 契約**（a*b+c の融合）や
  自動ベクトル化の差が出ると最下位ビットがずれる。`ValueNoise` / `Mulberry32` は
  整数演算主体なので安全だが、場の float 演算は未検証
- 分散実行を再検討する場合、**bit-exact 不一致の原因は「分割線のタイミング依存」
  と「アーキテクチャ差」の2つがあり得る**。切り分けのため、同一アーキ間での
  比較を先に取ること

### (2) エンティティ配信量（場だけでは足りない）

- プレイヤーが「動いている」と感じるのはエンティティである。PC を権威にすると
  場だけでなくエンティティ状態も配信対象になる
- **概算（推測）**: 動物は毎tick移動し得るので 30体 ×
  (id + kind + cell + facing + hunger) ≈ **240〜400 B/tick**。植物は静的なので差分のみ
- **推測**: 場のパッチ（600〜800B）とエンティティ差分（〜400B）で合計
  **1 KB/tick 前後**。10Hz でも 10 KB/s 程度で、帯域は制約にならない

### (3) 場に高さ次元がない

- 現状の場は 2D (x,z)。Demo 4.5 の多層地形（机上／床）では 2D の場が破綻する
  可能性がある
- **推測**: Room Terrain では場を層ごとに持つか 3D 化が必要
- 分散実行の実装を 2D 前提で固めると、Demo 4.5 で再設計になる
