---
title: "M2 実装指示書（WSL2 / Linux 側）"
subtitle: "WorldDataService / ActorStateService gRPC・World Load/Save・Inventory 永続化・migrations 0002"
document_id: "IMPL-M2-WSL-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M2 / WSL2側）"
baseline: "Go / gRPC / PostgreSQL / NATS JetStream / buf"
related_document: "05A_M2実装指示書_Windows側_v0.1.md, 04A_M1実装指示書_Windows側_v0.1.md, 04B_M1実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M2 実装指示書：WSL2 / Linux 側 v0.1

本書は **M2「Inventory / Save」** の作業を **WSL2（Ubuntu）側**（API の gRPC 永続化サービス、DB 拡張、Outbox relay、Item Definition マスタ供給）に限定して指示する。Unity / Dedicated Server / クライアントは別冊 **05A（Windows側）** を参照。第0章「分担と連携」は両冊で共通・同一内容（M0 03A/03B と同一方針）。

M2 の到達点（MVP 第19章）: **共通Inventory・Item Definition・World Load/Save・Cache削除復旧**。本書（B）はその**永続 Writer 側**（API）を実装する。ランタイム（DS メモリ正本）は 05A（A）が実装する。

---

## 0. 分担と連携（共通・両冊同一）

### 0.1 環境別 責務分担（M2 該当分を再掲）

| 領域 | 担当環境 | M2 の主なタスク |
|---|---|---|
| Unity Runtime 共通Inventory / Item Definition(SO) | **Windows(A)** | slot/weight/version、InventoryCommand 適用、楽観ロック |
| World Bootstrap クライアント（DS） | **Windows(A)** | `LoadBootstrap` gRPC 呼出→Event適用→Ready Heartbeat |
| ローカルキャッシュ削除→復旧 | **Windows(A)** | 5.4、Server 正本から再取得 |
| **WorldDataService gRPC 実装（API）** | **WSL2(B)** | LoadBootstrap / AppendEvents / SaveSnapshot |
| **ActorStateService.Save 実装** | **WSL2(B)** | actor_runtime_states 永続化 |
| **DB 拡張（migrations 0002）** | **WSL2(B)** | FK・一意制約・GIN/Expression Index・追加テーブル |
| **Outbox relay（≤1秒）** | **WSL2(B)** | API 自身の outbox_messages → NATS 公開 |
| **Item Definition マスタ供給** | **WSL2(B)** | MVP 7.2 の18定義を正本データとして供給 |
| proto 生成（buf） | **WSL2(B)**（生成） | Go/Python/C# を生成し出力（既存 worlddata/gameplay/common を消費） |
| Docker Compose / DB / NATS / `make ci` | **WSL2(B)** | 起動・migrate・test |

### 0.2 リポジトリ配置・Git/LFS・改行コード規約

- 配置・改行・LFS の規約は M0（03A/03B 第0.2〜0.3章）と同一。**変更しない**。
- B が触るのは `services/`, `proto/`, `infra/`, `assets-pipeline/`, `scripts/*.sh`, `Makefile` のみ。**`unity/` は触らない**（proto の C# 生成物 `unity/SurvivalWorld/Assets/Generated/` のみ B が `buf generate` で出力する）。

### 0.3 境界成果物（M2 で増えるもの）

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| proto → C# 生成コード（worlddata/gameplay/common） | WSL2（`buf generate`） | Windows（DS/Client） | `unity/SurvivalWorld/Assets/Generated/` |
| proto → Go 生成コード（同上） | WSL2 | WSL2（api） | `services/gen/go/survival/v1/` |
| DBスキーマ migrations 0002 | WSL2 | 参照: 両方 | `services/api/migrations/` |
| **Item Definition マスタ（正本データ）** | **WSL2(B)** | **Windows(A) が SO へ取込・API が検証** | `services/api/data/item_definitions.json`（新規） |
| gRPC エンドポイント（api:8082 gRPC ポート） | WSL2 | Windows（DS が呼出） | 本書 3章で規定 |

### 0.4 連携フロー（M2 代表例）

