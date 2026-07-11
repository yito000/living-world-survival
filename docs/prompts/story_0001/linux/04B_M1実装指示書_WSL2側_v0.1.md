---
title: "M1 実装指示書（WSL2 / Linux 側）"
subtitle: "Auth/Matchmaking 本実装・REST/Internal gRPC・JWT・Join Ticket・DB"
document_id: "IMPL-M1-WSL-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M1 / WSL2側）"
baseline: "Go（auth/api）/ PostgreSQL / gRPC(buf) / JWT / Argon2id / NATS JetStream"
related_document: "04A_M1実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M1 実装指示書：WSL2 / Linux 側 v0.1

本書は **M1（接続）** の作業を **WSL2（Ubuntu）側**（Auth/Matchmaking の本実装、REST・Internal gRPC、JWT、Join Ticket 署名/単回使用、DB マイグレーション、`go test`／`make smoke` 拡張）に限定して指示する。Unity/クライアント・Dedicated Server 側は別冊 **04A（Windows側）** を参照。第0章「分担と連携」は両冊で共通・同一内容（詳細は 03A/03B 第0章と共通）。

M1 のマイルストーン成果（MVP第19章）: **Auth、Matchmaking、Join Ticket、FishNet接続、2 Client移動（3D TPS/WASD）**。本書はそのうち **バックエンド（Auth/Matchmaking）** を担当する。FishNet 接続・3D TPS・Join Ticket の提示/検証は 04A を参照。

---

## 0. 分担と連携（共通・両冊同一／詳細は 03A/03B 第0章と共通）

### 0.1 環境別 責務分担（M1 要点）

| 領域 | 担当環境 | M1 での主なタスク |
|---|---|---|
| Auth/Matchmaking REST（`/v1/...`） | **WSL2** | アカウント/セッション/matchmaking join の本実装（本書3章） |
| Auth Internal gRPC（`MatchmakingService`） | **WSL2** | RedeemJoinTicket/RegisterServer/Heartbeat/MarkDraining |
| JWT（access/refresh）・Join Ticket 署名 | **WSL2** | 署名鍵管理、単回使用の原子的更新 |
| DB マイグレーション（Auth 所有テーブル） | **WSL2** | accounts/password_credentials/refresh_tokens/game_servers/join_tickets |
| proto 定義 + 生成（buf） | **WSL2**（生成） | 既存 `auth.proto`/`gameplay.proto` を使い buf generate 済み前提 |
| FishNet 接続・Authenticator・3D TPS | **Windows** | Join Ticket 提示、DS 側公開鍵検証、2 Client 移動同期（04A） |
| REST クライアント（Unity） | **Windows** | アカウント作成/ログイン/matchmaking join の呼び出し（04A） |

> 分担の全体像（リポジトリ配置 0.2 / Git・LFS・改行 0.3 / 境界成果物 0.4 / 連携フロー 0.5）は 03B 第0章と同一。本書では M1 で新たに動く境界だけを補足する。

### 0.2 M1 の境界成果物（環境をまたぐ主要な取り決め）

| 成果物 | 生成/正 | 消費側 | 合意事項 |
|---|---|---|---|
| Join Ticket（署名付きトークン） | **WSL2（Auth 秘密鍵で署名）** | Windows（DS が公開鍵で事前検証、Auth が単回消費） | 署名アルゴリズム・公開鍵配布方法・トークンエンコードを本書 3.4 で確定し 04A と一致させる |
| `MatchmakingService`（gRPC） | **WSL2（実装）** | Windows（DS が gRPC クライアントとして呼ぶ） | `RedeemJoinTicket`/`RegisterServer`/`Heartbeat`/`MarkDraining`。既存 `proto/survival/v1/auth.proto` が唯一の正 |
| REST（`/v1/accounts` 等） | **WSL2（実装）** | Windows（`AuthClient` が HTTP 呼び出し） | パス・リクエスト/レスポンスJSONを本書 3.1〜3.3 で確定し 04A と一致させる |
| build_id | 両者合意 | 両者 | matchmaking join / RegisterServer / Join Ticket claims の `build_id` は Client・Server・Auth で同一値を使う（不一致は拒否） |

