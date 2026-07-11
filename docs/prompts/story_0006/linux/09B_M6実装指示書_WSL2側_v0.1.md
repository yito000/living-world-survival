---
title: "M6 実装指示書（WSL2 / Linux 側）"
subtitle: "Economy：EconomyService（購入/売却の単一Tx）・Buyer在庫生成・ランキングBatch・NATS経済イベント"
document_id: "IMPL-M6-WSL-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M6 / WSL2側）"
baseline: "Go / PostgreSQL / NATS JetStream / buf / golang-migrate"
related_document: "09A_M6実装指示書_Windows側_v0.1.md, 08A_M5実装指示書_Windows側_v0.1.md, 08B_M5実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M6 実装指示書：WSL2 / Linux 側 v0.1

本書は M6（Economy）の作業を **WSL2（Ubuntu）側**（Go の `api` サービス、proto、DB マイグレーション、NATS、ローカルCI）に限定して指示する。Unity Dedicated Server 側（Buyer spawn / 購入コマンド処理 / Runtime 反映 / AI 購入行動 / Buyer Kit 配置）は別冊 **09A（Windows側）** を参照。

M6 の成果（MVP 第19章）: **Buyer、有限Stock、購入Transaction、AI購入、ランキング**。本書の中心は「**永続 Writer である API が購入・売却・在庫・台帳を単一トランザクションで確定する**」ことである。

---

## 0. 分担と連携（要点／詳細は 03A/03B 第0章）

環境別責務・リポジトリ配置・Git/LFS/改行コード規約・境界成果物の全文は **M0 の 03A/03B 第0章（両冊同一）** を正とする。M6 で関係する要点のみ再掲する。

| 領域 | 担当 | M6 での具体 |
|---|---|---|
| Go `api`（EconomyService, ランキングBatch） | **WSL2** | 本書。gRPC サーバを `apid` に追加 |
| proto 生成（buf） | **WSL2**（生成） | `economy.proto` に Buyer 登録系 RPC を追加し Go/Python/C# を再生成 |
| DB マイグレーション | **WSL2** | `buyer_instances` / `buyer_stock` / `asset_rankings`（0002） |
| NATS `world.{id}.event.economy` 発行 | **WSL2** | 購入/売却/Buyer出現・消滅を発行 |
| Unity DS（Buyer spawn/購入/Runtime反映/AI購入/Kit配置） | **Windows** | 別冊 09A |

- **競合回避**: WSL2 側は `services/`, `proto/`, `infra/`, `assets-pipeline/`, `scripts/*.sh`, `Makefile` のみを編集する。`unity/`（`Assets/Generated/` を含む C# 出力先を除く）は触らない。C# 生成物は `buf generate` で `unity/SurvivalWorld/Assets/Generated/` に出力され、Windows 側 Unity が消費する（03B 0.4）。
- **通貨は必ず BIGINT（整数・最小通貨単位）**。float 禁止。`.golangci.yml` の forbidigo が `float32/float64` を検出する（MVP 13.1）。金額の乗算（modifier 適用）も整数演算で行う（本書 3.4）。
- **単一 Writer 原則**: 購入時の `inventory_entries` / `item_instances` / `currency_ledger` の永続確定は **API のみ**が行う。DS は結果を Runtime に映すだけで再永続化しない（MVP 12.2.1・付録C）。

### 0.1 M6 の境界成果物（環境をまたぐファイル）

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| `economy.proto` の C# 生成コード（RegisterBuyer 等の追加を含む） | WSL2（`buf generate`） | Windows（Unity DS） | `unity/SurvivalWorld/Assets/Generated/` |
| `economy.proto` の Go 生成コード | WSL2 | WSL2（`api`） | `services/gen/go/survival/v1/` |
| `buyer_instances` / `buyer_stock` / `asset_rankings` マイグレーション | WSL2 | 参照: DS（表示・復元） | `services/api/migrations/` |
| Economy gRPC エンドポイント（`api` が公開） | WSL2 | Windows（DS がクライアント） | 内部ネットワーク（TLS・MVP-SEC-007） |

---

## 1. 対象と前提（WSL2側）＋ M6 DoD

- 環境: WSL2（Ubuntu）+ Docker Desktop（03B 1章）。リポジトリは `/mnt/c/dev/living-world-survival`。
- 本書完了で、`api` が **EconomyService（CommitPurchase / CommitSale / RegisterBuyer / DespawnBuyer）** を gRPC で公開し、Buyer 在庫が seed から決定的に生成され、1時間ごと/管理コマンドで資産ランキングが計算され、経済イベントが NATS へ発行される状態にする。

