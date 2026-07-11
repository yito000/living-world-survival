---
title: "M1 実装指示書（Windows ネイティブ側）"
subtitle: "FishNet 接続・Join Ticket Authenticator・3D TPS 移動・REST クライアント"
document_id: "IMPL-M1-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M1 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / Input System / R3 / VContainer / UniTask"
related_document: "04B_M1実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M1 実装指示書：Windows ネイティブ側 v0.1

本書は **M1（接続）** の作業を **Windows ネイティブ側**（Unity Client と Unity Dedicated Server：FishNet 接続、Join Ticket Authenticator、3D 三人称移動、REST クライアント、EditMode/PlayMode テスト、PowerShell スクリプト）に限定して指示する。バックエンド（Auth/Matchmaking REST・gRPC・DB）は別冊 **04B（WSL2側）** を参照。第0章「分担と連携」は両冊で共通・同一内容（詳細は 03A/03B 第0章と共通）。

M1 のマイルストーン成果（MVP第19章）: **Auth、Matchmaking、Join Ticket、FishNet接続、2 Client移動（3D TPS/WASD）**。本書はそのうち **Unity Client / Dedicated Server** を担当する。REST/gRPC/DB/署名は 04B を参照。

> ファイル配置ルール（厳守）: Windows 側が触るのは **`unity/`** 配下と **`scripts/*.ps1`** のみ。`services/`・`proto/`・`infra/` は触らない（04B 担当）。proto の C# 生成物（`unity/SurvivalWorld/Assets/Generated/`）は **WSL2 が `buf generate` で生成**し、Windows は消費（コンパイル）するだけ（03A 6章）。

---

## 0. 分担と連携（共通・両冊同一／詳細は 03A/03B 第0章と共通）

### 0.1 環境別 責務分担（M1 要点）

| 領域 | 担当環境 | M1 での主なタスク |
|---|---|---|
| FishNet NetworkManager/Transport | **Windows** | Client/Server 共通の接続基盤（本書3章） |
| Join Ticket Authenticator（DS 側） | **Windows** | 接続時に Ticket 提示 → 公開鍵で事前検証、無効を拒否 |
| 3D 三人称コントローラ（Input System） | **Windows** | WASD/Look/Jump/Sprint → `InputCommand`（tick/sequence） |
| Bootstrap→World_MVP シーン遷移 | **Windows** | Auth UI → 接続 → ゲーム画面 |
| REST クライアント（`AuthClient`） | **Windows** | アカウント作成/ログイン/matchmaking join |
| Auth/Matchmaking REST・gRPC・DB・署名 | **WSL2** | `/v1/...`、`MatchmakingService`、JWT、Join Ticket 署名（04B） |
| proto → C# 生成 | **WSL2**（生成） | `InputCommand`/`JoinTicketClaims` 等の C# を Unity へ出力 |

> 分担の全体像（リポジトリ配置 0.2 / Git・LFS・改行 0.3 / 境界成果物 0.4 / 連携フロー 0.5）は 03A 第0章と同一。

### 0.2 M1 の境界成果物（環境をまたぐ主要な取り決め）

| 成果物 | 生成/正 | 消費側 | 合意事項 |
|---|---|---|---|
| Join Ticket（署名付きトークン） | WSL2（Auth 秘密鍵で署名, 04B 3.4） | **Windows（DS が公開鍵で事前検証）** | **署名は非対称鍵（Ed25519 推奨）**。DS には**公開鍵のみ**配布。claims は `survival.v1.JoinTicketClaims`。 |
| REST（`/v1/accounts` 等） | WSL2（実装, 04B 3.1〜3.2） | **Windows（`AuthClient`）** | パス・JSON 形状を 04B と一致させる（本書 3.5）。 |
| `MatchmakingService` gRPC | WSL2（実装, 04B 3.5） | **Windows（DS が gRPC クライアント）** | DS は `RegisterServer`/`Heartbeat`/`RedeemJoinTicket`/`MarkDraining` を呼ぶ。 |
| `InputCommand`（proto） | 両者（proto が正） | 両者 | `tick, sequence, move(Vec3), look(Vec3), jump, sprint`（`gameplay.proto` 既定）。 |
| build_id | 両者合意 | 両者 | Client の join・DS の `RegisterServer`・Ticket claims で同一値（不一致は拒否）。 |

---

## 1. 対象と前提（Windows側）＋ M1 の DoD

