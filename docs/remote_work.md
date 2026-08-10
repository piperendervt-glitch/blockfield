# リモート作業ガイド（帰省先などから）

このマシン（Windows 11 / `C:\dev\blockfield`）へ SSH でつなぎ、
Claude Code を動かして作業するための手順と、
**リモートでできること・できないこと**の切り分け。

---

## 1. できること / できないこと

| 作業 | リモート | 理由 |
|---|---|---|
| SimCore の実装・リファクタ | **できる** | UnityEngine 非依存（asmdef で強制） |
| ヘッドレス検証（SimRunner） | **できる** | .NET コンソールアプリ。画像も PNG で出る |
| 事前登録・設計文書の執筆 | **できる** | |
| git 操作 | **できる** | ただし push は pre-push ゲートを通る（下記） |
| **EditMode テスト** | **条件つき** | Unity Editor が**閉じている**必要がある。開いていると `Temp/UnityLockfile` で失敗する。SSH 越しに Unity を閉じるのは危険（作業中の変更を失う）ので、帰省前に閉じておくこと |
| **push** | **条件つき** | pre-push ゲートが EditMode テストを走らせる。上と同じ制約 |
| Unity Editor での目視確認（TerrainPreview） | **できない** | GUI が要る。RDP なら可能だが実用的でない |
| APK ビルド・デプロイ | **できない** | Quest が手元に無い |
| 実機セッション | **できない** | |

**帰省前にやっておくこと: Unity Editor を閉じる。**
これを忘れるとリモートからテストも push もできない。

---

## 2. 事前設定（帰省前に実施）

### 2a. OpenSSH サーバの有効化

管理者権限の PowerShell で:

```powershell
# インストール状況の確認
Get-WindowsCapability -Online -Name OpenSSH.Server*

# 未インストールなら
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0

# サービスを自動起動にして開始
Set-Service -Name sshd -StartupType Automatic
Start-Service sshd

# 状態確認（Running になっていること）
Get-Service sshd
```

ファイアウォールの規則（既定で作られるが、無ければ）:

```powershell
Get-NetFirewallRule -Name *OpenSSH-Server* | Select-Object Name, Enabled
New-NetFirewallRule -Name sshd -DisplayName "OpenSSH Server (sshd)" `
  -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22
```

### 2b. 公開鍵認証（パスワード認証より安全）

クライアント側（帰省先の端末）で鍵を作り、公開鍵をこのマシンへ置く。
**Windows の管理者ユーザーは `authorized_keys` の場所が違う**ので注意:

- 一般ユーザー: `C:\Users\<user>\.ssh\authorized_keys`
- **管理者グループのユーザー: `C:\ProgramData\ssh\administrators_authorized_keys`**

```powershell
# 管理者ユーザーの場合
notepad C:\ProgramData\ssh\administrators_authorized_keys
# 公開鍵を1行貼る。保存後に権限を締める（これをしないと鍵が無視される）
icacls C:\ProgramData\ssh\administrators_authorized_keys /inheritance:r `
  /grant "Administrators:F" /grant "SYSTEM:F"
Restart-Service sshd
```

### 2c. 外からつなぐ経路

同じ LAN 内でなければ、以下のどれかが要る:

- **Tailscale / ZeroTier**（推奨。ルータ設定不要、NAT 越え、鍵ベース）
- ルータのポート転送 + DDNS（22番を直接開けるのは避け、別ポートにする）

`sshd_config`（`C:\ProgramData\ssh\sshd_config`）でパスワード認証を切っておく:

```
PubkeyAuthentication yes
PasswordAuthentication no
```

変更後 `Restart-Service sshd`。

### 2d. スリープ・休止の無効化（**必須**）

スリープするとリモートから到達できなくなる。管理者 PowerShell で:

```powershell
# 現在の設定を確認（AC = 電源接続時）
powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE

# AC 接続時のスリープ・休止・ハイブリッドスリープを無効化
powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
powercfg /change monitor-timeout-ac 15   # 画面だけは消えてよい
powercfg /hibernate off

# 高速スタートアップも切る（休止状態が絡むため）
powercfg /h off

# 確認
powercfg /query SCHEME_CURRENT SUB_SLEEP
```

