---
title: "M7 Hardening 実装指示書（Windows ネイティブ側）"
subtitle: "Unity Import Processor / 負荷・復帰・Security（Authenticator）・Release Candidate ビルド"
document_id: "IMPL-M7-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M7 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / R3 / VContainer / UniTask"
related_document: "10B_M7実装指示書_WSL2側_v0.1.md, 09A_M6実装指示書_Windows側_v0.1.md, 09B_M6実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M7 Hardening 実装指示書：Windows ネイティブ側 v0.1

本書は M7（Hardening）の作業を **Windows ネイティブ側**（Unity Import Processor による Manifest→Prefab 生成、クライアント/DS の負荷試験、再起動後のクライアント復帰、FishNet Authenticator の Security 厳格化、Release Candidate ビルド、PlayMode/EditMode テスト拡充）に限定して指示する。バックエンド/CI/Blender 検査は別冊 **10B（WSL2側）** を参照。第0章「分担と連携」は両冊で要点共通。

M7 は新機能を追加しない。**M1〜M6 で実装済みの Unity Client / Dedicated Server** を、MVP第3章の性能目標・第17章のセキュリティ要件・第18章の受入条件に対して **計測・検証・厳格化し、Release Candidate（RC）ビルド品質へ引き上げる**ことが目的である。

---

## 0. 分担と連携（要点・両冊共通）

### 0.1 M7 環境別 責務分担

| 観点 | 担当環境 | 主なタスク |
|---|---|---|
| Unity Import Processor（Manifest→Prefab） | **Windows** | Client Prefab / Server Prefab 生成、pivot/grid/Socket/Collider/LOD/Interaction Point |
| Unity Batchmode Import（受入） | **Windows** | Batchmode で Import が成功すること（15章 受入） |
| 負荷試験（描画/同期/tick クライアント側） | **Windows** | 2 Client + 20 AI の PlayMode 負荷、fps/同期計測 |
| 負荷試験ハーネス（バックエンド計測） | **WSL2** | tick_ms/レイテンシ/DB/NATS/Outbox 計測 |
| 再起動後のクライアント復帰 | **Windows** | セッション/Join 再取得、Full Snapshot 再同期 |
| 再起動復旧テスト（サービス/DB/NATS） | **WSL2** | snapshot staging→checksum→active、Outbox flush、inbox_dedup |
| Security（FishNet Authenticator） | **Windows** | 期限切れ/再利用/build 不一致/無効 Character 拒否 |
| Security（サーバー/秘密/経路） | **WSL2** | JWT/JoinTicket 署名検証、Refresh rotation、sslmode、Rate Limit、audit |
| Release Candidate ビルド | **両方** | Windows=Client(Win) + Linux DS、WSL2=サービスイメージ確定 |
| PlayMode/EditMode テスト拡充 | **Windows** | Manifest importer/Damage Matrix/AI Template/Station jobs 等 |

### 0.2 リポジトリ配置・境界・競合回避（M0 03A 0.2〜0.4 に準拠）

- 単一クローンを Windows 側（例 `C:\dev\living-world-survival`）に置く。Unity プロジェクトルートは `unity/SurvivalWorld/`（03A 0.2）。
- 改行/LFS 規約（`.ps1`=CRLF、`.sh`=LF、`.meta`/バイナリ資産=LFS）は 03A 0.3 のまま厳守。
- **Windows 側が触るのは `unity/` と `scripts/*.ps1` のみ**。`services/`, `proto/`, `infra/`, `assets-pipeline/`, `scripts/*.sh`, `Makefile`, `.github/` は WSL2（10B）が担当。
- `unity/SurvivalWorld/Assets/Generated/`（proto C#）は WSL2 が `buf generate` で生成する。Windows はコンパイルして消費するのみ（触らない）。

### 0.3 M7 境界成果物

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| アセット Manifest（厳格検査済み） | WSL2（Blender） | Windows（Import Processor） | `build/assets/manifest.json` + `*.glb` |
| Client Prefab / Server Prefab | Windows（Import Processor） | Unity（実行/ビルド） | `unity/SurvivalWorld/Assets/Generated/Prefabs/` |
| Linux Dedicated Server ビルド（RC） | Windows（クロスビルド） | WSL2/Docker（負荷/Soak/復旧） | `unity/SurvivalWorld/Build/Server/` |
| Windows Client ビルド（RC） | Windows | 配布/受入 | `unity/SurvivalWorld/Build/Client/` |