- **DS 起動時**: A の World Bootstrap クライアントが B の `WorldData.LoadBootstrap(world_id, server_build)` を gRPC で呼び、`snapshot_id / sequence / snapshot_payload / event_tail[]` を受ける。A は payload を復元し `sequence` 以降の `event_tail` を順に適用してから Ready Heartbeat を送る（MVP 12.1）。
- **プレイ中**: A（DS）が Domain Event を Outbox から `WorldData.AppendEvents(server_id, events[])` へ **≤1秒**間隔で送る。B は `event_id` で重複排除し、永続 `sequence` を **API 側で採番**して確定する。
- **30秒ごと**: A（DS）が `WorldData.SaveSnapshot(world_id, sequence, checksum, payload)` を呼ぶ。B は **staging保存→checksum検証→active pointer 更新**の順で切り替える（MVP 12.1）。
- **単一Writer原則**: 永続 Writer は常に **API(B)**、セッション Writer は常に **DS(A)**（MVP 12.2.1 / 基本設計 6.1・9.4）。B は `inventory_entries / item_instances / currency_ledger / world_snapshots / domain_events` の唯一の Writer。

---

## 1. 対象と前提（WSL2側）と M2 DoD

### 1.1 対象

M2 で B が実装するのは、**API を「World と Inventory の永続 Writer」として機能させる** 部分である。具体的には次を `services/api` に追加する。

1. **gRPC サーバー**を `apid` に追加（既存の HTTP health は維持）。
2. `WorldDataService`（`LoadBootstrap` / `AppendEvents` / `SaveSnapshot`）実装。
3. `ActorStateService.Save` 実装。
4. **migrations 0002**（FK・一意制約・GIN/Expression Index・`actor_runtime_states` / `actor_state_projections` / `item_definitions` 追加）。
5. **Outbox relay**（API 自身の `outbox_messages` を NATS へ ≤1秒 で公開）。
6. **Item Definition マスタ供給**（MVP 7.2 の18定義）。
7. 上記のユニット/結合テスト。

### 1.2 M2 DoD（WSL2側の満たすべき条件）

- `make migrate` で **migrations 0002** が適用され、下記が成立する。
  - `inventory_entries.inventory_id` → `inventories` の FK（ON DELETE CASCADE、既存）が有効。
  - `item_instances` への FK（`inventory_entries.item_instance_id`、既存）が有効。
  - `domain_events` の一意制約 **`UNIQUE(world_id, sequence)`**（既存）＋ `event_id` PK による重複排除が有効。
  - `world_snapshots.payload` / `domain_events.payload` に **GIN Index**、頻出 JSON Path に **Expression Index** が付与される。
  - `actor_runtime_states` / `actor_state_projections` / `item_definitions` テーブルが存在する。
- `WorldData.LoadBootstrap` が **最新 active Snapshot の ID+payload と、その sequence 以降の Domain Event tail** を返す（Snapshot 不在時は空 payload と sequence=0）。
- `WorldData.AppendEvents` が **同一 `event_id` の再送を DUPLICATE として無視**し、新規は API 採番 `sequence` で確定する。`(world_id, sequence)` 競合時は CONFLICT。
- `WorldData.SaveSnapshot` が **staging→checksum検証→active pointer 更新**を1トランザクションで行い、checksum 不一致は保存を拒否する。
- `ActorState.Save` が `actor_runtime_states` を `version` 単調増加で upsert する（古い version は無視）。
- **Outbox relay** が `outbox_messages`（`published_at IS NULL`）を **≤1秒**間隔で NATS へ公開し、成功後に `published_at` を確定する。
- Item Definition マスタ（18定義）が `services/api/data/item_definitions.json` として供給され、API 起動時に読み込み・検証できる（数量/品質/耐久の Client 自由指定を拒否する検証の基礎データ）。
- `make ci`（proto/lint/test）が緑。gRPC 実装のユニット/結合テストが成功する。
- **重要データ（Inventory/所持金/Snapshot）を Client Cache に置かない**方針に反する API 応答がない（MVP 5.4 / 付録C）。

---

## 2. 前提成果物（M0 / M1 で構築済み）

M2 は次を**前提**とする（再実装しない）。