### 1.1 WSL2側 M6 DoD

- `economy.proto` に Buyer 登録/消滅 RPC を追加し、`make proto` が Go/Python/C# を再生成しドリフト検査に通る。
- マイグレーション 0002 で `buyer_instances` / `buyer_stock` / `asset_rankings` が作成され、`purchase_transactions` に冪等リプレイ用カラムが追加される（`make migrate` 緑）。
- `api` の gRPC サーバが起動し、`EconomyService.CommitPurchase` が **単一トランザクション**で在庫ロック→検証→在庫減算→台帳/個体/インベントリ/取引を確定する（MVP 12.2）。
- **冪等性**: 同一 `idempotency_key` の再送で二重確定せず、以前の `PurchaseResult` を返す（AT-019 準拠の再現）。
- **同時購入**: 同一 `stock_entry_id` を2者が同時購入すると1者のみ成功し、残高/在庫/アイテムに不整合が出ない（AT-012）。
- **在庫枯渇 / 残高不足 / Buyer 非 active** で正しいステータス（`OUT_OF_STOCK` / `INSUFFICIENT_FUNDS` / `REJECTED`）を返す。
- Buyer 在庫が `inventory_table_id` + `seed` から**決定的**に生成され、**有限・重み付き抽選・レア保証なし**（付録B.3）を満たす。
- ランキングBatch が全 Character/AI の `net_worth = 現金 + Item評価額 + 設備評価額` を計算し、`price_version` と `calculated_at` を保存する（MVP 12.3）。
- 購入/売却/Buyer出現・消滅が `world.{id}.event.economy` へ発行される（MVP 14.3）。
- Go テスト（冪等性・在庫枯渇・残高不足・同時購入・在庫生成の決定性）が緑（`make ci`）。
- 金額・数量に float を使わない（forbidigo 緑）。

---

## 2. 前提成果物（M0〜M5・すでに構築済み）

| 由来 | 前提 | M6 での扱い |
|---|---|---|
| M0 | `proto/survival/v1/economy.proto`（`EconomyService.CommitPurchase` / `CommitSale`、`PurchaseStatus`）、`common.proto`（`Money`, `ItemRef`, `ResultStatus`）、`gameplay.proto`（`BuyerPurchaseCommand`, `RequestInventorySnapshot`） | 本書で拡張・実装 |
| M0 | migrations 0001：`item_instances` / `inventories` / `inventory_entries` / `currency_ledger` / `purchase_transactions`（`idempotency_key` UNIQUE）/ `outbox_messages` / `inbox_dedup` | 購入Txで再利用。`buyer_stock` は**未作成**（本書 0002 で追加） |
| M0 | `apid`（HTTP `/healthz` `/readyz` のみ、`pgxpool` + NATS 接続、`GOOS=linux` ビルド） | gRPC サーバを追加 |
| M0 | `.golangci.yml`（forbidigo で float 監視）、`services/gen/go/survival/v1/economy_grpc.pb.go` | そのまま利用 |
| M1 | Auth / Join Ticket / FishNet 接続 | 購入の purchaser（Character）が確定している前提 |
| M2 | 共通 Inventory / Item Definition / World Load-Save（`inventories.version`） | `inventory_version` の突き合わせ元 |
| M3 | 採掘・製作・狩猟・料理（Item Definition の `base_price` / `sell_price` / 設備評価額の元データ） | 在庫の item_tag→item_definition 解決、評価額算出の元 |
| M4 | PersonalState / Template Runner（`economy.visit_buyer` / `economy.sell_surplus` テンプレ） | AI 購入は DS 側（09A）。API は Player/AI を区別せず同一 API |
| M5 | WorldState Projection（`economy_state`：供給量/購入数/売切れ時間/資産分布/価格Version）、NATS Consumer | `world.{id}.event.economy` の購読者。API は発行のみ |

> **Item Definition の価格データ**: `base_price`（購入基準額）と `sell_price`（売却額）は M2/M3 で定義済みの Item Definition（Definition Data）を正とする。M6 で新設しない。設備評価額は設備 Definition の `asset_value` を用いる。値が未定義のものは 0 として扱い、ランキングに影響しないこと。

---

## 3. 実装対象（WSL2側）

`api` サービス（Go, module `living-world-survival/services/api`）に **gRPC サーバ**と **Economy ドメイン**を実装する。配置方針:

```text
services/api/
├─ cmd/apid/main.go            # 既存HTTPに加え gRPC リスナを起動（3.1）
├─ internal/
│  ├─ economy/
│  │  ├─ service.go            # EconomyServiceServer 実装（CommitPurchase/CommitSale/RegisterBuyer/DespawnBuyer）
│  │  ├─ purchase.go           # 単一Tx（12.2）
│  │  ├─ sale.go               # 売却Tx
│  │  ├─ stock.go              # Buyer在庫の決定的生成（B.3 重み付き抽選）
│  │  ├─ pricing.go            # 整数価格演算（modifier適用・float禁止）
│  │  └─ events.go             # world.{id}.event.economy 発行（Outbox経由）
│  ├─ ranking/
│  │  └─ batch.go              # net_worth 計算Batch（12.3）
│  └─ store/                   # pgx クエリ（buyer_stock/currency_ledger 等）
└─ migrations/
   ├─ 0002_economy.up.sql      # buyer_instances / buyer_stock / asset_rankings ほか
   └─ 0002_economy.down.sql
```

### 3.1 gRPC サーバの追加（`apid`）

M0 の `apid` は HTTP のみ。M6 で **同一プロセスに gRPC サーバ**を追加する。

- 環境変数 `API_GRPC_PORT`（既定 `9092`）で listen。HTTP（`API_PORT=8082`）とは別ポート。
- `google.golang.org/grpc` を導入し、`economypb.RegisterEconomyServiceServer(grpcServer, economy.NewService(pool, natsPublisher))` を登録。
- 内部RPCは TLS + サービス認証（MVP-SEC-007）。dev はローカルなら plaintext 可、compose/本番構成では TLS。Client（DS）からのみ到達可能な内部ネットワークに置く（MVP-SEC-001）。
- graceful shutdown を既存のシグナルハンドリングに合わせる（`grpcServer.GracefulStop()`）。
- `.env.example` に `API_GRPC_PORT` を追記。`infra/docker-compose.yml` の `api` に `9092` を expose。

### 3.2 proto 追加（`economy.proto`）

Buyer の**登録（spawn）と消滅（despawn）**は DS（世界権威）が起点だが、`buyer_instances` / `buyer_stock` の永続 Writer は **API**（付録C）。よって DS→API の登録/消滅 RPC を `EconomyService` に追加する。既存の `CommitPurchase` / `CommitSale` は変更しない。

```proto
service EconomyService {
  rpc CommitPurchase(CommitPurchaseRequest) returns (CommitPurchaseResponse);
  rpc CommitSale(CommitSaleRequest) returns (CommitSaleResponse);
  // M6 追加：DS が seed/出現時刻を採番して登録、API が在庫を決定的に生成し確定。
  rpc RegisterBuyer(RegisterBuyerRequest) returns (RegisterBuyerResponse);
  // M6 追加：despawn 準備／完了。準備で新規購入を拒否、完了で残在庫を締める。
  rpc DespawnBuyer(DespawnBuyerRequest) returns (DespawnBuyerResponse);
}

message RegisterBuyerRequest {
  string idempotency_key = 1;     // (world_id, buyer 連番) から DS が決定的に生成
  string world_id = 2;
  string region_id = 3;
  int64 seed = 4;                 // DS 採番。在庫生成を決定的にする
  string inventory_table_id = 5;  // 付録B.3 の table（例 rare_weapon_buyer_v1）
  int32 price_modifier_bp = 6;    // buyer_modifier をベーシスポイント整数で（10000=×1.0）
  int64 spawn_at_unix_ms = 7;
  int64 despawn_at_unix_ms = 8;
}
message BuyerStockEntry {
  string stock_entry_id = 1;
  string item_definition_id = 2;
  int64 unit_price = 3;           // 整数（最小通貨単位）
  int32 remaining_quantity = 4;
  int64 version = 5;
}
message RegisterBuyerResponse {
  string buyer_instance_id = 1;
  repeated BuyerStockEntry stock = 2;
}

message DespawnBuyerRequest {
  string buyer_instance_id = 1;
  // PREPARING：新規購入拒否・開始済Txのみ完了 / DESPAWNED：締め
  string target_status = 2;
}
message DespawnBuyerResponse {
  ResultStatus status = 1;
}
```

- `price_modifier_bp` は **basis point 整数**（float 禁止のため）。`world_modifier` も同様に整数で `pricing.go` に定数化（3.4）。
- proto を変更したら **`make proto`** を実行し、Go/Python/C# を再生成、`git diff --exit-code`（`unity/.../Generated` を含む）でドリフトを検出（03B 5.4）。C# 生成物のコミット漏れが最頻の CI 失敗要因。
- `buf breaking` は既存 RPC を壊さないこと（追加のみ）を確認。