**ネットワークアダプタの省電力も切る**（これを忘れると
スリープしなくてもネットワークだけ落ちることがある）:

```powershell
Get-NetAdapter | Where-Object Status -eq Up | ForEach-Object {
  Set-NetAdapterPowerManagement -Name $_.Name -AllowComputerToSleep Disabled -ErrorAction SilentlyContinue
}
```

### 2e. 動作確認（帰省前に必ず1回）

クライアントから:

```bash
ssh <user>@<host>
# つながったら
cd C:/dev/blockfield
claude --version
git status
dotnet run -c Release --project tools/SimRunner -- --seeds 2 --ticks 200 --out runs/sshtest
```

**スマホのテザリングなど、家の LAN の外から**試すこと。
LAN 内でしか試していないと、帰省先で経路が無いことに気づく。

---

## 3. SSH 越しの Claude Code

```bash
ssh <user>@<host>
cd C:/dev/blockfield
claude
```

### 長時間タスクの扱い（重要）

SSH は切れる。**接続が切れても走り続け、結果がファイルに残る**形にする。

```powershell
# 悪い例: 結果がコンソールにしか残らない
dotnet run -c Release --project tools/SimRunner -- --seeds 48

# 良い例: 出力先を明示し、ログもファイルへ
dotnet run -c Release --project tools/SimRunner -- --seeds 48 --ticks 3000 `
  --conditions default,trample-off --out runs/20260810_check `
  *> runs/20260810_check.log
```

Claude Code に頼むときは「バックグラウンドで実行し、
完了したら `runs/<名前>/report.html` を読んで要約して」と伝える。
Claude Code のバックグラウンド実行はセッションが続くかぎり生き、
完了時に通知される。

**セッションごと切れても平気にしたい場合**は、
Windows のジョブとして投げてから抜ける:

```powershell
Start-Process -WindowStyle Hidden -FilePath "dotnet" -ArgumentList @(
  "run","-c","Release","--project","tools/SimRunner","--",
  "--seeds","48","--ticks","3000","--out","runs/overnight"
) -RedirectStandardOutput runs/overnight.log -RedirectStandardError runs/overnight.err
```

---

## 4. SimRunner の使い方

SimCore をヘッドレスで回し、**結果を全部ファイルに落とす**ツール。
Unity Editor を開かずに検証できるので、リモート作業の主力になる。

```bash
# 開発中の反復（5シード。数秒で終わる）
dotnet run -c Release --project tools/SimRunner -- --seeds 5 --ticks 2000

# 最終判定（48シード、対照つき）
dotnet run -c Release --project tools/SimRunner -- \
  --seeds 48 --ticks 3000 --conditions default,trample-off --out runs/final
```

### オプション

| オプション | 既定 | 説明 |
|---|---|---|
| `--seeds N` | 48 | シード数。**開発中の反復は5でよい**（CLAUDE.md: 最終判定は48シード） |
| `--ticks N` | 3000 | ティック数 |
| `--size N` | 50 | 箱庭の一辺 |
| `--parallel N` | コア数-2 | 並列度。シードは互いに独立なので決定論は壊れない |
| `--conditions a,b` | default | 条件（下記） |
| `--images N` | 1 | 画像を出す代表シード数。条件ごとに再実行するので増やすと遅い |
| `--out DIR` | `runs/日時` | 出力先 |

### 条件

| 名前 | 内容 |
|---|---|
| `default` | 既定パラメータ |
| `trample-off` | 踏み荒らしの**効果のみ**無効（書き込みは残す）。Demo 8 第3段 M2 の対照 |
| `nutrient-off` | 死の場の養分効果のみ無効。Demo 8 第2段 M2 の対照 |
| `fear-off` | 草食獣が恐怖場を読まない。Demo 8 第2段 M3 の対照 |