**M0（03B / 既存）**:
- `proto/survival/v1/{common,worlddata,gameplay,economy,...}.proto` と `buf` 生成（`services/gen/go/survival/v1/*.pb.go`・`*_grpc.pb.go` 生成済み）。
- `services/api`（Go）: `apid` の HTTP `/healthz` `/readyz`（Postgres+NATS 接続確認）。
- **migrations 0001**: `worlds / characters / world_snapshots / domain_events / item_instances / inventories / inventory_entries / currency_ledger / purchase_transactions / outbox_messages / inbox_dedup`。通貨は BIGINT。`domain_events` は `UNIQUE(world_id, sequence)` と `(world_id, occurred_at)` 索引済み。`outbox_messages` は `published_at IS NULL` の部分索引済み。
- `infra/docker-compose.yml`（postgres:16 / nats:2 `-js`）、`Makefile`、`scripts/*.sh`、`mise.toml`。
- `services/gen/go` は Go module `living-world-survival/services/gen/go`。api からの参照方法（`go.work` または `require`+`replace`）は M1 で確立済みとする。未確立なら本書 3.1 で確立する。

**M1（04B / 前提）**:
- Auth / Matchmaking / Join Ticket、DS ↔ Auth 内部 gRPC、FishNet 接続。
- api は gRPC サーバー未実装でよい（M2 で追加）。M1 で gRPC 基盤（TLS/サービス認証の枠組み・MVP-SEC-007）が入っていれば流用する。

> M0/M1 の DoD が全緑であることを前提とする。前提が崩れている場合は該当マイルストーンを先に復旧すること。

---

## 3. 実装対象（サービス／ドメインごと）

### 3.1 apid への gRPC サーバー追加

`services/api` に gRPC サーバーを追加する。HTTP（health）と gRPC は**別ポート**で同居させる。

- gRPC 待受ポート: `API_GRPC_PORT`（既定 `8092`）を `.env` / `.env.example` に追加。HTTP は既存 `API_PORT=8082` を維持。
- 生成 Go 実装の参照: `import worldpb "living-world-survival/services/gen/go/survival/v1"`。
  - api module から gen module を参照するため、リポジトリ直下に `go.work`（`use ./services/api ./services/gen/go ...`）を用意するか、`services/api/go.mod` に `require living-world-survival/services/gen/go v0.0.0` ＋ `replace living-world-survival/services/gen/go => ../gen/go` を追加する（M1 で未確立なら本節で確立）。
- gRPC ランタイム依存: `google.golang.org/grpc` を `services/api/go.mod` に追加。
- サーバー起動: `grpc.NewServer(...)` を作り、`worldpb.RegisterWorldDataServiceServer` / `worldpb.RegisterActorStateServiceServer` で登録。`net.Listen("tcp", ":"+grpcPort)` を別 goroutine で `Serve`。既存の graceful shutdown に `grpcServer.GracefulStop()` を追加する。
- セキュリティ（MVP-SEC-007）: 内部 gRPC は TLS + サービス認証を前提。開発は平文可だが、Secret をリポジトリに置かない。M1 の枠組みに合わせる。

**構成（新規ファイル）**:
```text
services/api/
├─ cmd/apid/main.go              # gRPC サーバー起動を追記
├─ internal/
│  ├─ grpcserver/
│  │  ├─ worlddata.go            # WorldDataService 実装
│  │  └─ actorstate.go           # ActorStateService 実装
│  ├─ store/                     # DB アクセス（pgx）
│  │  ├─ snapshot.go             # world_snapshots / worlds.active_snapshot_id
│  │  ├─ events.go               # domain_events 採番・重複排除
│  │  ├─ inventory.go            # inventories / inventory_entries / item_instances
│  │  └─ actor.go                # actor_runtime_states
│  ├─ outbox/relay.go            # outbox_messages → NATS relay（≤1秒）
│  └─ itemdef/itemdef.go         # Item Definition マスタ読込・検証
├─ data/item_definitions.json    # ★Item Definition 正本（A へ供給）
└─ migrations/0002_*.up.sql / .down.sql
```

### 3.2 WorldDataService.LoadBootstrap（MVP 12.1 / 14.2）

シグネチャ（既存 proto。実装対象）:
```protobuf
rpc LoadBootstrap(LoadBootstrapRequest) returns (LoadBootstrapResponse);
// req:  string world_id = 1; string server_build = 2;
// resp: string snapshot_id = 1; int64 sequence = 2; bytes snapshot_payload = 3;
//       repeated DomainEvent event_tail = 4;
```