### 3.3 マイグレーション 0002（`services/api/migrations/0002_economy.*.sql`）

golang-migrate 形式で追加する。**通貨は BIGINT**、`idempotency_key` は UNIQUE、`buyer_stock` は `(buyer_instance_id, remaining_quantity)` 索引・version 条件更新（MVP 13.1）。

`0002_economy.up.sql`（要点）:

```sql
-- Buyer 個体（Owner=API, MVP 13）。status: spawning / active / preparing / despawned
CREATE TABLE IF NOT EXISTS buyer_instances (
    buyer_instance_id  UUID PRIMARY KEY,
    world_id           UUID NOT NULL,
    region_id          TEXT NOT NULL,
    seed               BIGINT NOT NULL,
    inventory_table_id TEXT NOT NULL,
    price_modifier_bp  INTEGER NOT NULL DEFAULT 10000,  -- 10000 = ×1.0
    spawn_at           TIMESTAMPTZ NOT NULL,
    despawn_at         TIMESTAMPTZ NOT NULL,
    status             TEXT NOT NULL DEFAULT 'active',
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS buyer_instances_world_status_idx
    ON buyer_instances (world_id, status);

-- Buyer 在庫（Owner=API）。unit_price は整数。remaining_quantity と version で楽観制御。
CREATE TABLE IF NOT EXISTS buyer_stock (
    stock_entry_id     UUID PRIMARY KEY,
    buyer_instance_id  UUID NOT NULL REFERENCES buyer_instances (buyer_instance_id) ON DELETE CASCADE,
    item_definition_id TEXT NOT NULL,
    unit_price         BIGINT NOT NULL,
    remaining_quantity INTEGER NOT NULL,
    version            BIGINT NOT NULL DEFAULT 0,
    CONSTRAINT buyer_stock_qty_nonneg CHECK (remaining_quantity >= 0)
);
CREATE INDEX IF NOT EXISTS buyer_stock_buyer_remaining_idx
    ON buyer_stock (buyer_instance_id, remaining_quantity);

-- 資産ランキング（Owner=Batch, MVP 13）。net_worth は BIGINT。
CREATE TABLE IF NOT EXISTS asset_rankings (
    rank_id       UUID PRIMARY KEY,
    owner_id      UUID NOT NULL,
    net_worth     BIGINT NOT NULL,
    price_version BIGINT NOT NULL,
    calculated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS asset_rankings_run_idx
    ON asset_rankings (price_version, net_worth DESC);

-- 冪等リプレイ用：以前の PurchaseResult を復元できるよう結果を保存（12.2）。
ALTER TABLE purchase_transactions
    ADD COLUMN IF NOT EXISTS stock_entry_id         UUID,
    ADD COLUMN IF NOT EXISTS item_instance_ids      UUID[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS granted_definition_ids TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS new_inventory_version  BIGINT NOT NULL DEFAULT 0;
```

`0002_economy.down.sql` は上記の逆（`asset_rankings` / `buyer_stock` / `buyer_instances` DROP、`purchase_transactions` の追加カラム DROP）。

> `purchase_transactions` は M0 0001 で `purchase_id / idempotency_key(UNIQUE) / buyer / purchaser / amount / status / created_at` を持つ。冪等再送で `PurchaseResult` を完全復元するため、確定した `stock_entry_id` / 付与 `item_instance_ids` / `granted_definition_ids` / `new_inventory_version` を保存する。

### 3.4 価格演算（`pricing.go`・float 厳禁）

価格式は **base_price × buyer_modifier × world_modifier**（MVP 8.7）。float を使わず basis point（分母 10000）整数で計算し、**在庫生成時に確定して `unit_price`（BIGINT）に保存**する（購入時は再計算しない）。

```go
// world_modifier もベーシスポイント整数（設定値。既定 10000 = ×1.0）。
const worldPriceModifierBP int64 = 10000

// unitPrice = basePrice * buyerBP/10000 * worldBP/10000（切り捨て）。
func computeUnitPrice(basePrice, buyerBP, worldBP int64) int64 {
    p := basePrice * buyerBP / 10000
    p = p * worldBP / 10000
    if p < 1 {
        p = 1 // 最低価格 1（無償化防止）
    }
    return p
}
```

- **丸めは切り捨て（floor）で統一**。オーバーフロー防止のため `int64` を維持（basis point の中間積が `int64` 範囲に収まることをテストで確認）。
- forbidigo が `float32/float64` を検出するので、金額計算にそれらを持ち込まない。座標等でどうしても必要な箇所のみ `//nolint:forbidigo` を明示（本ドメインでは不要）。