### 0.4 連携フロー（M7 代表例）

- **アセット**: WSL2 が `make assets`（決定的生成＋厳格検査）→ `build/assets/` に glb/manifest → Windows の Import Processor が Batchmode で Client/Server Prefab を生成（受入）。
- **負荷**: Windows が RC の Linux DS をクロスビルド → WSL2/Docker で DS 起動＋バックエンド計測（10B 3.1）、Windows は 2 Client を PlayMode/実 Client で接続し fps/同期を計測。
- **復旧**: WSL2 がサービス/DS を再起動（10B 3.3）→ Windows Client がセッション/Join を再取得し Full Snapshot で復帰。

---

## 1. 対象と前提（Windows側）+ M7 DoD

- OS: Windows。Unity Editor 6000.5.x はネイティブ動作。Linux Dedicated Server モジュール導入済み（03A 2.1）。
- 前提: M0 の DoD 緑（プロジェクトがコンパイル通過、Client/Server ビルド・Unity テストが実行可能）。M1〜M6 の Client/DS 機能（接続/移動、Inventory、Survival、AI、WorldState 連携、Economy）が実装済み。
- 本書の完了で、Import Processor が Batchmode で Prefab を生成し、負荷/復帰/Authenticator 検証が通り、RC の Windows Client と Linux DS がビルドできる状態にする。

### 1.1 M7 DoD（MVP第19.1 の Definition of Done を M7 に反映）

- [ ] **Import 受入（15章）**: 生成コマンド1回（WSL2 の `make assets`）で再生成された Kit を、**Unity Batchmode Import が成功**して Client/Server Prefab を生成する。
- [ ] **サーバー権威**: Client 側に Damage/Loot/Drop/Craft/Purchase Price を確定する処理が無い（入力送信のみ）。座標/所有権は Server 権威（MVP-SEC-005/006、第19.1）。
- [ ] **Client Cache に重要データを置かない**: Inventory/所持金/位置/拠点状態は Server 保存から復元（AT-002、第5.4/第19.1）。Cache は表示・設定のみ。
- [ ] **テスト緑**: EditMode（Definitions/Template parser/Damage Matrix/Manifest importer）と PlayMode（Movement/Interaction/AI Template/Station jobs）が CI/ローカルで成功（第18.1）。
- [ ] **DS Headless**: RC の Linux DS が Headless 起動し、Readiness/Graceful Shutdown/Snapshot を確認（WSL2 で実行、第19.1）。
- [ ] **容量目標の記録**: Profiler で 1080p/60fps 目標や同期/tick を計測し、未達は数値付き既知 Issue（AT-020、第3章）。

---

## 2. 前提成果物（M0〜M6 で構築済み）

| Milestone | 本書が依存する Unity/DS 成果物 |
|---|---|
| M0 基盤 | URP プロジェクト、R3/VContainer/UniTask/FishNet/Input System 導入、`BuildScript.cs`、`unity_build_client.ps1`/`unity_build_server.ps1`/`unity_test.ps1`、`Assets/Generated/`（proto C#） |
| M1 接続 | Auth ログイン、Matchmaking Join、**JoinTicketAuthenticator**（Ticket Schema/署名/期限/build/server 検証）、FishNet 接続、2 Client 3D TPS/WASD 移動 |
| M2 Inventory/Save | 共通 Inventory 表示、Item Definition、World Load、Cache 削除復旧（Full Snapshot 受領） |
| M3 Survival | 採掘/Development/製作/狩猟/料理/Hunger/Waste/清掃の Client 操作＋Server 確定表示、DamageService（Player↔AI 非干渉） |
| M4 AI | AIActor 表示、Template Runner の可視化、20 AI |
| M5 WorldState/LLM | World Event Marker、AI Decision 反映表示 |
| M6 Economy | BuyerNPC 表示、購入 UI、購入確定の Runtime Inventory 反映（永続は API） |

- Import Processor が扱う Manifest（`build/assets/manifest.json`：asset_id/version、sockets、collider、lods、interaction_points、triangles、negative_scale、non_manifold_edges）は WSL2（10B 3.7）が厳格検査済みで供給する。