- OS: Windows。Unity Editor はネイティブ動作（03A 1章）。
- 既存状態: M0 完了で基盤ライブラリ（R3/VContainer/UniTask/FishNet/Input System）導入済み、Client/Server ビルドと EditMode/PlayMode テストが動く（03A DoD）。プロジェクトルートは `unity/SurvivalWorld/`。
- 本書の完了で、**2 つの Unity Client が別アカウントでログイン → matchmaking join → 同一 Dedicated Server へ FishNet 接続し、互いの 3D 三人称移動（WASD）が同期表示**される状態にする。

### 1.1 Windows側 M1 DoD

- **REST クライアント**が `POST /v1/accounts` / `POST /v1/sessions`（+refresh/logout）/ `POST /v1/matchmaking/join` を呼び、`server_endpoint` と `join_ticket` を取得できる。
- **FishNet NetworkManager/Transport** が Client/Server で構成され、DS が Linux Headless で起動して接続を受け付ける。
- **Join Ticket Authenticator（DS 側）**: 接続時に Client が `join_ticket` を提示 → DS が**公開鍵で JWS 事前検証** → Auth の `RedeemJoinTicket`（gRPC）で単回消費 → 成立。**期限切れ / 再利用 / build 不一致 / server 不一致 / 無効 Character を拒否**（切断）。
- **3D 三人称コントローラ**: `Move/Look/Jump/Sprint` を Input System で受け、`InputCommand`（`tick`/`sequence` 採番）に変換して DS へ送信。DS が Transform を確定、Client は所有キャラを予測・他キャラを補間。
- **シーン遷移**: `Bootstrap`（Auth UI）→ join 成功 → `World_MVP`（接続・スポーン）。
- `scripts\unity_test.ps1` で EditMode/PlayMode テストが緑（下記 5 章）。
- Client（Windows）と Dedicated Server（Linux）のビルドが M0 スクリプトで生成できる。

---

## 2. 前提成果物（M0 で構築済み・本書の起点）

- Unity プロジェクト（`unity/SurvivalWorld/`）: URP + R3/VContainer/UniTask/**FishNet**/Input System 導入済み、版固定（`packages-lock.json`）済み（03A 4章）。
- シーン雛形 `Assets/Scenes/Bootstrap.unity` / `Assets/Scenes/World_MVP.unity`（03A の `BuildScript` が参照）。
- `Assets/Editor/BuildScript.cs`（`BuildWindowsClient` / `BuildLinuxServer`）、`scripts\unity_build_client.ps1` / `unity_build_server.ps1` / `unity_test.ps1`（03A 5章）。
- **proto C# 生成物** `unity/SurvivalWorld/Assets/Generated/`（WSL2 が生成）。M1 で使う型は既に定義済み:
  - `survival.v1.InputCommand`（`tick, sequence, move, look, jump, sprint`）、`Vec3`（`gameplay.proto`）。
  - `survival.v1.JoinTicketClaims`（`ticket_id, account_id, character_id, server_id, world_id, build_id, issued_at_unix_ms, expires_at_unix_ms, nonce`）、`MatchmakingService` C# クライアント（`auth.proto`）。
- Linux Dedicated Server クロスビルド（`-standaloneBuildSubtarget Server`）が可能なモジュール導入済み（03A 2.1）。

> proto を変更したい場合も Windows では編集しない。必要なら 04B（WSL2）へ依頼し `make proto` で再生成する（03A/03B 0.5）。

---

## 3. 実装対象（Unity Client / Dedicated Server）

配置（提案, すべて `unity/SurvivalWorld/Assets/` 配下）:

```text
Assets/
├─ Scripts/
│  ├─ Bootstrap/        # Bootstrapper, LifetimeScope（VContainer）, シーン遷移
│  ├─ Net/              # NetworkSessionClient, JoinTicketAuthenticator(DS), Transport 設定
│  ├─ Auth/             # AuthClient（REST）, トークン保持
│  ├─ Player/           # ThirdPersonInputReader, NetworkPlayerController, ThirdPersonCameraRig
│  └─ Server/           # ServerBootstrap（DS 起動, RegisterServer/Heartbeat）
├─ Scenes/              # Bootstrap.unity / World_MVP.unity（既存）
├─ Settings/            # InputActions（.inputactions）
└─ Generated/           # proto C#（WSL2 生成・消費のみ）
```

### 3.1 FishNet ネットワーク基盤（NetworkManager / Transport）