### 3.5 Buyer 在庫の決定的生成（`stock.go`・付録B.3）

`RegisterBuyer` 内で、`inventory_table_id` の定義（付録B.3）と `seed` から**決定的**に在庫を生成する。**有限・重み付き抽選・レア保証なし**。

在庫テーブル定義（付録B.3・`assets-pipeline/` もしくは `services/api/internal/economy/tables/*.json` に配置し埋め込み）:

```json
{
  "inventory_table_id": "rare_weapon_buyer_v1",
  "slot_count_min": 4,
  "slot_count_max": 8,
  "entries": [
    { "item_tag": "weapon.common",   "weight": 60 },
    { "item_tag": "weapon.uncommon", "weight": 25 },
    { "item_tag": "weapon.rare",     "weight": 8  },
    { "item_tag": "blueprint.rare",  "weight": 2  }
  ],
  "guaranteed_rare_slots": 0
}
```

生成アルゴリズム（決定的・MVP 8.7 / 付録B.3）:

1. `rng := 決定的PRNG(seed)`（例 `math/rand.New(math/rand.NewSource(seed))`。**乱数は float を使わない**整数APIで抽選）。同一 `seed` は同一在庫を再現。
2. `slot_count = slot_count_min + rng.Intn(slot_count_max - slot_count_min + 1)`（4〜8）。
3. 各 slot について、`entries` の `weight` 合計に対する**重み付き抽選**で `item_tag` を1つ選ぶ（`rng.Int63n(total_weight)` の累積比較。float 化しない）。
4. 選ばれた `item_tag` を Item Definition（M2/M3）から解決し、該当タグの Definition を（seed 由来で決定的に）1つ選び `item_definition_id` を確定。
5. `remaining_quantity = 1 + rng.Intn(5)`（各1〜5・MVP 8.7）。
6. `unit_price = computeUnitPrice(base_price, price_modifier_bp, worldPriceModifierBP)`（3.4）。
7. **`guaranteed_rare_slots = 0`**：レアが1つも並ばない回を許容する。**レアを後から強制挿入してはならない**（AT-011 / AT-017）。

- **決定性の担保**: 同じ `(inventory_table_id, seed, price_modifier_bp)` で常に同一の `(item_definition_id, unit_price, remaining_quantity)` 列を生成する（`stock_entry_id`（UUID）だけは新規発番でよい）。テストで検証（5章）。
- Rare Buyer Rush（MVP 10.3 / AT-017）は 3 体分をそれぞれ独立 seed で `RegisterBuyer` する（在庫は独立・Rare保証なし）。API 側は Buyer の種別を区別せず同じ生成経路を通す。

### 3.6 CommitPurchase（単一トランザクション・MVP 12.2 / 12.2.1）

`CommitPurchaseRequest{ idempotency_key, buyer_instance_id, stock_entry_id, purchaser_id, inventory_version }` を **1 つの DB トランザクション**で確定する。

処理順（MVP 12.2 の SQL に忠実に）:

```text
BEGIN;
1. 冪等チェック：SELECT * FROM purchase_transactions WHERE idempotency_key = :key;
     ヒット → COMMIT せず、保存済み結果から PurchaseResult を復元して返す（status = DUPLICATE）。
2. 在庫行ロック：
     SELECT bs.remaining_quantity, bs.unit_price, bs.item_definition_id, bs.version, bi.status
       FROM buyer_stock bs JOIN buyer_instances bi ON bi.buyer_instance_id = bs.buyer_instance_id
      WHERE bs.stock_entry_id = :id FOR UPDATE;   -- 行ロック（同時購入で1件のみ成功）
3. 検証：
     - 行が無い → REJECTED
     - bi.status != 'active' → REJECTED（despawn 準備後は新規購入拒否）
     - remaining_quantity <= 0 → OUT_OF_STOCK
     - inventory_version != inventories.version(購入者) → REJECTED（version 不一致）
     - 現在残高 - unit_price < 0 → INSUFFICIENT_FUNDS
4. 在庫減算（version 条件更新）：
     UPDATE buyer_stock SET remaining_quantity = remaining_quantity - 1, version = version + 1
       WHERE stock_entry_id = :id AND version = :expected_version;   -- 0行なら競合→ROLLBACK/再取得
5. 通貨台帳：
     INSERT INTO currency_ledger(entry_id, owner_id, delta, balance_after, reason, correlation_id)
       VALUES(:e, :purchaser, -:unit_price, :new_balance, 'purchase', :purchase_id);
6. Item 個体生成（品質/耐久を持つ個体の場合）：
     INSERT INTO item_instances(item_instance_id, definition_id, quality, durability, attributes) ...;
7. インベントリ確定（永続 Writer=API）：
     INSERT INTO inventory_entries(inventory_id, slot_index, item_definition_id, item_instance_id, quantity, ...) ...;
     UPDATE inventories SET version = version + 1 WHERE inventory_id = :inv;  -- new_persisted_inventory_version
8. 取引記録（冪等キー UNIQUE）：
     INSERT INTO purchase_transactions(purchase_id, idempotency_key, buyer, purchaser, amount, status,
         stock_entry_id, item_instance_ids, granted_definition_ids, new_inventory_version)
       VALUES(...);   -- UNIQUE 違反時は競合再送 → 既存結果を返す
9. Outbox へ経済イベントを積む（3.8）：同一Txで outbox_messages に INSERT。
COMMIT;
```