処理:
1. `worlds` から `world_id` の行と `active_snapshot_id` を取得。行がなければ NOT_FOUND（または空世界の初期化ポリシーに従う。MVP では既存 world 前提）。
2. `active_snapshot_id` が示す `world_snapshots` 行を取得 → `snapshot_id / sequence / payload` を応答へ。active が未設定（新規 world）なら `snapshot_id=""`, `sequence=0`, `snapshot_payload={}`。
3. `domain_events` から **`WHERE world_id=$1 AND sequence > $snapshot.sequence ORDER BY sequence ASC`** を取得し `event_tail[]` に詰める（`DomainEvent{event_id, world_id, aggregate_id, local_sequence, type, payload, occurred_at_unix_ms}`）。
4. `server_build` は互換性検証（不一致は将来 reject 予定。MVP は記録のみでよい）。
5. `occurred_at`（TIMESTAMPTZ）→ `occurred_at_unix_ms`（int64）へ変換して返す。

注意:
- **Snapshot payload はそのまま `bytes` で返す**（API 側で JSON を解釈しない。復元は DS 側の責務）。
- event_tail は sequence 昇順を厳守（DS が順に適用するため）。
- checksum は Snapshot 保存時に検証済みなので Load 時の再検証は必須ではないが、`world_snapshots.checksum` を応答外メタとして保持しておく。

### 3.3 WorldDataService.AppendEvents（MVP 12.1 / 13.1）

シグネチャ:
```protobuf
rpc AppendEvents(AppendEventsRequest) returns (AppendEventsResponse);
// req:  string server_id = 1; repeated DomainEvent events = 2;
// resp: repeated ResultStatus results = 1;  // events[] と同順・同数
```

処理（1リクエスト＝1トランザクション推奨、event 単位で結果を返す）:
1. events を受信順に処理。各 `DomainEvent` は `event_id`（ULID, DS 生成）と `local_sequence`（aggregate 内順序, DS 生成）を持つ。
2. **重複排除**: `event_id` は `domain_events.event_id`（PK, UUID）。同一 `event_id` が既存なら **`RESULT_STATUS_DUPLICATE`**（副作用なし・冪等）。
   - 注: proto/生成物では `event_id` は文字列 ULID。DB PK は UUID。**ULID を UUID 列へ格納するため、ULID(128bit) をそのまま UUID として保持する**か、`event_id TEXT UNIQUE` へ変更する。migrations 0002 で `domain_events.event_id` を **TEXT** 化し `UNIQUE` を付与する方針を採る（ULID を素直に保持。落とし穴6.5参照）。
3. **永続 sequence の採番は API のみ**（MVP 13.1・DEC）。`world_id` ごとに現在の最大 `sequence` を `SELECT max(sequence) ... FOR UPDATE`（またはアドバイザリロック `pg_advisory_xact_lock(hashtext(world_id))`）で取り、`+1` を割り当てて INSERT。
4. `(world_id, sequence)` 一意制約に違反した場合（並行採番の競合など）は当該 event を **`RESULT_STATUS_CONFLICT`**。DS は再送または Snapshot 再同期で回復する。
5. 正常 INSERT は **`RESULT_STATUS_OK`**。`occurred_at_unix_ms` → TIMESTAMPTZ 変換。
6. 必要に応じて、確定した event を API 自身の `outbox_messages` へ積む（WorldState Consumer / Batch への配信用。3.6 relay が NATS へ流す）。

不変条件:
- **domain_events の Writer は API のみ**。WorldState 等は Consumer（付録C）。
- 重複排除は `event_id`、順序保証は `local_sequence`（aggregate 内）と API 採番 `sequence`（world 全体）の二層。

### 3.4 WorldDataService.SaveSnapshot（MVP 12.1）

シグネチャ:
```protobuf
rpc SaveSnapshot(SaveSnapshotRequest) returns (SaveSnapshotResponse);
// req:  string world_id = 1; int64 sequence = 2; string checksum = 3; bytes payload = 4;
// resp: string snapshot_id = 1;
```