- `Bootstrap` シーンに **NetworkManager** を配置（Client/Server 共通、DS ビルドでも同一シーン起点）。Transport はデフォルト（Tugboat/UTP 等、導入済み版に従う）。
- **サーバー実行モデル**（BSD第5.2 / MVP第6章）: 固定 Tick（暫定 20Hz）でプレイヤー入力を処理。DS ビルドでレンダリング/音声/UI/カメラを除外・無効化。
- **NetworkObject と権限**（MVP第6.3）: `PlayerCharacter` は **Client owned / Server authority**（Owner が Input 送信、Transform/Stat 確定は Server）。M1 では PlayerCharacter のみ対象（AI/Animal 等は M3 以降）。
- **`NetworkSessionClient`**（MVP 5.3）: FishNet 接続、Join Ticket 送信、切断・再接続を **UniTask + CancellationToken** で実装（`async void` 禁止, MVP 5.5.2）。

### 3.2 Join Ticket Authenticator（DS 側）（MVP 6.2 / 11.3, BSD第11章）

FishNet の `Authenticator` を継承した **`JoinTicketAuthenticator`** を Server 側に実装する（`ServerBootstrap` が NetworkManager に登録）。フロー:

1. **接続時**、Client（`NetworkSessionClient`）が `join_ticket`（署名付きトークン文字列）を Authenticator ブロードキャストで DS へ提示。
2. DS が **公開鍵で JWS を事前検証**（04B が配布する `JOIN_TICKET_PUBLIC_KEY`。Ed25519/EdDSA）。claims = `JoinTicketClaims`。
3. DS が claims をローカル検証: **`expires_at_unix_ms` 期限切れ / `server_id` 不一致（自 DS の server_id）/ `build_id` 不一致（自ビルドの build_id）/ `character_id` 無効** を拒否。
4. DS が Auth の **gRPC `RedeemJoinTicket(server_id, ticket)`** を呼び **単回消費**（04B 3.4/3.5）。`ok=false`（再利用・期限・不一致）なら拒否。
5. すべて通れば認証成立 → `World_MVP` の Player をスポーン。失敗は接続を **拒否（切断）**。

- 拒否理由の分類（**期限切れ / 再利用 / build 不一致 / server 不一致 / 無効 Character / 署名不正**）をログに残す（相関 ID, BSD第13章）。**署名検証（DS ローカル）と単回消費（Auth）の二段構え**で、Auth 到達不能でも署名段で明確な不正は弾く。
- 公開鍵事前検証と Auth 単回消費は二重防御（MVP 11.3）。Player→AI 等の非干渉は M3 以降だが、Authenticator の Target/権限方針は今から Server authority を徹底。

### 3.3 3D 三人称コントローラ（Input System → InputCommand）

**Input Action Map**（MVP 5.2）を `.inputactions` で定義（M0 で受け皿があれば拡張）。M1 で最低限使うのは Move/Look/Jump/Sprint:

| Action | Default Binding | 処理（M1） |
|---|---|---|
| Move | WASD | `Vector2`。カメラ Yaw 基準で移動。 |
| Look | Mouse Delta | Yaw/Pitch。Cursor Lock 時のみ。 |
| Jump | Space | 接地中かつ Server 許可。 |
| Sprint | Left Shift | 速度のみ設定（Stamina 消費は MVP 省略可）。 |
| Interact / PrimaryAction / Inventory / Cancel | E / Left Mouse / Tab,I / Esc | **M1 では未使用でよい**（M2/M3 で実装。Action は定義しておく）。 |

- **`ThirdPersonInputReader`**（MVP 5.3）: Input System Action を **R3 で束ねて** `InputCommand` へ変換（MVP 5.5.2）。フィールドは `gameplay.proto` の `InputCommand` に一致させる:
  - `tick`（クライアント Tick）、`sequence`（**送信ごとに単調増加**。Reconciliation/重複排除用）、`move`（`Vec3`）、`look`（`Vec3`）、`jump`（bool）、`sprint`（bool）。
- **`NetworkPlayerController`**（MVP 5.3）: 所有プレイヤーの **予測（Client Prediction）**、Command 送信、**Reconciliation**。他プレイヤーは **Interpolation**（BSD第5.1）。M1 は移動同期が確認できれば可（詳細な予測実装は段階導入でよいが、権威は必ず Server）。
- **`ThirdPersonCameraRig`**（MVP 5.3）: Client 専用カメラ、Pitch/Yaw、障害物回避。DS ビルドでは無効。
- **権威の徹底**（MVP 5.1/6.3）: Client は入力（`InputCommand`）だけ送る。座標確定は Server。**クライアント側で最終 Transform を確定しない**（DoD: Server 権威を迂回しない, MVP 19.1）。

### 3.4 シーン遷移（Bootstrap → World_MVP）