- 返却は `CommitPurchaseResponse{ status, granted_items[], item_instance_ids[], new_persisted_inventory_version, charged }`。`charged.amount = unit_price`（`Money`, 整数）。
- **残高取得**: `currency_ledger` の当該 `owner_id` の最新 `balance_after`（無ければ初期残高＝ M1/M2 で付与される初期通貨。無ければ 0）。`SELECT ... ORDER BY created_at DESC, entry_id DESC LIMIT 1 FOR UPDATE` 相当で口座も直列化する（同一購入者の並行購入での二重使用を防ぐ・MVP-SEC-009）。
- **購入者インベントリ**: `inventories WHERE owner_type IN ('character','ai') AND owner_id = :purchaser`。version の突き合わせと更新をここで行う（12.2.1 の `new_persisted_inventory_version`）。
- **冪等キー衝突の扱い**（12.2）: ステップ1の事前 SELECT に加え、ステップ8で UNIQUE 違反が起きた場合（並行同一キー）も **既存行を再読込して同じ結果を返す**。二重に台帳/在庫を動かさない。
- **満杯インベントリ**（AT-004 相当）: 空き slot が無ければ `REJECTED`（在庫・台帳を動かさずロールバック）。
- Player と AI を区別しない（AT-013）。purchaser_id が Character でも AI でも同一経路。

### 3.7 CommitSale（売却・単一Tx）

`CommitSaleRequest{ idempotency_key, buyer_instance_id, seller_id, items[] }` を単一 Tx で確定する。

- 冪等チェック（`idempotency_key`。売却も `purchase_transactions` 相当の冪等が必要なため、専用の冪等記録を持つか `purchase_transactions.status='sale'` を流用。**設計簡素化のため sale 用に同テーブルへ `status='sale'` 行を記録**し `idempotency_key` UNIQUE を共有）。
- `items[]` の各 `ItemRef` を売却額（Item Definition の `sell_price`、整数）で評価し合計 `proceeds` を算出（**整数合算**）。
- `inventory_entries` から対象を減算（永続 Writer=API）、`item_instances` は消費（個体なら削除/無効化）、`currency_ledger` に `+proceeds`、`inventories.version` を +1。
- 返却 `CommitSaleResponse{ status(ResultStatus), proceeds(Money), new_persisted_inventory_version }`。
- 対象が存在しない/所有していない場合は `CONFLICT`／`REJECTED`。DS 経由のみが売却でき、Client 入力の価格は採用しない（MVP-SEC-006）。

### 3.8 NATS 経済イベント発行（`events.go`・MVP 14.3）

Subject `world.{world_id}.event.economy` に発行する。**Outbox パターン**で確定と発行を分離（At-least-once・03B 10章 / MVP 16 NATS切断）。

- 発行契機と payload（JSON）:
  - **購入確定**: `{ "type":"purchase", "purchase_id", "buyer_instance_id", "stock_entry_id", "purchaser", "item_definition_id", "amount", "remaining_quantity" }`
  - **売却確定**: `{ "type":"sale", "seller", "buyer_instance_id", "proceeds", "item_definition_ids":[...] }`
  - **Buyer 出現**: `{ "type":"buyer_spawned", "buyer_instance_id", "region_id", "stock_count", "despawn_at" }`（RegisterBuyer 成功時）
  - **Buyer 消滅**: `{ "type":"buyer_despawned", "buyer_instance_id" }`（DespawnBuyer 完了時）