処理（**staging→checksum検証→active pointer 更新**を1トランザクション）:
1. **checksum 検証**: 受信 `payload` から API 側で checksum を再計算し、`req.checksum` と一致するか確認。**不一致は保存を拒否**（`INVALID_ARGUMENT`）。アルゴリズムは A/B で一致させる（例: SHA-256 の hex。05A と同一定義。落とし穴6.4）。
2. **staging 保存**: `world_snapshots` へ新規行を INSERT（`snapshot_id`=新規 UUID, `world_id`, `sequence`, `payload`, `checksum`）。この時点では active に切替えない。
   - `UNIQUE(world_id, sequence)`（既存）により同一 sequence の二重保存を防止。既存なら冪等に既存 `snapshot_id` を返してよい。
3. **active pointer 更新**: `UPDATE worlds SET active_snapshot_id=$new WHERE world_id=$1`。ここで初めて有効化。
4. コミット後、旧 Snapshot は保持（Corrupt 時の 1つ前へのフォールバック用・MVP 16章）。GC は M2 スコープ外（保持で可）。
5. 応答に `snapshot_id` を返す。

注意:
- staging と active 切替を**同一 Tx** にすることで「payload は保存されたが active が古い」中途半端状態を作らない。
- checksum は「Corrupt Snapshot 検出」（MVP 16章）の要。**必ず保存前に検証**する。

### 3.5 ActorStateService.Save（MVP 14.2 / 付録C）

シグネチャ:
```protobuf
rpc Save(SaveRequest) returns (SaveResponse);
// req:  string actor_id = 1; int64 version = 2; bytes personal_state = 3;
//       repeated InventoryEntry inventory_summary = 4;
// resp: ResultStatus status = 1;
```

処理:
1. `actor_runtime_states`（migrations 0002 で追加）へ **version 単調増加の upsert**。
   - `INSERT ... ON CONFLICT (actor_id) DO UPDATE SET ... WHERE actor_runtime_states.version < EXCLUDED.version`。
   - 受信 `version` が既存以下なら更新せず **`RESULT_STATUS_DUPLICATE`**（古い保存の追い越しを防止）。更新成功は `OK`。
2. `personal_state`（JSON エンコード済み bytes）は JSONB `payload` として保存。
3. `inventory_summary[]`（`InventoryEntry{slot_index, item, quantity, reserved}`）は runtime state の一部としてサマリ保存。**Inventory の正本 `inventory_entries` はここで書かない**（それは購入/確定経路＝API の別処理。付録C: `actor_runtime_states` の権威は DS、`inventory_entries` の権威は API）。混同しないこと。
4. `world_id` は `actor_id` から解決するか、req に含まれない場合は runtime state の payload から得る（MVP proto に world_id が無いため payload 側で保持）。

> 付録C: `actor_runtime_states` は「DS 生成 → API 永続化」。フィールドの権威は DS、永続書き込みは API 経由。再起動復元（AT-018）で DS が読む。

### 3.6 Outbox relay（API 自身の outbox_messages → NATS, ≤1秒）

MVP 9.2 / 12.1: API が確定したイベント（domain_events 由来・economy 由来）を **NATS へ ≤1秒 で公開**する。

- `internal/outbox/relay.go` に relay ループを実装。
- ポーリング: `SELECT message_id, topic, payload FROM outbox_messages WHERE published_at IS NULL ORDER BY available_at ASC LIMIT $batch` を **1秒間隔（またはそれ未満）**で実行（既存の部分索引 `outbox_unpublished_idx` を利用）。
- 各行を `topic`（NATS Subject, MVP 14.3: `world.{id}.event.actor` / `.resource` / `.economy` 等）へ Publish。JetStream 発行。
- 公開成功後 `UPDATE outbox_messages SET published_at=now() WHERE message_id=$1`。失敗は `retry_count` を増やし次周回へ（指数 Backoff 可）。
- **At-least-once**: 受信側（WorldState 等）は `inbox_dedup` で冪等化（既存テーブル）。
- 注: DS 側の「Event Outbox を ≤1秒 flush・30秒ごと Snapshot」の**タイマー本体は A（DS）**。B は (1) その受け皿となる `AppendEvents`/`SaveSnapshot`、(2) **API 自身の** outbox_messages の NATS relay を担う。両者を混同しない（落とし穴6.6）。

### 3.7 DB 拡張（migrations 0002）

`services/api/migrations/0002_inventory_save.up.sql` / `.down.sql` を追加（golang-migrate 形式）。**既存 0001 は変更しない**（追記マイグレーション）。

