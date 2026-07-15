---
title: "M1 完了レポート（WSL2 / Linux 側）"
subtitle: "Auth/Matchmaking 本実装・実バックエンド↔Unity DS↔クライアント E2E 疎通確認"
document_id: "REPORT-M1-WSL-001"
document_type: "completion_report"
version: "0.1"
issued_at: "2026-07-15"
status: "完了（Story_0001 / M1 / WSL2側）"
related_document: "04B_M1実装指示書_WSL2側_v0.1.md, 04A_M1実装指示書_Windows側_v0.1.md, 04A-1_Story_0001a_DevLocalMode実装指示書_Windows側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md"
language: "ja"
---

# M1 完了レポート：WSL2 / Linux 側 v0.1

本レポートは **Story_0001（M1・接続）** の WSL2 側（Auth/Matchmaking バックエンド）実装が
指示書 `04B_M1実装指示書_WSL2側_v0.1.md` の DoD を満たすことを、2026-07-15 に実測検証した結果と、
実 Unity Dedicated Server（DS）・クライアントとの E2E 疎通確認の記録をまとめたものである。

**結論: 04B（M1 WSL2側）DoD 全項目を達成。実 DS + 実クライアントの E2E 接続まで確認し、Story_0001 を完了とする。**

---

## 1. 検証環境

| 要素 | 値 |
|---|---|
| Auth REST | `:8081`（コンテナ Healthy） |
| Auth Internal gRPC（MatchmakingService） | `:9091` |
| PostgreSQL | `:5432`（healthy） |
| NATS JetStream | `:4222` |
| DB マイグレーション | `0001_init` + `0002_auth_m1` 適用済み（`make migrate`） |
| Auth コンテナ | `docker compose build auth` → `up -d --wait auth`（`authd: REST listening :8081` / `gRPC listening :9091`） |

---

## 2. DoD 検証結果（04B §1.1）

現行コードに対する実測。記録依存ではなく本セッションで再検証した。

| # | DoD 項目 | 判定 | 根拠 |
|---|---|---|---|
| 1 | REST 5本動作（accounts / sessions / sessions/refresh / DELETE sessions/current / matchmaking/join） | ✅ | `internal/rest/rest.go` に5本結線。統合テスト緑＋実機スモークで account→login→join 成功 |
| 2 | Internal gRPC 4本（Redeem/Register/Heartbeat/MarkDraining） | ✅ | `internal/grpc/server.go`。実 DS が RegisterServer/Heartbeat で登録成功 |
| 3 | パスワード Argon2id（OWASP 下限以上） | ✅ | `internal/password/password.go`（m≥19MiB, t≥2, p≥1、PHC 形式） |
| 4 | Access=短寿命JWT / Refresh=ローテ+再利用検知（DBハッシュ保存） | ✅ | `internal/token`。`TestRefreshReuseDetectionRevokesFamily` / `RevokeFamily`（family 単位失効） |
| 5 | Join Ticket=Auth秘密鍵署名（Ed25519）+単回使用+不正拒否 | ✅ | `internal/store/store.go` `UPDATE join_tickets SET used_at=now() WHERE ticket_id=$ AND used_at IS NULL AND expires_at>now()`。実機で redeem #2=`already_used` 拒否 |
| 6 | Auth 所有5テーブルの migration 0002 が `make migrate` で適用 | ✅ | `migrations/0002_auth_m1.up.sql`/`.down.sql` 適用済み |
| 7 | `go test ./...`（auth）緑 | ✅ | 全パッケージ ok。`internal/integration` 0.279s（DB 到達で実走、スキップでない） |
| 8 | `make ci`（proto/lint/test）緑・生成物ドリフト無し | ✅ | `ci_proto.sh: OK`（buf lint/generate/drift/breaking 全通過）、`golangci-lint` **0 issues**（gen/go・auth・api）、生成物差分なし |
| 9 | `make smoke` 拡張（REST/gRPC E2E 疎通） | ✅ | `cmd/mm-smoke` 緑（下記 3.1） |

### 受入シナリオ（04B §5.2）
- **単回使用**: 同一 ticket の RedeemJoinTicket 2回目が拒否（`already_used`）✅
- **Refresh 再利用検知**: 失効済み refresh の再提示で family 全体失効・401（単体テスト）✅
- **期限切れ / build・server 不一致 拒否**: 統合テストで網羅・パス ✅
- **ready な game_server 無し時の join エラー**: 実装・テストで確認 ✅

---

## 3. 実測ログ（抜粋）