- 実装: 購入/売却/登録/消滅の各 Tx 内で `outbox_messages(topic='world.{id}.event.economy', payload)` を INSERT し、別 goroutine（Outbox Publisher）が未発行行を JetStream へ publish→`published_at` 更新。**ゲーム Tick をブロックしない**。
- 金額は payload でも整数（JSON number, 小数化しない）。消費者は WorldState `economy_state`（MVP 10.1 / M5）。

### 3.9 ランキング Batch（`ranking/batch.go`・MVP 12.3）

- 起動: **1時間ごと（ticker）** または **管理コマンド**（`apid` の内部 HTTP `POST /admin/ranking/run` もしくは gRPC 管理RPC）で全 Character/AI を対象に実行。
- 計算式: `net_worth = 現金 + Item評価額 + 設備評価額`（すべて整数）。
  - **現金**: `currency_ledger` の owner ごとの最新 `balance_after`。
  - **Item評価額**: 所有 `inventory_entries` × Item Definition の評価額（`sell_price` 等）を整数合算。個体（`item_instances`）は品質/耐久補正を整数で反映してよい。
  - **設備評価額**: 所有設備 Definition の `asset_value` を整数合算。
- 保存: `asset_rankings(rank_id, owner_id, net_worth, price_version, calculated_at)`。**同一実行は同一 `price_version`**（実行ごとに単調増加する版番号。評価に用いた価格表バージョン）で全行を書く。`calculated_at` は実行時刻。
- MVP では Client 公開必須ではない（API/DB で結果確認できればよい）。過去実行は残す（`price_version` で世代管理）。
- 実行は重い処理になり得るので request handler 内で完結させず、専用 goroutine/バッチで実行（BSD R7 / MVP 10.2 の非同期方針に準拠）。

---

## 4. 実装順序（WSL2側）

| # | タスク | 完了確認 |
|---|---|---|
| L-1 | `economy.proto` に `RegisterBuyer`/`DespawnBuyer` と関連 message を追加（3.2） | `buf lint` 緑 |
| L-2 | `make proto`（Go/Python/C# 再生成 + drift 検査） | `git diff --exit-code` 緑、C# が `unity/.../Generated` に出力 |
| L-3 | migration 0002（buyer_instances/buyer_stock/asset_rankings + purchase_transactions 拡張）＋ `make migrate` | テーブル作成、`down` で逆適用可 |
| L-4 | `apid` に gRPC サーバ追加（3.1）、`.env.example`/compose に `API_GRPC_PORT`（9092） | gRPC listen、health 維持 |
| L-5 | `pricing.go`（整数価格演算） | 単体テスト緑・forbidigo 緑 |
| L-6 | `stock.go`（B.3 決定的生成） | 決定性テスト緑（同 seed→同在庫） |
| L-7 | `RegisterBuyer`/`DespawnBuyer` 実装（在庫生成→永続→出現イベント） | 統合テスト（在庫が有限・レア保証なし） |
| L-8 | `CommitPurchase` 単一Tx（3.6） | 冪等/枯渇/残高不足/同時購入テスト緑 |
| L-9 | `CommitSale`（3.7） | 売却テスト緑 |
| L-10 | `events.go` Outbox→NATS 発行（3.8） | `world.{id}.event.economy` を購読して確認 |
| L-11 | ランキング Batch（3.9）＋管理コマンド | `asset_rankings` に price_version 付きで書かれる |
| L-12 | `make ci`（proto/lint/test/assets）＋ `make smoke` | 全緑・api gRPC 起動 |

---

## 5. テスト・受入（WSL2側）

`services/api` の Go テスト（`go test ./... -race`）と統合テスト（Go API + PostgreSQL・MVP 18.1 Integration）。DB は compose の Postgres または testcontainers。