---

## 1. 対象と前提（WSL2側）＋ M1 の DoD

- 環境: Windows 上の **WSL2（Ubuntu 22.04 推奨）** + **Docker Desktop（WSL2 バックエンド）**（03B 1章）。
- リポジトリ: `/mnt/c/dev/living-world-survival`（03B 0.2）。
- 本書の完了で、**Auth/Matchmaking が REST と Internal gRPC を本実装**し、2 つの Unity Client が「アカウント作成 → ログイン → matchmaking join → Join Ticket 取得」を行え、DS が gRPC で Ticket を単回消費できる状態にする。
- api サービスは **M1 では M0 の最小（health のみ）のまま**据え置く（World Load/Save は M2 で本実装）。

### 1.1 WSL2側 M1 DoD

- REST 5 本が動作する: `POST /v1/accounts` / `POST /v1/sessions` / `POST /v1/sessions/refresh` / `DELETE /v1/sessions/current` / `POST /v1/matchmaking/join`。
- Internal gRPC `MatchmakingService` の 4 RPC が動作する: `RedeemJoinTicket` / `RegisterServer` / `Heartbeat` / `MarkDraining`。
- パスワードは **Argon2id** でハッシュ化して `password_credentials` に保存（平文・可逆暗号禁止, BSD第11章）。
- **Access Token = 短寿命 JWT**、**Refresh Token = ローテーション + 再利用検知**（DB はハッシュ保存, RFC 9700 準拠）。`JWT_SIGNING_KEY` で署名。
- **Join Ticket は Auth 秘密鍵で署名**、`RedeemJoinTicket` で **`used_at IS NULL` 条件更新による単回使用**を保証。期限切れ/再利用/server・build 不一致を拒否。
- Auth 所有 5 テーブルのマイグレーション（`0002_...`）が `make migrate` で適用される。
- `go test ./...`（auth）が緑。`make ci`（proto/lint/test）が緑、生成物ドリフトなし。
- `make smoke` が拡張され、health に加えて **REST/gRPC の疎通（アカウント作成→ログイン→join→redeem）** を確認する。

---

## 2. 前提成果物（M0 で構築済み・本書の起点）

M0（03A/03B）で以下が構築済み。M1 はこの上に積む。

- `infra/docker-compose.yml`（postgres:16 / nats:2 / auth:8081 / api:8082 / worldstate:8083 / llm-worker）、`infra/nats/nats.conf`。
- `proto/survival/v1/*.proto`（auth/worlddata/economy/worldevent/gameplay/ai/common）と `buf.yaml`/`buf.gen.yaml`。**M1 で使う `MatchmakingService`・`JoinTicketClaims`・`InputCommand` は既に定義済み**（下記 3 章参照）。
- 生成物: `services/gen/go/survival/v1/*.pb.go`（`auth_grpc.pb.go` 含む）、`services/gen/python/...`、`unity/SurvivalWorld/Assets/Generated/`（C#）。
- `services/auth`・`services/api`（Go, health + `/readyz` DB ping）。`services/auth/migrations/0001_init.up.sql`（accounts/password_credentials/refresh_tokens/game_servers/join_tickets の**最小 DDL**が既に存在）。
- `services/worldstate`・`services/llm-worker`（Python, health）。
- `Makefile` + `scripts/`（`ci_go.sh`/`ci_python.sh`/`ci_proto.sh`/`ci_assets.sh`/`migrate.sh`/`smoke.sh`/`check_tools.sh`）、`mise.toml`。
- M0 DoD 全緑（bootstrap/up/migrate/ci/smoke）。