**(a) domain_events.event_id を ULID 保持へ**（3.3 の方針）:
```sql
-- ULID(文字列26桁) をそのまま保持するため TEXT 化。UNIQUE で重複排除。
ALTER TABLE domain_events ALTER COLUMN event_id TYPE TEXT USING event_id::text;
-- PK は既存。event_id で dedup。aggregate 内順序と world 順序:
CREATE UNIQUE INDEX IF NOT EXISTS domain_events_agg_local_uq
  ON domain_events (world_id, aggregate_id, local_sequence);
```
（既に 0001 で PK=event_id・`UNIQUE(world_id, sequence)`・`(world_id, occurred_at)` 索引がある前提。UUID→TEXT 変更が困難な環境では 0001 側の型を見直すか、DS が UUID 形式 ULID を送る運用に合わせる。落とし穴6.5。）

**(b) GIN / Expression Index**（MVP 13.1 [R10]）:
```sql
CREATE INDEX IF NOT EXISTS world_snapshots_payload_gin
  ON world_snapshots USING gin (payload jsonb_path_ops);
CREATE INDEX IF NOT EXISTS domain_events_payload_gin
  ON domain_events USING gin (payload jsonb_path_ops);
-- 頻出 JSON Path の Expression Index（例: event payload の type/aggregate 絞り込み補助）
CREATE INDEX IF NOT EXISTS domain_events_type_idx ON domain_events (type);
```

**(c) inventories の一意性と索引**:
```sql
-- owner は player/ai の polymorphic。1 owner 1 inventory（MVP 前提）を一意化。
CREATE UNIQUE INDEX IF NOT EXISTS inventories_owner_uq
  ON inventories (owner_type, owner_id);
```

**(d) actor_runtime_states（新規・付録C / MVP 13章）**:
```sql
CREATE TABLE IF NOT EXISTS actor_runtime_states (
    actor_id   UUID PRIMARY KEY,
    world_id   UUID NOT NULL,
    version    BIGINT NOT NULL,
    payload    JSONB NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS actor_runtime_states_world_idx ON actor_runtime_states (world_id);
```

**(e) actor_state_projections（新規・WorldState Consumer 用・投影専用）**:
```sql
CREATE TABLE IF NOT EXISTS actor_state_projections (
    actor_id           UUID PRIMARY KEY,
    world_id           UUID NOT NULL,
    projection_version BIGINT NOT NULL,
    payload            JSONB NOT NULL,
    rebuilt_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
```
（M2 では API は書かない。将来 WorldState が Writer。テーブルだけ用意。）

**(f) item_definitions（新規・Item Definition マスタ・MVP 7.2）**:
```sql
CREATE TABLE IF NOT EXISTS item_definitions (
    item_definition_id TEXT PRIMARY KEY,
    tags               TEXT[] NOT NULL DEFAULT '{}',
    stack_limit        INTEGER NOT NULL,
    weight_milli       INTEGER NOT NULL,   -- 重量は float 回避のため 1/1000 単位の整数
    rarity             INTEGER NOT NULL DEFAULT 0,
    base_value         BIGINT NOT NULL DEFAULT 0,  -- 通貨は BIGINT（MVP 13.1）
    use_effect         JSONB NOT NULL DEFAULT '{}'::jsonb
);
```

> `.down.sql` は上記の DROP / ALTER 逆操作を記述（`event_id` の型戻し含む）。

### 3.8 Item Definition マスタ供給（MVP 7.2）

- 正本データを `services/api/data/item_definitions.json` に置く（18定義, MVP 7.2 表を厳密反映）。A（Windows）はこれを SO へ取り込む（05A 3章）。**同じ正本を両側が使う**ことで A/B の定義ズレを防ぐ。
- 重量は **milli 整数**（`weight_milli`, 例 `1.0`→`1000`）で保持し float を避ける（通貨・数量規約に合わせる。落とし穴6.3）。rarity は 0=common..3=epic（基本設計 6.1）。
- API 起動時に `internal/itemdef` が JSON を読み、`item_definitions` へ seed（`ON CONFLICT DO UPDATE`）するか、メモリ保持で `AppendEvents`/検証に使う。**quantity/quality/durability を Client 入力から採用しない**（MVP 7.3 / MVP-SEC-006）ための参照元。