- **`Bootstrapper`**（MVP 5.3）: VContainer `LifetimeScope` で `AuthClient`/`NetworkSessionClient` 等をコンストラクタ注入（MVP 5.5.2）。`Bootstrap` シーンで Auth UI を表示。
- フロー: Auth UI（アカウント作成/ログイン）→ `matchmaking/join` 成功 → `server_endpoint` へ FishNet 接続開始 → 接続成立で **`World_MVP`** へ遷移し Player スポーン。
- 接続失敗/切断時はエラー表示し `Bootstrap` へ戻す（UniTask のキャンセルで確実に破棄, MVP 5.5.2）。

### 3.5 REST クライアント（`AuthClient`）（MVP 5.3 / 11.1）

`AuthClient`（MVP 5.3）で 04B の REST を呼ぶ（HTTP/JSON、UniTask で非同期・リトライ/タイムアウト）:

| 呼び出し | Method Path | 送信 | 受領 |
|---|---|---|---|
| アカウント作成 | `POST /v1/accounts` | `email, password, display_name` | `account_id` |
| ログイン | `POST /v1/sessions` | `email, password` | `access_token, refresh_token, expires_in` |
| トークン更新 | `POST /v1/sessions/refresh` | `refresh_token` | 新 Token pair |
| ログアウト | `DELETE /v1/sessions/current` | `Authorization: Bearer <access_token>` | 204 |
| マッチング | `POST /v1/matchmaking/join` | `character_id, build_id`（Bearer） | `server_endpoint, join_ticket, expires_at` |

- **トークン保持**（MVP 5.3/5.4）: access/refresh は **OS Credential Store 相当を優先**し平文ファイル保存を禁止。**重要ゲームデータ（所持金/Inventory 等）はローカルに正本保存しない**（M1 では取得もしない）。
- `build_id` は DS の `RegisterServer` / Ticket claims と**同一値**（0.2 の合意）。ビルド時に埋め込むか config 化。

### 3.6 Dedicated Server 起動（`ServerBootstrap`）（MVP 6.2, 12.1 の一部）

- **`ServerBootstrap`**（MVP 6.2）: Config、FishNet 起動、Readiness を担う。M1 では:
  - 起動時に Auth gRPC **`RegisterServer(server_id, world_id, build_id, endpoint, capacity)`** を呼び自身を登録。
  - 定期的に **`Heartbeat(server_id, players, ready, tick_ms)`**（例 5 秒間隔）で LastSeen 更新・ready 通知（BSD第13章 Health）。
  - 終了時に **`MarkDraining(server_id)`** で matchmaking 対象外化（Graceful Shutdown, BSD第5.2）。
- World Load/Save（`WorldData.LoadBootstrap` 等）は **M2** の範囲。M1 は固定/空 World で 2 Client の移動同期を成立させれば良い。

---

## 4. 実装順序表（Windows側 M1）

| # | タスク | 完了確認 |
|---|---|---|
| W-1 | `Generated/` の M1 型（`InputCommand`/`JoinTicketClaims`/`MatchmakingService`）を確認・コンパイル | 参照エラーなし（無ければ 04B に生成依頼） |
| W-2 | `AuthClient`（REST, 3.5）実装 + Bootstrap の Auth UI | アカウント作成→ログイン→join が成功し `join_ticket` 取得 |
| W-3 | FishNet NetworkManager/Transport 構成（3.1）+ `NetworkSessionClient` | ローカルで Client↔Server 接続確立 |
| W-4 | `ServerBootstrap`（3.6）: `RegisterServer`/`Heartbeat`/`MarkDraining` gRPC | 04B の `game_servers` に ready 登録される |
| W-5 | `JoinTicketAuthenticator`（DS, 3.2）: 公開鍵事前検証 + `RedeemJoinTicket` 単回消費 | 正常 Ticket で接続成立 |
| W-6 | 拒否分岐（期限切れ/再利用/build不一致/server不一致/無効Character, 3.2） | 各不正で接続が切断される |
| W-7 | Input Action Map（Move/Look/Jump/Sprint, 3.3）+ `ThirdPersonInputReader` → `InputCommand` | tick/sequence 採番で Command が送信される |
| W-8 | `NetworkPlayerController`（Server authority 移動）+ `ThirdPersonCameraRig` | 所有キャラが WASD で移動、Server が Transform 確定 |
| W-9 | シーン遷移 Bootstrap→World_MVP（3.4）+ Player スポーン | join 成功で World_MVP に入りスポーン |
| W-10 | **2 Client を同一 DS へローカル接続**し移動同期 | 互いの移動が表示され、所有外 Character を操作できない（AT-001） |
| W-11 | EditMode/PlayMode テスト（5章）+ `scripts\unity_test.ps1` | テスト緑 |
| W-12 | `unity_build_client.ps1` / `unity_build_server.ps1` でビルド | Client(Win)/Server(Linux) 成果物生成 |