> 既存 `services/auth/cmd/authd/main.go` は health/readyz のみの雛形。M1 で REST ルーティング・gRPC サーバ・ドメイン実装を追加する。既存 `0001_init` の Auth テーブルは M0 用の最小列なので、M1 で不足列を `0002` マイグレーションで追加する（下記 3.6）。

### 2.1 生成 gRPC コードの取り込み（重要な前提整備）

- 生成 Go コードは `services/gen/go/survival/v1`（go_package_prefix = `living-world-survival/services/gen/go`）に出力されるが、**現状 `services/gen/go` に `go.mod` が無い**。auth からこの gRPC スタブ（`survival.v1.MatchmakingServiceServer` 等）を import できるよう、いずれかで結線する:
  - 推奨: リポジトリ直下に **`go.work`** を置き `services/auth`・`services/api`・`services/gen/go`（go.mod 付与）を束ねる、または
  - `services/gen/go` に `go.mod`（module `living-world-survival/services/gen/go`）を追加し、auth の `go.mod` に `require` + `replace ./ ../gen/go` を書く。
- 結線後 `go build ./...`（auth）が生成スタブを解決できることを確認。`ci_go.sh` の `go build` 対象に gen を含める。

---

## 3. 実装対象（Auth/Matchmaking サービス）

対象は **`services/auth`** のみ（api は据え置き）。設計は MVP第11章・第13章、BSD第4.1・第11章を正とする。パッケージ構成（提案）:

```text
services/auth/
├─ cmd/authd/main.go                 # 既存。REST + gRPC を起動するよう拡張
├─ internal/
│  ├─ config/       # env 読み込み（JWT_SIGNING_KEY, JOIN_TICKET_* 等）
│  ├─ store/        # pgx リポジトリ（accounts/credentials/refresh_tokens/game_servers/join_tickets）
│  ├─ password/     # Argon2id ハッシュ/検証
│  ├─ token/        # JWT 発行/検証、Refresh ローテーション/再利用検知
│  ├─ ticket/       # Join Ticket 署名/検証（Ed25519）、claims
│  ├─ rest/         # net/http ハンドラ（/v1/...）
│  └─ grpc/         # MatchmakingServiceServer 実装
└─ migrations/0002_auth_m1.up.sql / .down.sql
```

### 3.1 Client 向け REST（MVP 11.1）— アカウント/セッション

`net/http`（M0 と同じ標準ライブラリ方針）で以下を実装する。JSON 入出力。エラーは `{"error":{"code","message"}}` 形式で統一。

| # | Method | Path | Request（JSON） | Response（JSON, 成功時） | 主な検証/挙動 |
|---|---|---|---|---|---|
| R1 | POST | `/v1/accounts` | `email`, `password`, `display_name` | `201` `{ account_id }` | email 一意（`accounts_email_unique`）、password を Argon2id で `password_credentials` へ保存。email 重複は `409`。 |
| R2 | POST | `/v1/sessions` | `email`, `password` | `200` `{ access_token, refresh_token, expires_in }` | credential 検証（Argon2id）。失敗は `401`。refresh_token を `refresh_tokens` に**ハッシュ保存**し `family_id` を採番。 |
| R3 | POST | `/v1/sessions/refresh` | `refresh_token` | `200` 新しい Token pair（access+refresh） | Refresh ローテーション。**再利用検知**時は family 全体を失効し `401`（3.3）。 |
| R4 | DELETE | `/v1/sessions/current` | （`Authorization: Bearer <access_token>`） | `204` | access_token から account を特定し、当該 refresh family を `revoked_at` 設定でログアウト。 |

- `display_name` は MVP では characters（API 所有）へは載せず、M1 では account に紐づく表示名として `accounts` か別途保持でよい（`accounts` に列追加するか、M1 は受領のみで保存省略可。実装時に 3.6 の DDL と整合させる）。
- 入力バリデーション（email 形式、password 長）を最低限行う。詳細ポリシーは後続で強化。