**対照の作り方の原則**: 機構を切るときは**場への書き込みは残し、
効果だけを0にする**。書き込みまで止めると RNG の消費列が変わり、
世界の進行そのものが別物になる。「その機構の効果」ではなく
「別の世界」を比べてしまう。

### 出力

| ファイル | 内容 |
|---|---|
| **`report.html`** | **これ1枚で全部見られる。**画像は data URI で埋め込み済みなので、SCP で1ファイル持ち帰れば済む |
| `summary.json` | 条件ごとの集計＋シードごとの ContentHash（決定論の追跡用） |
| `population.csv` | `condition,seed,tick,plants,herbivores,wolves`（10ティック間隔） |
| `images/*.png` | 地形俯瞰と場のヒートマップ |

ヒートマップの色と濃さは**実機・エディタのオーバーレイと同じ規約**
（`EcologyStats.FieldDisplayScale` / `FieldDisplayIntensity`）を使う。
PNG で見た印象と実機で見た印象がずれないようにするため。

- 植生=緑 / 恐怖=赤 / 獲物=青 / 死=マゼンタ / 踏み荒らし=茶
- 適性0のセル（壁や穴）は**暗い灰色**。「場が薄い」のか
  「そもそも対象外」なのかを区別できる

### 回帰検知（`--compare`）

前回の `summary.json` と突き合わせ、`diff_report.html` を出す。

```bash
dotnet run -c Release --project tools/SimRunner -- \
  --compare runs/nightly_20260810/summary.json
```

見るものは3つ。

1. **ContentHash の一致**（最優先）。**コードを変えていないのに不一致なら
   決定論 f(シード, イベントログ) が破れている。**本プロジェクトの前提が
   崩れたということなので、他の指標より先にこれを調べる。
   不一致があると赤いバナーが最上部に出て、終了コードが 2 になる
2. **M5**（生態系の安定条件）の合否と、その内訳
3. 各指標の差分。赤=悪化 / 橙=10%以上動いた / 緑=改善。
   指標の多くは良し悪しが一意でないため、橙は「注意して見る」印であって
   不合格ではない

**終了コード**: `0` 問題なし / `1` M5 不合格 / `2` 決定論の破れ。

#### M5 の判定を 0/48 にしていない理由

草食獣ギルドと植物の全滅は 0 を要求するが、**狼だけは 25% までを許容**する。
狼の全滅は死の場も踏み荒らしも切った状態でも **3/48（約6%）起きる**
生態系そのものの性質であり（Demo 8 第2段の48シード計測、実測幅 2〜6/48）、
0 を要求すると夜間バッチが毎晩「不合格」を出して、
本当の退行が起きたときに気づけなくなる。

シードが 12 未満の実行では、1件の全滅が簡単に許容率を超えてしまい
「率」として意味を持たないため、狼の項目は評価しない。

### 長時間実験（`--checkpoint-interval`）

指定間隔で `checkpoints.csv` に途中経過を追記する。**実行中でも別窓から読める**
（AutoFlush 済み）。個体数・累計・各場の平均と最大・個体の重みの平均と標準偏差を
記録する。

```bash
dotnet run -c Release --project tools/SimRunner -- \
  --seeds 5 --ticks 100000 --checkpoint-interval 2000
```

### 指標の読み方（間違えやすい点）

- **墓場の植物密度比は 1.0 と比べない。** 餓死は餌の乏しい場所で起きるため、
  養分効果が無くても墓場の草は少ない（対照で 0.35）。**対照条件と比べる**
- **迂回率は 50% と比べない。** 恐怖場は狼の周りに山を作るので、
  避けていなくても「下る」方向が多くなる。対照（w_fear=0）は約55%
- **比はシードごとの平均ではなく合算**（分子分母を足してから割る）。
  シードごとに平均すると、植物の少ないシードに引きずられる

---

## 5. 自動シミュレーション実行環境

### 夜間バッチ（`scripts/nightly_sim.ps1`）

標準セット（48シード × 3,000ティック、現行 `SimParams`）を毎晩回し、
**直近の nightly と自動比較**する。実測 約30秒。