18定義（MVP 7.2, `item_definition_id | tags | stack | weight | rarity`）:
```text
stone(resource.stone,50,1.0,0), iron_ore(resource.ore.iron,30,1.5,0),
rare_ore(resource.ore.rare,10,1.5,2), wood(resource.wood,30,0.8,0),
iron_ingot(material.ingot.iron,20,1.2,0), rare_ingot(material.ingot.rare,10,1.2,2),
leather(material.leather,20,0.5,0), bone(material.bone,20,0.5,0),
stone_spear(weapon.hunting.basic,1,3.0,0), raw_meat(food.raw.meat,10,1.0,0),
rare_meat(food.raw.meat.rare,5,1.0,2), cooked_meat(food.cooked.meat,10,0.8,0),
food_waste(waste.food,20,0.3,0), stone_pickaxe(tool.mining,1,4.0,0),
iron_hunting_spear(weapon.hunting,1,5.0,0), luxury_food(food.luxury,5,0.8,2),
decorative_weapon(asset.luxury.weapon,1,6.0,2), rare_weapon(weapon.rare,1,5.0,3)
```
（cooked_meat/luxury_food は `use_effect` に Hunger+30。luxury_food は waste x2。MVP 7.2/8.1/8.6。）

---

## 4. 実装順序表（WSL2側）

| # | タスク | 完了確認 |
|---|---|---|
| L2-1 | `go.work` または `replace` で api → gen module 参照を確立、`google.golang.org/grpc` 追加 | `go build ./...` 緑 |
| L2-2 | migrations 0002 作成（3.7 a〜f）＋ `make migrate` | 追加テーブル/索引/制約が作成される |
| L2-3 | `internal/store`（snapshot/events/inventory/actor）DB アクセス実装 | 単体テストで CRUD 確認 |
| L2-4 | `WorldData.LoadBootstrap` 実装（3.2） | Snapshot+tail を sequence 昇順で返す |
| L2-5 | `WorldData.AppendEvents` 実装（3.3・event_id dedup・sequence API採番） | 再送が DUPLICATE、新規は OK、競合は CONFLICT |
| L2-6 | `WorldData.SaveSnapshot` 実装（3.4・staging→checksum→active） | checksum 不一致を拒否、active 切替が原子的 |
| L2-7 | `ActorState.Save` 実装（3.5・version 単調 upsert） | 古い version が無視される |
| L2-8 | gRPC サーバーを `apid` に結線（3.1・別ポート・graceful stop） | `grpcurl`/クライアントで疎通 |
| L2-9 | Outbox relay 実装（3.6・≤1秒 poll・published_at 確定） | outbox 行が NATS へ流れ published_at が入る |
| L2-10 | Item Definition マスタ JSON＋読込/seed（3.8） | 18定義が検証・供給できる |
| L2-11 | ユニット/結合テスト（5章）＋ `make ci` | 全緑 |
| L2-12 | `buf generate` の drift 検査（proto 未変更でも実行） | `git diff --exit-code` 緑 |

---

## 5. テスト・受入（WSL2側）

### 5.1 ユニット/結合テスト（Go）

- `AppendEvents` **冪等性**（AT-003 / AT-019 の永続側）:
  - 同一 `event_id` を2回送る → 1回目 OK・2回目 DUPLICATE。`domain_events` は1行のみ。
  - 並行 `AppendEvents` で `sequence` が連番かつ一意（`UNIQUE(world_id, sequence)` 違反が CONFLICT で返る）。
- `SaveSnapshot` :
  - 正しい checksum → 保存＆`worlds.active_snapshot_id` 更新。
  - 改竄 payload（checksum 不一致）→ 拒否、active 不変。
  - 同一 `(world_id, sequence)` 再送 → 冪等（既存 snapshot_id）。
- `LoadBootstrap` :
  - active Snapshot（sequence=N）＋ sequence>N の events → payload と tail を昇順で返す。
  - active 無し（新規 world）→ 空 payload・sequence=0・tail は全 events。
- `ActorState.Save` : version 逆行を無視、前進を反映。
- **DB 結合**: `make up`（postgres）＋ `make migrate`（0001+0002）を張った状態で pgx 実接続テスト（M0 の Go test 方針に合わせ `-race -count=1`）。
- Outbox relay: 未公開行を投入 → relay 後に `published_at` が入り NATS へ届く（テスト用 NATS or embedded）。