### 3.2 Client 向け REST（MVP 11.1）— Matchmaking Join

| # | Method | Path | Request（JSON） | Response（JSON, 成功時） | 主な検証/挙動 |
|---|---|---|---|---|---|
| R5 | POST | `/v1/matchmaking/join` | `character_id`, `build_id`（`Authorization: Bearer <access_token>`） | `200` `{ server_endpoint, join_ticket, expires_at }` | access_token 検証 → build 互換の **ready な game_server** を選定 → `join_tickets` 行を作成 → **署名付き Join Ticket** を発行して返す。 |

- サーバ選定: `game_servers` から `ready = true` かつ `build_id` 一致（かつ capacity 余裕）を選ぶ。M1 は 1 World・1 DS 前提（BSD第14章）でよいが、複数対応可能な SQL にする。
- `join_ticket` は 3.4 の署名付きトークン文字列。`expires_at` は発行時刻 + `JOIN_TICKET_TTL`（既定 60 秒, BSD第4.1「60秒程度」）。
- 対応する `join_tickets` 行を `used_at = NULL` で INSERT（`ticket_id` は claims の `ticket_id` と一致）。

### 3.3 JWT（access/refresh）とトークン運用（BSD第11章 / RFC 9700）

- **Access Token**: JWT（`HS256` で `JWT_SIGNING_KEY` 署名、または Ed25519）。claims に `sub=account_id`、`exp`（短寿命, 既定 15 分程度）、`aud`/`scope` を用途限定で付与。`/v1/matchmaking/join`・`DELETE /v1/sessions/current` は Bearer で検証。
- **Refresh Token**: 不透明ランダム文字列。DB には **ハッシュ（例: SHA-256）保存**（平文保存禁止）。`family_id` でローテーションチェーンを管理。
  - `refresh` 時: 提示トークンを検索 → 未失効・未期限切れなら**新 access+refresh を発行し古い refresh を `revoked_at` で失効**（ローテーション）。
  - **再利用検知**: 既に `revoked_at` が入った refresh が再提示されたら、その `family_id` 全体を失効させて `401`（盗用対策, RFC 9700）。
- `refresh_tokens` 列: `token_id, account_id, token_hash, family_id, expires_at, revoked_at`（既存 0001 に一致）。

### 3.4 Join Ticket 署名・検証（MVP 11.3 / BSD第4.1）

- **claims は proto の `survival.v1.JoinTicketClaims`**（`auth.proto` 既定）を用いる:
  `ticket_id, account_id, character_id, server_id, world_id, build_id, issued_at_unix_ms, expires_at_unix_ms, nonce`。
- **署名鍵は Auth の秘密鍵、DS は公開鍵で事前検証**（BSD第4.1 / MVP 11.3）。**非対称鍵（Ed25519 推奨）**にして、公開鍵を DS へ配布（04A と一致させる。配布方法は env/ファイル/エンドポイントのいずれか、本書 3.4 で確定して 04A に伝える）。
- **トークンエンコード**: claims を JWS（例: JWT で Ed25519=`EdDSA`）にする。ペイロードに上記 claims を格納し、`iat`/`exp` は `issued_at_unix_ms`/`expires_at_unix_ms` と対応させる。DS が JWS 検証だけで期限・server・build を確認でき、`RedeemJoinTicket` で単回性を確認する二段構え。
- **単回使用**: `RedeemJoinTicket` で以下を原子的に実行:

```sql
UPDATE join_tickets
   SET used_at = now()
 WHERE ticket_id = $1
   AND used_at IS NULL      -- ★単回使用の肝（再利用は 0 rows）
   AND expires_at > now();
-- 影響行数 1 → OK / 0 → 再利用 or 期限切れ or 不存在 → error
```