| 種別 | ケース | 対応 AT / 合格条件 |
|---|---|---|
| Unit | 決定的在庫生成：同一 `(table_id, seed)` で同一在庫列 | AT-011。異なる seed で分布が weight に沿う。**Rare が無い回が存在**する |
| Unit | 価格演算：`computeUnitPrice` の整数丸め（floor）・最低価格1・オーバーフロー無し | float 不使用（forbidigo） |
| Integration | **冪等性**：同一 `idempotency_key` で `CommitPurchase` を2回 → 2回目は `DUPLICATE`、台帳/在庫/インベントリは1回分のみ変化、返却は同一結果 | AT-019 準拠 |
| Integration | **在庫枯渇**：`remaining_quantity=1` を2回購入 → 1回目 `COMMITTED`、2回目 `OUT_OF_STOCK` | AT-011/AT-012 |
| Integration | **残高不足**：残高 < unit_price → `INSUFFICIENT_FUNDS`、在庫・台帳を動かさない | 12.2 |
| Integration | **同時購入**：同一 `stock_entry_id` を並行2 goroutine → 1件のみ `COMMITTED`、他は `OUT_OF_STOCK`。残高/在庫/Item に不整合なし | AT-012 |
| Integration | **Buyer 非 active**：status='preparing' の Buyer へ購入 → `REJECTED`（despawn 準備後の新規購入拒否） | 12.2 |
| Integration | **version 不一致**：`inventory_version` が現行と異なる → `REJECTED` | 12.2.1 |
| Integration | 売却：`CommitSale` で `proceeds` 加算・在庫減算・version+1、冪等 | AT-011 系 |
| Integration | ランキング：所持金＋Item＋設備を net_worth に計上、`price_version`/`calculated_at` 保存 | 12.3 |
| Integration | NATS：購入/売却/出現/消滅で `world.{id}.event.economy` が発行される | 14.3 |

- 受入の起点は MVP 第18章 AT-011（有限Stock/Seed保存/Rare無しでも成立）、AT-012（同時購入で1者のみ）、AT-019（購入応答直後の crash 後も Item/台帳保持＝Tx とOutboxでAPI側は保証。DS再起動は 09A）。
- `make ci` と `make smoke`（api gRPC 起動含む）が緑であること。

---

## 6. 落とし穴（WSL2側）

- **float 混入**（最頻の設計違反）: 価格・net_worth・proceeds に `float64` を使わない。modifier は basis point 整数（3.4）。forbidigo が検出する（MVP 13.1）。
- **二重 Write**（12.2.1）: 購入時の `inventory_entries` 永続確定は API のみ。DS は再永続化しない。API 側でも「購入付与」を Outbox の通常 Inventory 変更として二度送らない（購入は CommitPurchase Tx 内でのみ確定）。
- **冪等の穴**: 事前 SELECT だけでなく、`purchase_transactions.idempotency_key` の **UNIQUE 違反時にも既存結果を返す**（並行同一キー）。二重に在庫/台帳を動かさない。
- **同時購入の取りこぼし**: `SELECT ... FOR UPDATE` と version 条件 UPDATE の**両方**を用い、UPDATE 0行なら競合として扱う。行ロック無しの単純 UPDATE は競合検知漏れ。
- **レア保証の誤実装**: `guaranteed_rare_slots=0`。抽選後にレアを補填してはいけない（AT-011/AT-017）。
- **決定性の破壊**: 在庫生成に時刻/UUID/未シードの乱数を混ぜない。`seed` 由来 PRNG のみ。`stock_entry_id` の UUID は在庫内容に影響させない。
- **Outbox 未発行**: NATS 切断時は Outbox に溜め、復旧後に順送（ゲームを止めない・MVP 16）。発行を Tx 内で同期 publish しない。
- **proto 生成物のコミット漏れ**: `economy.proto` 変更後の C#（`unity/.../Generated`）と Go 生成物を必ずコミット。`git diff --exit-code` で検出（03B 5.4）。
- **gRPC ポート衝突/未公開**: HTTP(8082) と gRPC(9092) を分離。compose/内部ネットワークでのみ DS から到達（MVP-SEC-001/007）。
- **口座の直列化漏れ**: 同一購入者の並行購入で残高を二重使用しないよう、残高読取も Tx 内で直列化する（MVP-SEC-009 監査）。

---

## 参考資料

- MVP 詳細設計書 v0.2.2：8.7（Buyer）、12.2 / 12.2.1（Purchase Transaction・単一Writer）、12.3（ランキングBatch）、13 / 13.1（DB・Index/Constraint・float禁止）、14.1/14.2/14.3（Command/gRPC/NATS）、18（AT-011/012/019/021）、19（M6 DoD）、付録B.3（Buyer Stock Definition）、付録C（データ所有権）。
- 基本設計書 v0.2.1：6.4（Buyerと経済）、6.1/9.4（単一 Writer）。
- proto：`proto/survival/v1/economy.proto`, `common.proto`, `gameplay.proto`。
- [R8] [NATS JetStream](https://docs.nats.io/nats-concepts/jetstream)
- [R10] [PostgreSQL JSON types](https://www.postgresql.org/docs/current/datatype-json.html)
- [R-MIGRATE] [golang-migrate](https://github.com/golang-migrate/migrate)
- [R-BUF] [Buf docs](https://buf.build/docs)
- [R-GRPC-GO] [gRPC-Go](https://grpc.io/docs/languages/go/)
