---
title: "Story_0001a 実装指示書（Windows / Unity 側）"
subtitle: "Dev Local Mode: Unity Editor 単体で Auth/Matchmaking/Join Ticket/FishNet 接続/TPS 移動を完結させる"
document_id: "IMPL-STORY-0001A-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（Story_0001a / Dev Local Mode / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / Input System / R3 / VContainer / UniTask"
related_document: "04A_M1実装指示書_Windows側_v0.1.md, 04B_M1実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md"
language: "ja"
---

# Story_0001a 実装指示書：Dev Local Mode（Windows / Unity 側）v0.1

本書は **Story_0001a** として、M1 実装の上に **Unity Editor 単体で動作確認できる Dev Local Mode** を追加するための実装指示である。

目的は、WSL2 側バックエンド（Auth/Matchmaking REST・gRPC・DB）を起動しなくても、Unity Editor の Play だけで次を確認できる状態にすること:

- 疑似ログイン
- 疑似 matchmaking join
- dev join ticket 発行
- FishNet local server/client 起動
- Join Ticket Authenticator の成功/拒否分岐
- `World_MVP` への遷移
- 3D TPS WASD 移動

> ファイル配置ルール: Windows 側が触るのは **`unity/`** 配下と必要最小限の **`scripts/*.ps1`** のみ。`services/`・`proto/`・`infra/` は触らない。`unity/SurvivalWorld/Assets/Generated/` の proto C# 生成物は手編集しない。

---

## 1. 前提とスコープ

### 1.1 前提

Story_0001a は M1 Windows 側実装後の状態を前提とする。

既存または直前セッションで追加済みの想定:

- `SurvivalRuntimeConfig`
- `AuthClient`
- `NetworkSessionClient`
- `JoinTicketAuthenticator`
- `JoinTicketClaimsValidator`
- `JwsEd25519JoinTicketVerifier`
- `JoinTicketBroadcast`
- `ServerBootstrap`
- `ThirdPersonInputReader`
- `NetworkPlayerController`
- `ThirdPersonCameraRig`
- `PlayerControls.inputactions`
- `Bootstrap.unity` / `World_MVP.unity` の M1 用基盤配置

### 1.2 Story_0001a の対象

Story_0001a では、**Editor 専用のローカル代替実装**を追加する。

- REST Auth の代替: `DevLocalAuthClient`
- Matchmaking/gRPC redeem の代替: `DevLocalMatchmakingGateway`
- Join Ticket 署名/検証の dev 実装: `DevLocalJoinTicketIssuer`
- Editor Play 用の自動起動: `DevLocalBootstrapper` または既存 `Bootstrapper` の dev 分岐
- Editor 単体テスト: Dev ticket、単回 redeem、接続フロー、TPS 入力

### 1.3 非対象

- 本番 REST/gRPC 実装の削除・置換
- 本番 Join Ticket 検証の弱体化
- `services/` 側の mock server 実装
- proto 変更
- DB/マイグレーション変更
- 本番 build に dev shortcut を混入させること

---

## 2. 完了条件（DoD）

Story_0001a 完了条件:

- Unity Editor の `Bootstrap` シーンで Play すると、バックエンド未起動でも Dev Local Mode で `World_MVP` まで到達できる。
- Editor Play 中に local FishNet server と local client が同一プロセス内で起動し、dev join ticket 認証を通過する。
- `World_MVP` で `PlayerCharacter` を WASD 操作できる。
- Dev Local Mode は **Editor / Development 用 config で明示的に有効化した時だけ**動く。
- 通常 config では既存の本番向け `AuthClient` / `GeneratedMatchmakingGateway` / JWS 検証経路を使う。
- Join Ticket の拒否分岐（期限切れ、server mismatch、build mismatch、再利用）を EditMode または PlayMode テストで確認する。
- `scripts\unity_test.ps1` が緑。
- `scripts\unity_build_client.ps1` と `scripts\unity_build_server.ps1` が緑。

---

## 3. 設計方針

### 3.1 本番経路と Dev 経路を分離する

Dev Local Mode は本番実装を置き換えない。以下のように interface / config で分岐する。

```text
Bootstrapper
├─ Production Mode
│  ├─ AuthClient
│  ├─ NetworkSessionClient
│  └─ IMatchmakingGateway = GeneratedMatchmakingGateway
└─ Dev Local Mode
   ├─ DevLocalAuthClient または IAuthClient 実装
   ├─ DevLocalJoinTicketIssuer
   ├─ DevLocalMatchmakingGateway
   └─ NetworkSessionClient（既存を流用）
```

推奨:

- `IAuthClient` を追加し、既存 `AuthClient` と `DevLocalAuthClient` を同じ呼び出し形にする。
- `IMatchmakingGateway` は既存の interface を利用する。
- `JoinTicketAuthenticator.Configure(...)` に dev gateway と dev public key を渡せるようにする。

### 3.2 Config で明示的に有効化する

`SurvivalRuntimeConfig` に以下を追加する。

```csharp
[Header("Dev Local Mode")]
[SerializeField] private bool devLocalMode;
[SerializeField] private bool autoStartLocalServerInEditor = true;
[SerializeField] private bool autoConnectLocalClientInEditor = true;
[SerializeField] private string devAccountId = "dev-account-01";
[SerializeField] private string devCharacterId = "dev-character-01";
```

公開 property:

```csharp
public bool DevLocalMode => devLocalMode;
public bool AutoStartLocalServerInEditor => autoStartLocalServerInEditor;
public bool AutoConnectLocalClientInEditor => autoConnectLocalClientInEditor;
public string DevAccountId => devAccountId;
public string DevCharacterId => devCharacterId;
```

制約:

- `devLocalMode == false` のとき dev 実装は一切使わない。
- `UNITY_EDITOR` 条件付きにし、通常 Player build では dev auto-flow が走らないようにする。
- Development Build で使いたい場合は、別途 `DEVELOPMENT_BUILD` と明示 config の両方を要求する。

---

## 4. 実装対象

### 4.1 DevLocalJoinTicketIssuer

配置:

```text
Assets/Scripts/Dev/DevLocalJoinTicketIssuer.cs
```

責務:

- `JoinTicketClaims` を生成する。
- 既存 `JwsEd25519JoinTicketVerifier` が検証できる形式の compact JWS を発行する。
- Editor 起動時に dev Ed25519 key pair を生成するか、固定 dev key を `SurvivalRuntimeConfig` に保持する。

推奨:

- BouncyCastle の Ed25519 を利用する。
- header は `{ "alg": "EdDSA", "typ": "JWT" }`。
- payload は既存 verifier が読む JSON shape に合わせる:
  - `ticket_id`
  - `account_id`
  - `character_id`
  - `server_id`
  - `world_id`
  - `build_id`
  - `issued_at_unix_ms`
  - `expires_at_unix_ms`
  - `nonce`

注意:

- 既存 verifier が `JsonUtility` で読むため、payload は Unity JSON 互換の flat object にする。
- dev key は本番秘密鍵ではない。`DevLocal` namespace / folder に閉じ込める。

### 4.2 DevLocalMatchmakingGateway

配置:

```text
Assets/Scripts/Dev/DevLocalMatchmakingGateway.cs
```

責務:

- `IMatchmakingGateway` を実装する。
- `RegisterServerAsync` / `HeartbeatAsync` / `MarkDrainingAsync` は in-memory state を更新して成功を返す。
- `RedeemJoinTicketAsync` は ticket_id 単位で単回消費を保証する。

仕様:

- `RedeemJoinTicketAsync(serverId, ticket)` は local verifier で claims を取り出し、以下を確認する。
  - signature valid
  - expires not passed
  - server_id matches
  - build_id matches
  - character_id not empty
  - ticket_id 未使用
- 成功時、ticket_id を used set に追加して `Success()`。
- 再利用時、`Failure("join_ticket_reused")`。

注意:

- in-memory state でよい。Editor Play 停止で消えてよい。
- 本番 `GeneratedMatchmakingGateway` の代替として config でだけ差し替える。

### 4.3 DevLocalAuthClient

配置:

```text
Assets/Scripts/Dev/DevLocalAuthClient.cs
```

責務:

- REST を呼ばずに疑似 account/session/matchmaking を返す。
- 既存 `AuthClient` と同じ利用者コードから呼べるように interface を合わせる。

推奨 interface:

```csharp
public interface IAuthClient
{
    ITokenStore TokenStore { get; }
    UniTask<CreateAccountResponse> CreateAccountAsync(string email, string password, string displayName, CancellationToken cancellationToken);
    UniTask<SessionTokenPair> LoginAsync(string email, string password, CancellationToken cancellationToken);
    UniTask<SessionTokenPair> RefreshAsync(CancellationToken cancellationToken);
    UniTask LogoutAsync(CancellationToken cancellationToken);
    UniTask<MatchmakingJoinResponse> JoinMatchmakingAsync(string characterId, string buildId, CancellationToken cancellationToken);
}
```

既存 `AuthClient` は `IAuthClient` を実装する。

`DevLocalAuthClient.JoinMatchmakingAsync` は:

- `server_endpoint = config.ServerEndpoint`
- `join_ticket = DevLocalJoinTicketIssuer.Issue(...)`
- `expires_at = now + ttl`

を返す。

### 4.4 Bootstrapper の Dev Local 分岐

対象:

```text
Assets/Scripts/Bootstrap/Bootstrapper.cs
```

実装:

- `SurvivalRuntimeConfig.DevLocalMode` が true の場合、`DevLocalAuthClient` を使う。
- Editor Play 時、自動で以下を実行する dev helper を追加する。

```csharp
#if UNITY_EDITOR
private async UniTaskVoid StartDevLocalFlowAsync(CancellationToken cancellationToken)
{
    if (!config.DevLocalMode || !config.AutoConnectLocalClientInEditor) return;
    await LoginJoinAndConnectAsync("dev@example.local", "dev-password", cancellationToken);
}
#endif
```

注意:

- `async void` は使わず `UniTaskVoid` + `.Forget()` または `UniTask` を使う。
- Play 停止時に `destroyCancellationToken` でキャンセルされること。
- 自動接続を嫌う場合に備え、`AutoConnectLocalClientInEditor` で OFF にできること。

### 4.5 ServerBootstrap の Dev Local 分岐

対象:

```text
Assets/Scripts/Server/ServerBootstrap.cs
```

現状の課題:

- `matchmakingGateway` が `UnavailableMatchmakingGateway.Instance` 固定だと、Editor 単体で Join Ticket redeem が必ず失敗する。

実装:

- `SurvivalRuntimeConfig.DevLocalMode` が true の場合、`DevLocalMatchmakingGateway` を生成し、`JoinTicketAuthenticator.Configure(...)` に渡す。
- Editor かつ `AutoStartLocalServerInEditor` の場合、batchmode でなくても local server を起動できるようにする。

起動条件:

```csharp
bool shouldStartServer =
    Application.isBatchMode ||
    (Application.isEditor && config.DevLocalMode && config.AutoStartLocalServerInEditor);
```

注意:

- 本番 build で dev local server が勝手に起動しないこと。
- `MarkDrainingAsync` は Play 停止時に呼ばれてもよいが、dev gateway では no-op 成功でよい。

### 4.6 NetworkSessionClient の Editor local 接続

対象:

```text
Assets/Scripts/Net/NetworkSessionClient.cs
```

確認/修正:

- `server_endpoint` が `127.0.0.1:7770` の時に Tugboat client が正しく接続すること。
- `OnClientConnectionState.Started` で `JoinTicketBroadcast` が送信されること。
- `OnAuthenticated` 後に `World_MVP` をロードすること。

必要なら追加:

- 接続タイムアウト。
- 失敗時に handler を必ず解除。
- `authenticatedCompletion` の二重完了防止。

### 4.7 Player spawn / local playable state

現状が `World_MVP` に `PlayerCharacter_Prototype` を直接置いているだけの場合、FishNet spawn/ownership が成立しない可能性がある。

Story_0001a では最低限どちらかを実装する。

推奨 A: FishNet spawn を実装する

- `PlayerCharacter.prefab` を NetworkManager の spawnable prefab に登録する。
- 認証成功後、server が接続 client に対して `PlayerCharacter` を spawn し owner を付与する。
- `NetworkPlayerController.Update()` は `NetworkObject.IsSpawned && IsOwner` の時だけ入力を送る。

代替 B: Editor dev only の local playable fallback

- Dev Local Mode かつ FishNet spawn が未整備の場合だけ、`World_MVP` の prototype を local playable として動かす。
- この fallback はテスト用に限定し、Server authority の本線とは混ぜない。

優先は A。M1 の「FishNet 接続/TPS 移動」の確認になるため。

---

## 5. UI / 操作

Story_0001a は UI を作り込みすぎない。

最低限:

- `Bootstrap` Play で自動接続できる。
- Dev Local Mode ON/OFF が `SurvivalRuntimeConfig` で見える。
- 接続失敗時は Console に理由が出る。

任意:

- `DevLocalHud` を追加し、画面左上に mode / server / auth / scene / player spawned を小さく表示する。
- 表示は Editor / Development 限定にする。

---

## 6. テスト指示

### 6.1 EditMode

追加テスト候補:

```text
Assets/Tests/EditMode/M1aDevLocalTicketTests.cs
Assets/Tests/EditMode/M1aDevLocalGatewayTests.cs
Assets/Tests/EditMode/M1aDevLocalConfigTests.cs
```

確認項目:

- `DevLocalJoinTicketIssuer` が `JwsEd25519JoinTicketVerifier` で検証できる ticket を発行する。
- 期限切れ ticket は `Expired`。
- `server_id` mismatch は `ServerMismatch`。
- `build_id` mismatch は `BuildMismatch`。
- `DevLocalMatchmakingGateway.RedeemJoinTicketAsync` は同一 ticket の 2 回目を拒否する。
- `SurvivalRuntimeConfig.DevLocalMode == false` では production path が選ばれる。