- 拒否条件（`RedeemJoinTicket` が `ok=false, error=...` を返す）: 署名不正 / 期限切れ / `server_id` 不一致 / `build_id` 不一致 / 既に `used_at` あり（再利用） / 行なし。**無効 Character の拒否**は FishNet Authenticator 側（04A）と二重防御。
- `JOIN_TICKET_SIGNING_KEY`（秘密鍵）・`JOIN_TICKET_PUBLIC_KEY`（公開鍵）・`JOIN_TICKET_TTL` を config 化（`.env.example` に追記, 平文実鍵はコミットしない）。

### 3.5 Internal gRPC `MatchmakingService`（MVP 11.2）

既存 `proto/survival/v1/auth.proto` の `MatchmakingService`（buf generate 済み。`services/gen/go/survival/v1/auth_grpc.pb.go`）を **実装**する。DS ⇔ Auth 間専用（Public 公開しない, BSD第11章）。gRPC サーバは REST とは別ポート（例 `AUTH_GRPC_PORT=9091`）で待受。

| # | RPC | Request 主フィールド | 処理 |
|---|---|---|---|
| G1 | `RedeemJoinTicket` | `server_id`, `ticket` | 署名検証 → `used_at IS NULL AND expires_at > now()` 条件更新で単回消費 → 成功なら `JoinTicketClaims` を返す。失敗は `ok=false, error`。 |
| G2 | `RegisterServer` | `server_id`, `world_id`, `build_id`, `endpoint`, `capacity` | `game_servers` を upsert（登録/更新）。`ready=false` で登録し Heartbeat で ready 化してよい。 |
| G3 | `Heartbeat` | `server_id`, `players`, `ready`, `tick_ms` | `game_servers.last_seen = now()`、`ready` 更新。matchmaking 対象は `ready=true` かつ last_seen 近傍。 |
| G4 | `MarkDraining` | `server_id` | `ready=false`（ドレイン）にし matchmaking 対象外化。 |

- gRPC サーバ起動を `cmd/authd/main.go` に追加（REST HTTP サーバと並走、graceful shutdown も両方対象）。
- 認証: M1 は内部ネットワーク前提で最小でよいが、将来のサービス資格情報（BSD第11章）を見据え、共有シークレット等の差し込み口を config に用意しておく（実装は最小可）。

### 3.6 DB マイグレーション（`0002_auth_m1`）

`services/auth/migrations/0002_auth_m1.up.sql` / `.down.sql`（golang-migrate 形式, 03B 7章）。既存 `0001_init` の**最小テーブルに M1 で必要な列/制約を追加**する。MVP第13章の列を正とする。

- `game_servers`: 実装で参照する列を確認。0001 は `server_id, world_id, build_id, endpoint, capacity, ready, last_seen` を持つため、M1 は原則そのまま利用可。必要なら `status`（active/draining）を追加。
- `join_tickets`: 0001 は `ticket_id, account_id, character_id, server_id, expires_at, used_at`。**claims と整合させるため `world_id`, `build_id`, `nonce`, `issued_at` 列を追加**（`JoinTicketClaims` の各フィールドを DB でも参照/監査できるように）。`ticket_id` 一意（PK）は既存（MVP 13.1「ticket_id は一意制約」）。
- `refresh_tokens`: 0001 の列で足りる。`token_hash` に一意 index、`(account_id, family_id)` index を追加。
- `accounts`/`password_credentials`: `email UNIQUE` は既存。`display_name` を保持するなら `accounts` に列追加。
- 制約（MVP 13.1）: `email`・`ticket_id` は一意。通貨列は無い領域だが、他領域と同様 BIGINT 方針は踏襲（本書対象外）。

> M1 は Auth 所有テーブルのみ。characters（API 所有）は M2 で本実装。matchmaking join の `character_id` は M1 では**存在検証を簡略化**（形式チェック＋将来 API 連携の差し込み口）してよいが、DS 側 Authenticator が最終的に無効 Character を拒否する（04A・MVP 11.3）。