- 出力: `runs/nightly_<yyyyMMdd>/`（`report.html` / `diff_report.html` /
  `summary.json` / `population.csv` / `images/` / `run.log`）
- 比較対象は「前日」ではなく**直近の nightly**（PC が落ちていた日は飛ぶため）
- **30日より前の `nightly_*` は自動削除**する。画像を埋め込んだ
  `report.html` が1回あたり数百KB〜数MBになるため
- 終了コード: `0` 問題なし / `1` M5 不合格 / `2` 決定論の破れ / `10` 前提エラー

手動実行:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\nightly_sim.ps1
```

#### タスクスケジューラへの登録（管理者権限で実行）

毎日 03:00 に実行する。

```powershell
$action = New-ScheduledTaskAction -Execute "powershell.exe" `
  -Argument '-NoProfile -ExecutionPolicy Bypass -File "C:\dev\blockfield\scripts\nightly_sim.ps1"' `
  -WorkingDirectory "C:\dev\blockfield"

$trigger = New-ScheduledTaskTrigger -Daily -At 03:00

# スリープを無効化してある前提だが、万一スリープしていても起こして実行する。
# 電源接続時のみ・バッテリ駆動で止めない設定にはしない（デスクトップのため）
$settings = New-ScheduledTaskSettingsSet `
  -WakeToRun `
  -StartWhenAvailable `
  -DontStopIfGoingOnBatteries `
  -AllowStartIfOnBatteries `
  -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
  -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName "blockfield-nightly-sim" `
  -Action $action -Trigger $trigger -Settings $settings `
  -RunLevel Limited -Description "blockfield: 48シード×3000ティックの夜間回帰検証"
```

`-StartWhenAvailable` を付けるのは、PC が落ちていて 03:00 を逃した場合に
次回起動時へ振り替えるため。`-MultipleInstances IgnoreNew` は、
前回が終わっていないときに二重起動しないため。

確認と手動起動:

```powershell
Get-ScheduledTask -TaskName "blockfield-nightly-sim"
Get-ScheduledTaskInfo -TaskName "blockfield-nightly-sim"   # 前回実行結果
Start-ScheduledTask -TaskName "blockfield-nightly-sim"     # 今すぐ実行
```

解除:

```powershell
Unregister-ScheduledTask -TaskName "blockfield-nightly-sim" -Confirm:$false
```

**翌朝に見るもの**: `runs/nightly_<日付>/diff_report.html` を開き、
最上部のバナーを確認する。赤（決定論の破れ）なら最優先で調査する。

### 長時間実験（`scripts/longrun_sim.ps1`）

10万ティック × 5シードをバックグラウンドで走らせる。

**目的**: 生態系の長期安定性、場の飽和挙動（死の場は τ≈333 なので
数万ティックで飽和するはず）、進化導入後の重み分布の変化を観察する。
Demo 6「不在中の進行」の前哨実験。

```powershell
# 既定（10万ティック × 5シード、2000ティックごとにチェックポイント）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\longrun_sim.ps1

# 縮小版（動作確認用）
powershell ... -File scripts\longrun_sim.ps1 -Ticks 10000 -Seeds 2

# 前面で実行（進捗を直接見る）
powershell ... -File scripts\longrun_sim.ps1 -Foreground
```

バックグラウンド起動は Claude Code や親シェルに紐付かない独立プロセスなので、
**SSH セッションが切れても走り続ける**。進捗は
`runs/longrun_<日時>/run.log`、途中経過は同ディレクトリの
`checkpoints.csv` で追える。

## 6. リモートで詰まったときの確認順

1. `Get-Service sshd` — サービスが動いているか
2. `Get-Process Unity` — Editor が開いていないか（テスト・push が失敗する）
3. `Test-Path C:\dev\blockfield\Temp\UnityLockfile` — 残骸のロックファイル。
   **Unity プロセスが無いことを確認してから**削除する
4. `powercfg /query SCHEME_CURRENT SUB_SLEEP` — スリープ設定が戻っていないか
5. `dotnet --version` — SDK があるか