### 6.2 PlayMode

追加テスト候補:

```text
Assets/Tests/PlayMode/M1aDevLocalBootstrapPlayModeTests.cs
Assets/Tests/PlayMode/M1aDevLocalMovementPlayModeTests.cs
```

確認項目:

- Dev Local Mode で local server 起動要求が走る。
- Dev Local Mode で `JoinMatchmakingAsync` が local endpoint と ticket を返す。
- `World_MVP` ロード後、player が存在する。
- owner player の入力 command が生成される。

可能なら PlayMode で FishNet host/client の最小接続まで見る。

### 6.3 手動受入

手順:

1. Unity Editor で `Assets/Scenes/Bootstrap.unity` を開く。
2. `SurvivalRuntimeConfig.DevLocalMode` を true にする。
3. `AutoStartLocalServerInEditor` と `AutoConnectLocalClientInEditor` を true にする。
4. Play を押す。
5. `World_MVP` に遷移する。
6. WASD で `PlayerCharacter` が移動する。
7. Console に join ticket accepted / authenticated 相当のログが出る。
8. Play 停止後、再度 Play して ticket used set がリセットされる。

---

## 7. 実装順序

| # | タスク | 完了確認 |
|---|---|---|
| 1 | `SurvivalRuntimeConfig` に Dev Local Mode 設定を追加 | Inspector で ON/OFF 可能 |
| 2 | `IAuthClient` を追加し `AuthClient` を対応 | 既存 production 経路がコンパイル維持 |
| 3 | `DevLocalJoinTicketIssuer` を実装 | 既存 verifier で署名検証成功 |
| 4 | `DevLocalMatchmakingGateway` を実装 | redeem 1回目成功、2回目拒否 |
| 5 | `DevLocalAuthClient` を実装 | local endpoint + ticket を返す |
| 6 | `Bootstrapper` を Dev Local Mode 対応 | Editor Play で自動 join/connect |
| 7 | `ServerBootstrap` を Dev Local Mode 対応 | Editor Play で local server 起動 |
| 8 | FishNet spawn/ownership を整備 | owner player が WASD 操作可能 |
| 9 | EditMode/PlayMode テスト追加 | `unity_test.ps1` 緑 |
| 10 | Client/Server build 検証 | 両 build script 緑 |

---

## 8. 落とし穴

- Dev Local Mode を本番経路に混ぜない。`DevLocal` folder / namespace / config で境界を明確にする。
- `devLocalMode` が false の時、REST/gRPC 本番経路の挙動を変えない。
- 本番 secret を dev key として使わない。
- `JoinTicketAuthenticator` の署名検証をスキップしない。Dev ticket も既存 verifier を通す。
- redeem 単回性を省かない。Editor でも reused ticket を拒否する。
- `async void` を使わない。`UniTask` と `destroyCancellationToken` を使う。
- FishNet 未 spawn の `NetworkBehaviour` で `IsOwner` / `IsServerStarted` を読まない。`NetworkObject == null` ガードを先に置く。
- `World_MVP` に置いた prototype を本番 spawn と混同しない。最終的には server spawn + owner 付与に寄せる。
- `Assets/Generated/` を手編集しない。

---

## 9. 次セッション開始時の確認コマンド

```powershell
git status --short
.\scripts\unity_test.ps1
.\scripts\unity_build_client.ps1
.\scripts\unity_build_server.ps1
```

最初に確認するファイル:

```text
unity/SurvivalWorld/Assets/Scripts/Bootstrap/Bootstrapper.cs
unity/SurvivalWorld/Assets/Scripts/Server/ServerBootstrap.cs
unity/SurvivalWorld/Assets/Scripts/Net/JoinTicketAuthenticator.cs
unity/SurvivalWorld/Assets/Scripts/Net/JwsEd25519JoinTicketVerifier.cs
unity/SurvivalWorld/Assets/Scripts/Net/NetworkSessionClient.cs
unity/SurvivalWorld/Assets/Scripts/Player/NetworkPlayerController.cs
unity/SurvivalWorld/Assets/Settings/SurvivalRuntimeConfig.asset
unity/SurvivalWorld/Assets/Scenes/Bootstrap.unity
unity/SurvivalWorld/Assets/Scenes/World_MVP.unity
```

---

## 10. 受入メモ

Story_0001a は「本番バックエンド未接続でも Editor で遊べる」ための開発支援ストーリーであり、本番 Auth/Matchmaking の代替ではない。

受入時は次を明確に分けて判断する。

- Editor 単体の開発体験: Story_0001a の対象
- 実バックエンド連携: Story_0001 / 04A + 04B の対象
- 2 Client 別プロセス同期: M1 の最終 E2E 対象