### 3.7 設定（`.env.example` 追記）

M0 の `.env.example` に M1 用を追記（実値は `.env`=gitignore）:

```dotenv
# --- M1 Auth ---
JWT_SIGNING_KEY=dev-only-change-me          # access token 署名（HS256 の場合）
ACCESS_TOKEN_TTL=15m
REFRESH_TOKEN_TTL=720h
JOIN_TICKET_SIGNING_KEY=...                 # Auth 秘密鍵（Ed25519, dev用）
JOIN_TICKET_PUBLIC_KEY=...                  # DS 配布用 公開鍵（04A と一致）
JOIN_TICKET_TTL=60s
AUTH_GRPC_PORT=9091
```

---

## 4. 実装順序表（WSL2側 M1）

| # | タスク | 完了確認 |
|---|---|---|
| L-1 | `services/gen/go` の module 結線（go.work か go.mod+replace）。auth から gRPC スタブを import 可能に（2.1） | `go build ./...`（auth）が生成物を解決 |
| L-2 | `internal/config` 実装（M1 env 読込, 3.7） | 起動時に必須 env 検証が通る |
| L-3 | `migrations/0002_auth_m1`（join_tickets 列追加ほか, 3.6）+ `make migrate` | 追加列/制約が適用される |
| L-4 | `internal/password`（Argon2id）+ `internal/store`（pgx リポジトリ） | 単体テストでハッシュ検証・CRUD 通過 |
| L-5 | `internal/token`（JWT 発行/検証, refresh ローテ/再利用検知, 3.3） | 単体テストでローテ・再利用検知が通る |
| L-6 | REST R1〜R4（accounts/sessions/refresh/logout, 3.1） | curl でアカウント作成→ログイン→refresh→logout |
| L-7 | `internal/ticket`（Ed25519 署名/検証, claims=JoinTicketClaims, 3.4） | 単体テストで署名検証・改竄検出 |
| L-8 | REST R5（matchmaking/join, 3.2）+ join_tickets 行作成 | curl で server_endpoint/join_ticket/expires_at 取得 |
| L-9 | gRPC `MatchmakingService` 実装 G1〜G4（3.5）+ authd 起動に組込み | grpcurl 等で 4 RPC が応答 |
| L-10 | `RedeemJoinTicket` の `used_at IS NULL` 単回使用（3.4）+ 再利用/期限/不一致拒否 | 2 回 redeem で 2 回目が error |
| L-11 | `go test ./...`（auth）: 単体+統合（3.1〜3.5） | `make test` 緑 |
| L-12 | `scripts/smoke.sh` 拡張（REST/gRPC 疎通, 5章） | `make smoke` が join→redeem まで確認 |
| L-13 | `make ci`（proto/lint/test）+ ドリフト検査 | 全緑・生成物差分なし |

---

## 5. テスト・受入（WSL2側）

MVP第18章の自動テスト区分（Unit / Integration）に対応させる。M1 の主対象は **AT-001（2 Client が別 Account でログイン・同一 World 接続）** の前提となる認証・matchmaking 基盤。

### 5.1 自動テスト区分（本書の担当）

| 区分 | 対象（M1・WSL2） |
|---|---|
| Unit | Argon2id ハッシュ/検証、JWT 発行/検証、**Refresh ローテーション・再利用検知**、**Join Ticket claims 署名/検証・改竄検出**（MVP 18.1「Ticket claims」）。 |
| Integration | Go auth + PostgreSQL: アカウント作成→ログイン→refresh→logout、**matchmaking join→RedeemJoinTicket 単回消費**、再利用/期限切れ/build不一致の拒否（MVP 18.1「Auth Refresh rotation」）。 |

### 5.2 受入シナリオ（`go test` / smoke）