---

## 3. 実装対象（Windows側・観点別）

### 3.1 Unity Import Processor（MVP第15章 / 基本設計第12章）

**目的**: WSL2 が生成した Manifest＋glb を読み、**Client Prefab と Server Prefab を自動生成**する。人手のプレハブ組みを排し、決定的・再現可能にする。

**実装**（`unity/SurvivalWorld/Assets/Editor/AssetImportProcessor.cs`）:

- 入力: `build/assets/manifest.json`（各モジュールの `kit/name/asset_id/version/glb/sockets/interaction_points/lods/has_collider/triangles`）と対応する `*.glb`。
- **AssetPostprocessor**（`OnPostprocessModel` / メニューコマンド `Tools/Survival/Import Assets`）で glb Import 時に以下を設定:
  - **bottom-center pivot**: モデル原点を底面中心に合わせる（Manifest の pivot 規約に従い、glb 生成側が底面原点＝03B 既存の `location=(0,0,size/2)` 生成に対応）。
  - **grid 寸法**: Manifest の grid/寸法にスナップ（モジュール接続用）。
  - **Socket Empty**: Manifest の `sockets`（例 `socket_top`）を子 Empty（Transform）として付与。命名を保持。
  - **Collider Mesh**: `UCX_` 命名の Collider メッシュを MeshCollider（凸/非凸を用途で選択）として設定。`has_collider` 必須。
  - **LOD**: `lods`（LOD0…）を LODGroup に構成。
  - **Interaction Point**: `interaction_points`（例 `ip_use`）を子 Empty＋タグ付けで付与。
- **Client Prefab**: 描画・LOD・Material・Collider・Interaction を含む（第12章「Client 用は描画・LOD・Material を含む」）。出力 `Assets/Generated/Prefabs/Client/<kit>_<name>.prefab`。
- **Server Prefab**: Collider・NavMesh Modifier・Interaction metadata 中心の**軽量**（描画/Material を省く）。出力 `Assets/Generated/Prefabs/Server/<kit>_<name>.prefab`。
- **決定性**: 同じ asset_id/version の入力から同じ Prefab を再生成（差分が出たら Manifest 変化のみが原因）。生成物は `Assets/Generated/Prefabs/` に置き、コミット対象にするか .gitignore にするかを M7 で確定（受入は再生成成功で判定するため .gitignore + 生成手順の固定を推奨）。

### 3.2 Unity Batchmode Import（受入 / 第15章）

- `Assets/Editor/BuildScript.cs` に **`ImportAssets()`（static）** を追加し、`AssetImportProcessor` を Batchmode から起動（glb→Prefab 生成）。失敗時は `EditorApplication.Exit(1)`。
- `scripts/unity_import_assets.ps1`（新規、CRLF）:

```powershell
$Unity = "C:\Program Files\Unity\Hub\Editor\6000.5.xfx\Editor\Unity.exe"
& $Unity -batchmode -nographics -quit -logFile - `
  -projectPath "$PSScriptRoot\..\unity\SurvivalWorld" `
  -executeMethod BuildScript.ImportAssets
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

- **受入（第15章）**: WSL2 の `make assets` で再生成した `build/assets/` を入力に、上記スクリプトが **exit 0** で Client/Server Prefab を生成すること。missing socket/negative scale/non-manifold/triangle budget/collider 欠如は WSL2 の `validate.py`（10B 3.7）で事前に弾かれている前提だが、Import 側でも Manifest の必須フィールド欠落を検出したら失敗させる（多重防御）。

### 3.3 負荷試験（クライアント/DS 描画・同期・tick / 第3章 / AT-020）

**目的**: 第3章の目標に対し、Client 側の描画/同期/操作応答を計測する（バックエンド tick/レイテンシは WSL2 が計測）。

| 指標 | 目標（第3章） | 計測方法 |
|---|---|---|
| Client Frame | 1080p 試験環境で 60fps 目標 | PlayMode/実 Client の Unity Profiler（frame time、GC、draw calls） |
| 通常操作応答 | P95 200ms 以内（Server 処理） | Command 送信→Server 確定反映の往復を Client 側で時刻計測（参考値、正は WSL2） |
| 同期 | 2 Client 相互移動が表示 | 2 Client で相互の Transform 複製を目視/自動確認（AT-001） |

**実装**:

- **PlayMode 負荷シーン**（`Assets/Tests/PlayMode/LoadTest_2C20AI`）: 2 Client 相当＋20 AI＋動物/World Item を含む World_MVP を DS に接続して起動し、一定時間の frame time/同期を記録。
- **実測**: WSL2 が RC の Linux DS を Docker/Headless 起動（10B 3.1）→ Windows から実 Client 2 本（または 1 台 2 インスタンス）で接続し、Profiler で 1080p/60fps を確認。AT-020 目標（16 Player/20 AI/80 Animals）は DS/バックエンド側 Gate を WSL2 が測り、Client は代表構成（2 Client）で描画性能を記録。
- **記録**: フレーム/同期の計測結果を `build/reports/`（WSL2 と共有）へエクスポートするか、テスト成果物として残し、未達は数値付き既知 Issue（第19.1）。

### 3.4 再起動後のクライアント復帰（第16章 / AT-002・AT-018）

**目的**: サーバー/DS 再起動やセッション切断の後、Client が正しく復帰する（重要データは Server 保存から復元、Client Cache に依存しない）。

**実装/検証**:

- **Join Ticket 期限切れ/切断時の再取得**（第16章）: FishNet 切断を検知したら Client は Matchmaking（`/v1/matchmaking/join`）から Join Ticket を再取得して再接続する。期限切れは一般化エラーにせず再取得フローへ。
- **セッション再取得**: access token 失効時に `/v1/sessions/refresh` で再取得（refresh rotation は Auth 側＝WSL2、Client は 401→refresh→retry を実装）。
- **Full Snapshot 再同期**（第16章 Inventory version conflict）: Runtime と API/Snapshot の不整合検知時、Client は Server から **Full Snapshot** を受領して表示を差し替える（Client 側は権威を持たない）。
- **Cache 削除復帰（AT-002）**: ローカルキャッシュ削除後の再ログインで、Inventory/所持金/位置/拠点状態が Server 保存から復元されることを PlayMode/手動で確認。
- **DS 再起動復帰（AT-018 の Client 側）**: WSL2 が別 DS で復元（10B 3.3）した後、Client が再接続して World/AI/Buyer/自 Character を正しく再表示すること。

### 3.5 Security（FishNet Authenticator 厳格化 / 第17章 / 第11.3）

**目的**: `JoinTicketAuthenticator` を M7 で厳格化し、不正 Ticket を全て拒否する（MVP-SEC-004、第11.3、[R6]）。

| ケース | 挙動（拒否） | 検証（PlayMode/EditMode） |
|---|---|---|
| **期限切れ** | `expires_at` 超過 Ticket を拒否し FishNet 切断（第16章） | 過去 `expires_at` の Ticket で接続 → 拒否 |
| **再利用** | 一度消費済み（Auth `used_at` 非 NULL）Ticket を拒否 | 同一 Ticket で2回接続 → 2回目拒否（RedeemJoinTicket は WSL2 が単回消費） |
| **build 不一致** | `build_id` が DS と不一致なら拒否 | 異なる build_id の Ticket → 拒否 |
| **server 不一致** | `server_id` が当該 DS と不一致なら拒否 | 他 server 宛 Ticket → 拒否 |
| **無効 Character** | `character_id` が account に属さない/存在しないなら拒否 | 無効 character_id → 拒否 |
| **署名不正** | Auth 公開鍵で署名検証失敗なら拒否 | 改竄 Ticket → 拒否 |

- DS は Auth 公開鍵で **事前検証**し、単回消費の最終確定は gRPC `RedeemJoinTicket`（`used_at IS NULL` 条件更新、WSL2 側）で行う。Client 入力（Damage/座標/価格）を採用しないこと（MVP-SEC-005/006）を Command ハンドラ側でも再確認する。
- 監査: Ticket 消費・拒否理由を（Password/Token を出さずに）ログ出力（MVP-SEC-002/009 の Client/DS 側）。

### 3.6 Release Candidate ビルド（第19.1）

- **Windows Client（RC）**: `unity_build_client.ps1`（03A 5.2）で IL2CPP・Release 構成をビルド。`Build/Client/survival.exe`。
- **Linux Dedicated Server（RC）**: `unity_build_server.ps1`（`-standaloneBuildSubtarget Server`）で `Build/Server/survival-server.x86_64`。Headless 起動・Readiness・Graceful Shutdown・Snapshot を WSL2/Docker で確認できること（10B 3.8 と連携）。
- **版固定**: `Packages/packages-lock.json`/`ProjectVersion.txt`/FishNet 版を確定（FishNet 更新は Network smoke を Upgrade Gate に＝RISK-05）。
- **成果物**: RC の Client/DS を `build/reports/` の負荷/復旧結果と合わせて RC 判定に供する。

### 3.7 PlayMode/EditMode テスト拡充（第18.1）

- **EditMode**: Definitions、Template parser、**Damage Matrix**（Player↔AI 拒否＝AT-006/007）、**Manifest importer**（3.1 の Import 結果検証：socket/collider/LOD/interaction/pivot が Prefab に反映されるか）。
- **PlayMode**: Character movement、Interaction、AI Template、Station jobs、加えて 3.3 の負荷シーン、3.4 の再接続/復帰、3.5 の Authenticator 拒否ケース。
- `scripts/unity_test.ps1`（03A 5.2）で EditMode/PlayMode を実行し、CI/夜間（10B 3.6 の Network E2E）と連携。

---

## 4. 実装順序（Windows側）

| # | タスク | 完了確認 |
|---|---|---|
| W-1 | `AssetImportProcessor.cs`（Manifest→設定：pivot/grid/Socket/Collider/LOD/Interaction、3.1） | glb Import で属性が付与される |
| W-2 | Client Prefab / Server Prefab 生成（3.1） | `Assets/Generated/Prefabs/{Client,Server}` に生成 |
| W-3 | `BuildScript.ImportAssets()`＋`unity_import_assets.ps1`（3.2） | Batchmode Import が exit 0（受入・15章） |
| W-4 | Manifest importer の EditMode テスト（3.7） | socket/collider/LOD/pivot が Prefab に反映を検証 |
| W-5 | `JoinTicketAuthenticator` 厳格化（期限/再利用/build/server/無効char/署名、3.5） | 6 ケースの拒否 PlayMode テスト緑 |
| W-6 | 再接続/復帰（Ticket 再取得・session refresh・Full Snapshot、3.4） | 切断→再取得→復帰、AT-002 復元 |
| W-7 | PlayMode 負荷シーン（2 Client + 20 AI）＋Profiler 計測（3.3） | frame time/同期を記録、AT-020 Client 側 |
| W-8 | Damage Matrix / AI Template / Station jobs のテスト拡充（3.7） | EditMode/PlayMode 緑（AT-006/007 等） |
| W-9 | RC ビルド（Windows Client + Linux DS）（3.6） | 両成果物生成、DS が WSL2 で Headless 起動 |
| W-10 | 容量目標（1080p/60fps・同期・操作応答）の計測記録（3.3） | 数値記録、未達は既知 Issue 化 |

---

## 5. テスト・受入（MVP第18章の具体化）

### 5.1 本書が主担当/裏取りする受入試験

| AT | 主担当 | Windows側での具体化 |
|---|---|---|
| Batch Import Test（付録A / 第15章） | Windows | `unity_import_assets.ps1` が Batchmode で Client/Server Prefab を生成し exit 0 |
| AT-001 2 Client 同一 World | Windows | 相互移動が表示、所有外 Character を操作できない（ownership、MVP-SEC-005） |
| AT-002 Cache 削除復旧 | Windows | 再ログインで Inventory/所持金/位置/拠点が Server から復元 |
| AT-006/007 Player↔AI 非干渉 | Windows（表示/入力）＋WSL2（DamageService） | Player→AI で Damage 0/警告、AI→Player 候補が生成されない |
| AT-014 LLM 停止 | Windows（表示） | 60秒超でも Tick 継続表示、AI が Fallback 挙動へ（正は WSL2） |
| AT-018 Server 再起動 | Windows（Client 復帰）＋WSL2 | 再起動後に再接続し World/AI/Buyer/自 Character を再表示 |
| AT-020 負荷試験 | Windows（描画/同期）＋WSL2（tick/レイテンシ） | 2 Client 描画 60fps 記録、同期表示、ボトルネック記録 |

### 5.2 自動テスト区分（第18.1）での位置づけ

- **Unity EditMode**: Definitions、Template parser、Damage Matrix、**Manifest importer**（3.1/3.7）。
- **Unity PlayMode**: Character movement、Interaction、AI Template、Station jobs、負荷/復帰/Authenticator（3.3〜3.5）。
- **Network E2E**: 2 Client + Dedicated Server + Backend を CI/夜間起動（WSL2 の nightly と連携、10B 3.6）。

### 5.3 受入判定

- Batchmode Import が exit 0 で Prefab を生成（第15章 受入）。EditMode/PlayMode が緑。
- RC の Windows Client と Linux DS がビルドでき、DS が WSL2 で Headless 起動・Readiness/Graceful Shutdown/Snapshot を確認（第19.1）。
- 1080p/60fps・同期・操作応答の実測が記録され、未達は数値付き既知 Issue として残る（第3章/第19.1）。

---

## 6. 落とし穴（Windows側）

- **Import Processor は決定的に**。Prefab を手で編集すると再生成で差分が出る。編集は Manifest（＝WSL2 の generate.py）側に寄せ、Import は自動生成のみ。
- **Server Prefab に描画を混ぜない**。Collider/NavMesh/Interaction metadata 中心の軽量に保つ（第12章）。描画同梱は DS メモリ/tick を無駄に消費。
- **bottom-center pivot と grid のズレ**。glb 生成側（03B 既存：底面原点）と Import の pivot 前提を一致させる。ズレるとモジュール接続が破綻。
- **`UCX_` Collider の取りこぼし**。命名規約に沿った Collider を MeshCollider へ確実に割当。`has_collider` 前提を Import 側でも確認（多重防御）。
- **Authenticator の拒否漏れが最大のセキュリティ穴**。期限/再利用/build/server/無効char/署名の**全て**を拒否する。特に**再利用**は Auth の単回消費（`used_at`）に依存するので DS の事前検証だけで済ませない。
- **Client に権威を持たせない**。Damage/Loot/Drop/Craft Result/Purchase Price を Client 送信値で確定しない（MVP-SEC-006）。復帰時も Full Snapshot は Server から受ける。
- **Client Cache に重要データを置かない**（第5.4/第19.1）。Inventory/所持金/位置/拠点は Server 保存が正。Cache は表示・設定のみ。AT-002 で確認。
- **FishNet 更新は Upgrade Gate**（RISK-05）。package lock を固定し、更新時は Network smoke を必ず通す。
- **`-runTests` に `-quit` を付けない／ビルドの `-executeMethod` には付ける**（03A 8章）。`ImportAssets` は `-quit` を付ける（ビルド系）。
- **`Unity.exe` のパスはパッチ版依存**。`unity_import_assets.ps1` の版指定を実環境に合わせる。
- **`.ps1` は CRLF・`Assets/Generated/` は WSL2 生成**（触らない）。M7 追加の Prefab 出力先を Generated 配下にする場合、proto C# 出力（WSL2 管理）と衝突しないサブフォルダ（`Prefabs/`）に隔離する。

---

## 参考資料

[R1] [Unity 6000.5.x Release Notes](https://unity.com/releases/editor/whats-new/6000.5.3f1)
[R2] [Unity 6.5 Dedicated Server requirements](https://docs.unity3d.com/6000.5/Documentation/Manual/dedicated-server-requirements.html)
[R6] [FishNet: Authenticator](https://fish-networking.gitbook.io/docs/manual/guides/authentication)
[R-DSBUILD] [Unity Manual: Build for Dedicated Server](https://docs.unity3d.com/6000.2/Documentation/Manual/dedicated-server-build.html)
[R-BATCH] [Unity Support: Batchmode build locally](https://support.unity.com/hc/en-us/articles/9466056266004-How-do-I-build-my-Unity-Project-in-Batchmode-Locally)
[R-CLI] [Unity Manual: Command line arguments](https://docs.unity3d.com/Manual/EditorCommandLineArguments.html)
[R-POST] [Unity: AssetPostprocessor](https://docs.unity3d.com/ScriptReference/AssetPostprocessor.html)
[R-LOD] [Unity: LODGroup](https://docs.unity3d.com/ScriptReference/LODGroup.html)
[R-PROF] [Unity: Profiler](https://docs.unity3d.com/Manual/Profiler.html)