### 3.1 バックエンド E2E スモーク（`services/auth` `go run ./cmd/mm-smoke`）
```
mm-smoke: account created (smoke-...@example.com)
mm-smoke: logged in
mm-smoke: server registered + ready (...)
mm-smoke: joined, endpoint=127.0.0.1:7777 expires_at=...
mm-smoke: redeem #1 ok, #2 rejected (already_used) — single-use OK
mm-smoke: E2E OK
```

### 3.2 `go test ./...`（auth, DB あり）
```
ok  services/auth/cmd/authd            (cached)
ok  services/auth/internal/integration 0.279s
ok  services/auth/internal/password    (cached)
ok  services/auth/internal/ticket      0.007s
ok  services/auth/internal/token       (cached)
```

### 3.3 実 Unity DS の gRPC 登録（`game_servers`）
```
server_id | 00000000-0000-0000-0000-000000000101
world_id  | 00000000-0000-0000-0000-000000000201   ← gRPC 側で world-mvp を UUID にマップ
build_id  | dev-local
endpoint  | 127.0.0.1:7770
ready     | t
```
FishNet Tugboat が **UDP `0.0.0.0:7770`** で listen、Heartbeat 正常（失敗0）。
その後 Unity クライアントからの接続・認証成立を確認（AT-001 相当の実 E2E）。

---

## 4. 疎通確認中に発見した課題と対処（Windows/Unity ビルド側）

WSL2 側実装は要件充足済みだったが、実 DS を起動して疎通させる過程で **Windows 側ビルド成果物**に
2件の問題を検出。いずれも Windows 側の再ビルドで解消済み。記録として残す。

| # | 問題 | 症状 | 恒久対処（実施済み） |
|---|---|---|---|
| 1 | サーバービルドに `Bootstrap.unity` 未収録（`BuildScript.cs` の `ServerScenes` が `World_MVP.unity` のみ）。`NetworkManager`/`ServerBootstrap`/`JoinTicketAuthenticator` は Bootstrap 側にのみ存在 | DS が起動コンポーネント不在の World_MVP へ直行 → RegisterServer も FishNet listen(7770) も走らず、`game_servers` 未登録・99% CPU ループ | `ServerScenes` に `Bootstrap.unity` を先頭追加して再ビルド |
| 2 | Grpc.Core ネイティブ lib 名不一致。`Grpc.Core.dll`(2.46.6) の DllImport 名は `grpc_csharp_ext`（Mono/Linux は `libgrpc_csharp_ext.so` を探す）が、ビルド同梱は `Plugins/x86_64/libgrpc_csharp_ext.x64.so`（`.x64` 付き）のみ | native ロード失敗 → `Channel` 生成が `TargetInvocationException` → gateway 未構成 → RegisterServer/Heartbeat 全失敗 | サーバービルドの `Plugins/x86_64/` に `.x64` を外した `libgrpc_csharp_ext.so` を同梱して再ビルド |

補足（Windows 側への改善提案）:
- `ServerBootstrap.CreateAuthGrpcChannel` の catch が `ex.Message` のみを出力し内側例外を隠すため、
  デバッグ時は `ex.InnerException`（または `ex.ToString()`）を出すと native ロード失敗の原因が即判明する。

---

## 5. 実務メモ（次回以降の疎通運用）

- **FishNet Tugboat は UDP**。listen 確認は `ss -lun`（`ss -ltn`=TCP では出ない）。
- Linux DS を `/mnt/d`（Windows FS）で直接実行すると Unity 初期化が遅い。ローカル ext4 に `cp -a` して実行すると速い。
- Windows クライアント → WSL2 上 DS への UDP 接続は、WSL2 の localhost フォワーディングが不安定なことがある。
  必要なら DS の WSL2 IP 指定、またはクライアントも WSL2 側で実行する。
- `pkill -f survival-server` は**自身のシェルのコマンドライン文字列にもマッチしてシェルごと落とす**ため、
  DS 停止は PID 指定で行う。

---

## 6. 成果物の状態

- WSL2 側コード（`services/`・`proto/`・`infra/`）は git クリーン＝コミット済み（`0042f94 Story_0001実装`）。
- WSL2 側の未処理タスクは無し。未追跡ファイルは `unity/` 配下と Windows 側成果物のみ（当スコープ外）。
- バックエンド docker スタック（postgres/nats/auth）は稼働継続。不要時は `make down`。

---

## 7. 次マイルストーン

- **AT-001 本受入**（2 クライアントが別アカウントで同一 World 接続・移動同期・所有外 Character 非操作）は Windows 側主導の最終 E2E。
- **M2 / story_0002**（World Load/Save、api サービス本実装）。M1 で据え置いた api（health のみ）を本実装する。