- **単回使用**: 同一 `ticket` で `RedeemJoinTicket` を 2 回 → 1 回目 `ok=true`、2 回目 `ok=false`（`used_at` 済み）。
- **再利用検知（refresh）**: 一度ローテした古い refresh_token を再提示 → family 全体失効・`401`。
- **期限切れ**: `expires_at` 経過後の redeem → 拒否。
- **build/server 不一致**: claims と `RedeemJoinTicketRequest.server_id` 不一致、または `build_id` 不一致 → 拒否。
- **matchmaking**: ready な `game_servers` が無いと `join` は適切なエラー（例 `503`/`409`）。`RegisterServer`+`Heartbeat(ready=true)` 後は成功。

### 5.3 smoke 拡張（`scripts/smoke.sh`）

M0 の health チェック（auth/api/worldstate/llm-worker `/healthz`）に加えて、E2E 相当の疎通を追記:

1. `POST /v1/accounts` → `POST /v1/sessions` で token 取得。
2. gRPC `RegisterServer` + `Heartbeat(ready=true)` でダミー DS を登録。
3. `POST /v1/matchmaking/join` で `join_ticket` 取得。
4. gRPC `RedeemJoinTicket` で単回消費（1 回目 OK / 2 回目 error）。

> smoke は `curl`（REST）と `grpcurl` もしくは小さな Go テストバイナリ（gRPC）で実装。`make smoke` の依存に build を含める（03B 5章 smoke ターゲット踏襲）。

---

## 6. 落とし穴（WSL2側）

- **生成 gRPC の module 未結線**（2.1）。`services/gen/go` に `go.mod`/`go.work` が無いと auth が `MatchmakingServiceServer` を import できずビルド不能。最初に L-1 で解消。
- **単回使用を `SELECT → UPDATE` の 2 文で書く**と競合で二重消費が起きる。**`UPDATE ... WHERE used_at IS NULL`（1 文）＋影響行数判定**にする（3.4）。同様に matchmaking の capacity 判定も競合注意。
- **Refresh を平文 DB 保存**は禁止（BSD第11章）。必ずハッシュ保存。**再利用検知で family 失効を忘れる**と盗用トークンが生き続ける（RFC 9700）。
- **パスワードを bcrypt/独自ではなく Argon2id**（BSD第11章）。パラメータは OWASP 下限以上。
- **Join Ticket を対称鍵（HMAC）で署名**すると DS に秘密鍵を配ることになり、公開鍵事前検証の要件（DS=公開鍵）を満たせない。**非対称（Ed25519）**にする。DS へ渡すのは公開鍵のみ。
- **build_id の不整合**。Client の join・DS の `RegisterServer`・claims の `build_id` が三者一致しないと接続が全拒否。04A と値を合わせる（0.2 の合意事項）。
- **gRPC を Public 公開**しない（BSD第11章）。compose ではポートを内部のみ、または DS からのみ到達可能に。
- **`.sh` の CRLF**・**proto 生成物のコミット漏れ**は M0 同様に CI 失敗要因（03B 10章）。`make ci` の `git diff --exit-code` で検出。
- api を M1 で触らない（据え置き）。触るとドリフトや責務越境になる。

---

## 参考資料

[R6] [FishNet 認証（Authenticator）](https://fish-networking.gitbook.io/docs/manual/guides/authentication)
[R12] [OWASP Password Storage Cheat Sheet（Argon2id）](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
[R13] [RFC 9700: Best Current Practice for OAuth 2.0 Security](https://www.rfc-editor.org/rfc/rfc9700)
[R-JWT] [RFC 7519: JSON Web Token (JWT)](https://www.rfc-editor.org/rfc/rfc7519)
[R-ED] [RFC 8037 / Ed25519 署名（EdDSA）](https://www.rfc-editor.org/rfc/rfc8037)
[R-GRPC-GO] [gRPC-Go](https://grpc.io/docs/languages/go/)
[R-BUF] [Buf docs](https://buf.build/docs)
[R-MIGRATE] [golang-migrate](https://github.com/golang-migrate/migrate)