### 5.2 受入トレーサビリティ（MVP 18章）

| AT | M2(B) の関与 | 確認 |
|---|---|---|
| AT-002 | Cache 削除後の復元は Server 正本から | `LoadBootstrap`＋Inventory 永続が復元源であること |
| AT-003 | 二重送信で1回分だけ変化 | `AppendEvents` の event_id dedup |
| AT-018 | Server 再起動で World/AI 復元 | `LoadBootstrap`（Snapshot+tail）＋`actor_runtime_states` |
| AT-019 | 購入応答直後 crash 後も保持 | 永続 Writer=API（Inventory/通貨は Tx 確定済み） |
| AT-021 | 購入時 Inventory 二重 Write 防止 | 永続は API のみ。DS は runtime のみ（本書は API 側担保） |

> 購入 Tx 本体（`Economy.CommitPurchase`）は **M6** スコープ。M2 では Inventory の永続基盤（`inventory_entries`/`item_instances`）と単一 Writer の土台を用意する。

### 5.3 `make ci`

- `make ci`（proto/lint/test）が緑。`gofmt`/`go vet`/`golangci-lint`/`go test ./... -race` を満たす。
- 通貨・重量に float を混入させない（`weight_milli`/BIGINT。静的解析で警戒。03B 5.2）。

---

## 6. 落とし穴（WSL2側）

1. **sequence を DS に採番させない**。永続 `sequence` の Writer は API のみ（MVP 13.1）。DS が送るのは `event_id`＋`local_sequence` まで。API 側で採番・確定する。
2. **domain_events の Writer を増やさない**。WorldState は Consumer（付録C）。M2 で API 以外の書込経路を作らない。
3. **float 混入禁止**。重量は `weight_milli`（整数）、通貨は BIGINT。JSON マスタも整数で持つ（3.8）。
4. **checksum アルゴリズムを A と一致**させる。`SaveSnapshot` の検証と DS の生成が別式だと常に不一致で保存不能になる（例: 両者 SHA-256 hex・対象バイト列の定義も一致させる）。05A と突き合わせる。
5. **event_id の型不整合**。proto は ULID(文字列)、0001 の PK は UUID。migrations 0002 で TEXT 化して UNIQUE 維持（3.7a）。放置すると ULID を UUID へ変換できず INSERT 失敗、または dedup が壊れる。
6. **Outbox の主体を取り違えない**。DS の Event Outbox flush（≤1秒）と 30秒 Snapshot の**タイマーは A**。B は受け口（AppendEvents/SaveSnapshot）と **API 自身の** outbox relay を担う。
7. **staging→active を分離 Tx にしない**。SaveSnapshot は保存と active 切替を1 Tx で。分けると中途半端 active が発生する。
8. **`unity/` を触らない**。C# 生成物 `unity/SurvivalWorld/Assets/Generated/` は `buf generate` の出力のみ。手編集や他ファイル変更は A の領域。
9. **migrations は追記**。0001 を書き換えず 0002 を足す。`.down.sql` を必ず用意し `migrate down` で戻せること。
10. **gRPC ポート衝突**。HTTP(8082) と gRPC(8092) を分離。compose/`.env.example` に両方を定義し、DS(A) の接続先と一致させる。

---

## 参考資料

- MVP詳細設計 v0.2.2: 7章（Inventory/Item Definition/Command）, 12.1（World Bootstrap/Save）, 12.2.1（単一Writer原則）, 13章（DB論理設計/Index）, 14.2（WorldData/ActorState gRPC）, 16章（復旧）, 付録C（データ所有権）。
- 基本設計 v0.2.1: 6.1（共通Inventory・単一Writer）, 9.2（保存単位と復旧）, 9.4（データ所有権マトリクス）。
- proto: `proto/survival/v1/{common,worlddata,gameplay}.proto`（本書は既存型を実装対象として参照）。
- M0: `03B_M0実装指示書_WSL2側_v0.1.md`（Makefile/scripts/buf/migrate/CI）。
- [R10] PostgreSQL JSON types / GIN。[R11] Range Partition。[R8] NATS JetStream。[R-MIGRATE] golang-migrate。