---

## 5. テスト・受入（Windows側）

MVP第18章の自動テスト区分（Unity EditMode / PlayMode / Network E2E）に対応。M1 の主受入は **AT-001（2 Client が別 Account でログイン・同一 World 接続、互いの移動が表示され所有外 Character を操作できない）**。

### 5.1 自動テスト区分（本書の担当）

| 区分 | 対象（M1・Windows） |
|---|---|
| Unity EditMode | `JoinTicketClaims` パース/検証ロジック（期限・server・build 判定）、`InputCommand` 変換（Input→`tick`/`sequence`）、`AuthClient` の JSON シリアライズ。 |
| Unity PlayMode | Character movement（WASD→Server 権威 Transform）、接続成立/切断、Authenticator 成功/拒否分岐。 |
| Network E2E | 2 Client + Dedicated Server + Backend（WSL2/Docker）を起動し AT-001 を確認（CI/夜間相当。M1 はローカル手動で可）。 |

### 5.2 受入シナリオ

- **AT-001**: 別アカウントの 2 Client がログイン→join→同一 DS 接続。**互いの移動が見える**／**所有外 Character を操作できない**（Owner 制約）。
- **Ticket 拒否**: 期限切れ／使用済み（再利用）／`build_id` 不一致／`server_id` 不一致／署名改竄 の各ケースで接続が拒否される（3.2）。
- **権威**: Client 側で座標を直接書いても Server が上書き・棄却する（Server authority, MVP 19.1）。

> Network E2E は 04B の `make up`/`make migrate`/`make smoke`（Auth/gRPC）と DS の Linux ビルドを WSL2/Docker で実行して構成する（03A/03B 0.5「サーバー起動確認」）。

---

## 6. 落とし穴（Windows側）

- **公開鍵検証を省いて Auth 単回消費だけ**に頼ると、Auth 到達不能時に不正 Ticket を弾けない。**DS ローカルの公開鍵事前検証（Ed25519）＋ Auth 単回消費**の二段を必ず両方実装（3.2 / MVP 11.3）。
- **秘密鍵を DS/Client に配布しない**。DS に渡すのは**公開鍵のみ**（対称鍵にしない, 04B 6章と対）。
- **build_id の不一致**で全接続が拒否される。Client join・DS `RegisterServer`・Ticket claims で同一値（0.2）。ビルドに埋め込む値の管理を最初に決める。
- **`InputCommand.sequence` を単調増加させない**と Reconciliation/重複排除が壊れる。送信ごとに +1、切断で採番をリセットする方針を明確化（3.3）。
- **クライアントで最終 Transform を確定**すると Server 権威を迂回し DoD 違反（MVP 19.1）。Client は `InputCommand` のみ送る。
- **DS ビルドにカメラ/UI/R3 のホットループ購読を残す**とサーバー負荷/リーク（MVP 5.5.3）。DS ではカメラ/音声/UI を無効化、毎 Tick 更新はプレーン C#。
- **`-runTests` に `-quit` を付けない**／ビルドの `-executeMethod` には付ける（03A 5章）。
- **proto 生成 C# を Windows で手編集しない**。変更は 04B に依頼し `make proto` で再生成（03A 6章）。生成物のコミット漏れは WSL2 CI が検出。
- **`async void` を使わない**。FishNet 接続/再接続は UniTask + CancellationToken、切断・シーン破棄で確実にキャンセル（MVP 5.5.2）。
- Refresh Token を平文ファイルに保存しない（OS Credential Store 相当を優先, MVP 5.4）。

---

## 参考資料

[R2] [Unity Manual: Dedicated Server（build target/最適化）](https://docs.unity3d.com/Manual/dedicated-server.html)
[R3] [Unity Input System](https://docs.unity3d.com/Packages/com.unity.inputsystem@latest)
[R6] [FishNet 認証（Authenticator）](https://fish-networking.gitbook.io/docs/manual/guides/authentication)
[R-FN] [FishNet: NetworkManager / Transport / Prediction](https://fish-networking.gitbook.io/docs)
[R-ED] [Ed25519 署名（EdDSA / RFC 8037）](https://www.rfc-editor.org/rfc/rfc8037)
[R-UT] [UniTask](https://github.com/Cysharp/UniTask)
[R-R3] [Cysharp/R3](https://github.com/Cysharp/R3)
[R-VC] [VContainer](https://github.com/hadashiA/VContainer)
