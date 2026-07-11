---
title: "M3 実装指示書（WSL2 / Linux 側）"
subtitle: "Domain Event 永続確定 / NATS 発行・購読土台 / マスタデータ供給 / migration 0003"
document_id: "IMPL-M3-WSL-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M3 / WSL2側）"
baseline: "Go / Python(FastAPI) / PostgreSQL / NATS JetStream / buf"
related_document: "06A_M3実装指示書_Windows側_v0.1.md, 05B_M2実装指示書_WSL2側_v0.1.md, 05A_M2実装指示書_Windows側_v0.1.md, 04B_M1実装指示書_WSL2側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M3 実装指示書：WSL2 / Linux 側 v0.1

本書は M3（Survival Vertical Slice）の作業を **WSL2（Ubuntu）側**（Go/api の永続化、NATS 発行・購読土台、マスタデータ供給、DB マイグレーション、テスト）に限定して指示する。Unity/DS のゲームロジックと Client は別冊 **06A（Windows側）** を参照。第0章「分担と連携」は両冊で要点を共有する（規約の正本は M0 03A/03B 第0章）。

M3 の成果（MVP 第19章）: 採掘・Development・製作・狩猟・料理・Hunger・Waste・清掃の縦切り。WSL2 側は、DS が生成した **Domain Event を永続確定する受け皿**（`WorldData.AppendEvents` 経由で `inventory_entries`/`item_instances`/`currency_ledger` を確定 = **永続 Writer**）、**NATS `world.{id}.event.resource` / `.actor` の発行と購読土台**、**ResourceNode/Recipe/ItemDefinition のマスタデータ供給**、**migration 0003** を担う。

---

## 0. 分担と連携（要点・両冊共有）

> 規約の正本（リポジトリ配置 / Git・LFS・改行 / 境界成果物 / `make` エントリポイント）は M0 03B 第0章。M3 で変わらない。本章は M3 固有の連携点に絞る。

### 0.1 M3 の環境別 責務分担

| 領域 | 担当環境 | M3 での主なタスク |
|---|---|---|
| DS 上のゲームロジック・Domain Event 生成・Outbox・AppendEvents 呼び出し | **Windows**（Unity Server） | 06A 参照。event_id=ULID / local_sequence は DS 採番 |
| **`WorldData.AppendEvents` サーバー実装（永続 Writer）** | **WSL2**（services/api, Go） | event_id 重複排除、sequence 採番、event 効果を DB へ適用 |
| `domain_events` / `inventory_entries` / `item_instances` / `currency_ledger` の確定 | **WSL2**（api が唯一の Writer） | 単一 Tx、付録C 準拠 |
| NATS `world.{id}.event.resource` / `.actor` **発行**（Outbox Relay） | **WSL2**（api） | 永続確定後に発行 |
| NATS **購読土台**（consumer スケルトン） | **WSL2**（worldstate, Python） | M4/M5 投影の下地 |
| **マスタデータ供給**（ItemDefinition/Recipe/ResourceNodeDef） | **WSL2**（migration seed + Bootstrap 配信） | DS が LoadBootstrap で受領 |
| DB **migration 0003**、Go/Integration テスト | **WSL2** | 本書第3〜5章 |
| Client UI・入力・DS ゲームロジック | **Windows** | 06A 参照 |

### 0.2 競合回避（厳守）

- **WSL2 側が触るのは `services/`, `proto/`, `infra/`, `assets-pipeline/`, `scripts/*.sh`, `Makefile` のみ。** `unity/` は触らない（例外＝`unity/SurvivalWorld/Assets/Generated/` は buf の C# 出力先として **生成のみ**）。
- proto は **メッセージ/RPC の唯一の正**。M3 で proto 変更が要る場合（本 M3 では原則不要。`WorldData.AppendEvents`/`DomainEvent`/`InteractCommand`/`PrimaryActionCommand` は M0 で確定済み）は WSL2 が `.proto` を編集し `make proto` で全言語生成、C# 出力を含めてコミットする（03B 5.4）。

### 0.3 境界契約：Domain Event 型カタログ（A/B 共通・06A 第0章と一致させる）

DS（A）が生成し、api（B）が永続確定する M3 の Domain Event。**payload スキーマは 06A 0.3 と完全一致**させること。B は各 type の payload を解釈して DB へ適用し、対応する NATS subject へ発行する。

| type | aggregate_id | B の DB 適用 | NATS subject |
|---|---|---|---|
| `resource.mined` | node_id | `grants[]` を inventory_entries/item_instances へ加算 | `world.{id}.event.resource` |
| `resource.node_depleted` | node_id | （記録のみ。node runtime は Snapshot 側） | `world.{id}.event.resource` |
| `resource.node_regenerated` | node_id | （記録のみ） | `world.{id}.event.resource` |
| `station.job_started` | station_id | inventory_entries.reserved を確保（任意・下記注） | `world.{id}.event.actor` |
| `station.job_completed` | station_id | `consumed[]` 減算・`produced[]` 加算（個体は item_instances 生成） | `world.{id}.event.actor` |
| `station.job_cancelled` | station_id | reserved 解除（任意） | `world.{id}.event.actor` |
| `development.blueprint_unlocked` | world_id | `world_blueprints` に upsert | `world.{id}.event.actor` |
| `farm.crop_planted` / `farm.crop_harvested` | plot_id | harvested の produced を加算 | `world.{id}.event.actor` |
| `hunting.animal_killed` | animal_id | （記録のみ） | `world.{id}.event.actor` |
| `hunting.carcass_butchered` | carcass_id | `drops[]` を加算 | `world.{id}.event.actor` |
| `cooking.completed` | station_id | `consumed[]` 減算・`produced[]` 加算 | `world.{id}.event.actor` |
| `inventory.item_consumed` | actor_id | 対象 item を減算 | `world.{id}.event.actor` |
| `item.discarded` | actor_id | 対象を減算 + `world_items` に INSERT | `world.{id}.event.actor` |
| `cleaning.completed` | world_item_id | `world_items` から削除/Disposal + `reward_amount>0` なら currency_ledger 追記 | `world.{id}.event.actor` |
| `character.vitals_changed` | actor_id | （actor_runtime_states は ActorState.Save 経路。ここでは記録のみ） | `world.{id}.event.actor` |

> **reserved（予約）の永続方針**: セッション中の予約は DS メモリが正本（付録C「セッション中 Inventory」）。B は `job_started/cancelled` を**永続の必須適用にしない**でよい（記録・NATS 発行のみ）。**確定は `job_completed`（consumed/produced）で行う**。二重適用を避けるため、`consumed[]/produced[]` を持つ完了系イベントのみ inventory を書き換えるのが安全。
> **単一 Writer 原則**（12.2.1 / 付録C）: `inventory_entries`/`item_instances`/`currency_ledger` の Writer は **API のみ**。DS はこれらを直接書かず Domain Event を送る。M3 の通常フロー（採掘・料理・廃棄）は「DS が Runtime 更新 → Event を Outbox → API が永続確定」の一方向。

### 0.4 NATS Subject（14.3）と発行タイミング

| Subject | 載せる type | 発行元 |
|---|---|---|
| `world.{id}.event.resource` | `resource.*` | api Outbox Relay |
| `world.{id}.event.actor` | それ以外の M3 type（station/hunting/cooking/farm/inventory/item/cleaning/character/development） | api Outbox Relay |
| `world.{id}.event.economy` | （M3 では未使用。M6 経済） | — |

- **発行は永続確定後**（`domain_events` 追記が成功してから NATS へ）。順序は `outbox_messages` を経由し、Relay が JetStream へ publish。購読側の再配信・重複を許容する設計（At-least-once, 03B R8/R9）。

---

## 1. 対象と前提（WSL2側）／ M3 DoD

### 1.1 対象

- **`WorldData.AppendEvents` サーバー実装**（services/api, Go）: `event_id`(ULID) で重複排除、`(world_id) sequence` を API 採番、payload を解釈して inventory/item/currency/world_items/blueprint を単一 Tx で確定し、`outbox_messages` に NATS 発行分を積む。結果を `results[]`（OK/DUPLICATE/CONFLICT）で返す。
- **NATS 発行**（Outbox Relay）と**購読土台**（worldstate consumer スケルトン）。
- **マスタデータ供給**: migration 0003 で `item_definitions`/`recipes`/`recipe_ingredients`/`resource_node_defs` を作成・seed し、`WorldData.LoadBootstrap` の payload へ含めて配信。
- **migration 0003**: 上記マスタ＋`world_items`（Drop/Carcass 永続, AT-010）＋`world_blueprints`（Development 解放, 8.4）。
- **テスト**: Go Unit（採番/冪等/Recipe 消費）と Integration（api+PostgreSQL, AppendEvents 一連, 再起動復元）。

### 1.2 M3 WSL2側 DoD

- `WorldData.AppendEvents` が **event_id で冪等**（同一 event 2 回で 2 回目 `DUPLICATE`・DB は 1 回分, AT-003）。
- 各 M3 Domain Event が **単一 Tx** で `domain_events` 追記＋対応する `inventory_entries`/`item_instances`/`currency_ledger`/`world_items`/`world_blueprints` 反映＋`outbox_messages` 追記まで**原子的**に行われる（部分適用なし）。
- `(world_id, sequence)` が **API 採番で単調**（`domain_events_seq_unique` を破らない）。CONFLICT 時は該当 event を CONFLICT で返し DB を変更しない。
- NATS `world.{id}.event.resource` / `.actor` が**永続確定後**に発行され、worldstate 購読土台が受信ログを出す。
- `LoadBootstrap` の payload にマスタデータ（Item/Recipe/ResourceNodeDef）が含まれ、DS がキャッシュできる（06A 0.4-2）。**Recipe/精錬の数量・時間は 06A 3.3 と同値**。
- migration 0003 の up/down が `make migrate` で適用/巻き戻しでき、seed が冪等（再適用で重複しない）。
- 通貨は **BIGINT（最小単位・float 禁止, 13.1）**。重量も整数（下記 3.1 注）。
- `make ci`（proto/lint/test/assets）と `make smoke` が緑。Integration で AT-003/AT-009/AT-018/AT-021 相当を確認。

---

## 2. 前提成果物（M0–M2 / WSL2側から見た依存）

| 由来 | 前提 | 参照 |
|---|---|---|
| M0 | docker-compose(postgres/nats -js)、Makefile/scripts、buf 生成（Go/Python/C#）、`services/api` 骨格（/healthz,/readyz, pgxpool, nats）、migration 0001（worlds/characters/domain_events/inventories/inventory_entries/item_instances/currency_ledger/purchase_transactions/outbox_messages/inbox_dedup） | 03B, `services/api/migrations/0001_init.up.sql` |
| M0 | proto: `worlddata.proto`（`WorldDataService.AppendEvents/LoadBootstrap/SaveSnapshot`）、`common.proto`（`DomainEvent`/`InventoryEntry`/`ResultStatus`） | proto/survival/v1 |
| M1 | Auth/Join Ticket/game_servers、FishNet 接続の受け入れ | 04B（M1） |
| M2 | `WorldData.LoadBootstrap`/`AppendEvents`/`SaveSnapshot` の gRPC サーバー実装、`inventories`/`inventory_entries`/`item_instances` の基本 Read/Write、Outbox Relay の骨格、World Load/Save | 05B（M2） |

> M2 で `AppendEvents` の gRPC サーバーと Outbox Relay の**骨格**が入っている前提。M3 は AppendEvents に **M3 の event 効果適用**と **NATS 発行 subject 分岐**を積み増す。M2 で未実装なら AppendEvents の受理・冪等・sequence 採番の基礎から実装する。

---

## 3. 実装対象（api 永続 / NATS / マスタ / migration）

### 3.1 migration 0003（services/api/migrations/0003_survival.up.sql / .down.sql）

golang-migrate 形式（03B 7章）。**通貨・重量は整数**（13.1）。seed は `INSERT ... ON CONFLICT DO NOTHING` で冪等に。

**(1) マスタ: item_definitions（7.2 の実値を seed）**

```sql
CREATE TABLE IF NOT EXISTS item_definitions (
  item_definition_id TEXT PRIMARY KEY,
  primary_tag        TEXT NOT NULL,
  tags               TEXT[] NOT NULL DEFAULT '{}',
  stack_limit        INTEGER NOT NULL,
  weight_milli       BIGINT NOT NULL,   -- 重量は kg×1000 の整数（float 禁止・13.1）
  rarity             INTEGER NOT NULL DEFAULT 0,
  consume_hunger     INTEGER NOT NULL DEFAULT 0,  -- Consume 時 Hunger 回復（cooked_meat/luxury=30）
  waste_output       INTEGER NOT NULL DEFAULT 0,  -- 料理での waste 産出数（luxury=2, 通常=1）
  is_instance        BOOLEAN NOT NULL DEFAULT FALSE -- 品質/耐久を持つ個体か（武器等）
);
```

seed 値（7.2）: stone(50,1000,0)/iron_ore(30,1500,0)/rare_ore(10,1500,2)/wood(30,800,0)/iron_ingot(20,1200,0)/rare_ingot(10,1200,2)/leather(20,500,0)/bone(20,500,0)/stone_spear(1,3000,0,instance)/raw_meat(10,1000,0)/rare_meat(5,1000,2)/cooked_meat(10,800,0,consume_hunger=30)/food_waste(20,300,0)/stone_pickaxe(1,4000,0,instance)/iron_hunting_spear(1,5000,0,instance)/luxury_food(5,800,2,consume_hunger=30,waste_output=2)/decorative_weapon(1,6000,2,instance)/rare_weapon(1,5000,3,instance)。`stone_spear` は初期狩猟武器（石+木, leather 不要, 8.4）。

**(2) マスタ: recipes / recipe_ingredients（8.4 の実値・06A 3.3 と同値）**

```sql
CREATE TABLE IF NOT EXISTS recipes (
  recipe_id       TEXT PRIMARY KEY,
  kind            TEXT NOT NULL,   -- known | development | crafted_after_dev
  station_type    TEXT NOT NULL,   -- forge | anvil | cooking_station | farm_plot
  output_item     TEXT,            -- development は NULL 可
  output_quantity INTEGER NOT NULL DEFAULT 1,
  craft_seconds   INTEGER NOT NULL,
  unlock_blueprint TEXT,           -- development が解放する blueprint_id
  required_blueprint TEXT          -- 製作に必要な blueprint_id（NULL=常時可）
);
CREATE TABLE IF NOT EXISTS recipe_ingredients (
  recipe_id          TEXT NOT NULL REFERENCES recipes(recipe_id),
  item_definition_id TEXT NOT NULL,
  quantity           INTEGER NOT NULL,
  PRIMARY KEY (recipe_id, item_definition_id)
);
```

seed（8.4 + 精錬。**06A 3.3 と数量・時間を一致**）:
- `stone_pickaxe`(known/anvil, out=stone_pickaxe/30s): stone×5, wood×2
- `stone_spear`(known/anvil, out=stone_spear/20s): stone×3, wood×2（leather 不要）
- `iron_ingot`(known/forge, out=iron_ingot/40s・暫定): iron_ore×2
- `rare_ingot`(known/forge, out=rare_ingot/60s・暫定): rare_ore×2
- `iron_spear_research`(development/anvil, out=NULL/120s, unlock_blueprint=iron_spear): iron_ore×5, rare_ore×1
- `iron_hunting_spear`(crafted_after_dev/anvil, out=iron_hunting_spear/60s, required_blueprint=iron_spear): iron_ingot×3, wood×1, leather×1
- `rare_weapon_craft`(crafted_after_dev/anvil, out=rare_weapon/90s): rare_ingot×3, iron_ingot×5

> 精錬 2 件は 8.4 表に明示が無い暫定追加（iron_hunting_spear が iron_ingot を要するため）。**06A（DS）と本 seed を必ず同値**にする。片方だけ変えない。

**(3) マスタ: resource_node_defs（8.3）**

```sql
CREATE TABLE IF NOT EXISTS resource_node_defs (
  resource_type      TEXT PRIMARY KEY,   -- stone | iron | rare
  drop_item          TEXT NOT NULL,      -- stone->stone, iron->iron_ore, rare->rare_ore
  required_tool_tags TEXT[] NOT NULL,    -- {tool.mining}
  hardness           INTEGER NOT NULL,
  maximum_amount     INTEGER NOT NULL,
  quality            INTEGER NOT NULL DEFAULT 0,
  regeneration_policy JSONB NOT NULL DEFAULT '{}'::jsonb
);
```

seed（暫定・DS と同値）: stone(drop=stone, {tool.mining}, hardness=2, max=50), iron(drop=iron_ore, {tool.mining}, hardness=4, max=30), rare(drop=rare_ore, {tool.mining}, hardness=6, max=10, quality=2, regen=cooldown 長め)。

**(4) 永続: world_items（Drop/Carcass, AT-010）**

```sql
CREATE TABLE IF NOT EXISTS world_items (
  world_item_id      UUID PRIMARY KEY,
  world_id           UUID NOT NULL,
  item_definition_id TEXT NOT NULL,
  item_instance_id   UUID REFERENCES item_instances(item_instance_id),
  quantity           INTEGER NOT NULL DEFAULT 1,
  pos_x REAL NOT NULL, pos_y REAL NOT NULL, pos_z REAL NOT NULL,  -- 座標は float 許容（数量/通貨ではない）
  owner_id           UUID,
  tags               TEXT[] NOT NULL DEFAULT '{}',
  created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS world_items_world_idx ON world_items (world_id);
```

**(5) 永続: world_blueprints（Development 解放・World 共通推奨 8.4）**

```sql
CREATE TABLE IF NOT EXISTS world_blueprints (
  world_id     UUID NOT NULL,
  blueprint_id TEXT NOT NULL,
  unlocked_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (world_id, blueprint_id)
);
```

- `.down.sql` は上記 5 テーブルを DROP（seed も消える）。0001 のテーブルは触らない。
- `domain_events` に集計列や追加索引が必要なら本 migration で足す（例: `(world_id, type)` 索引で WorldState 投影を軽くする）。任意。

### 3.2 AppendEvents 永続確定（services/api・永続 Writer の中核）

`WorldDataService.AppendEvents(AppendEventsRequest{server_id, events[]})` を実装。**events は 1 リクエストに複数**。各 event を順に処理し `results[]` を返す。

**各 event の単一 Tx（原子性）:**

```text
BEGIN;
  -- 1) 冪等: event_id で重複排除
  INSERT INTO domain_events(event_id, world_id, aggregate_id, local_sequence, sequence, type, payload, occurred_at)
    VALUES (...) ON CONFLICT (event_id) DO NOTHING;
  -- 行が入らなければ DUPLICATE として COMMIT（副作用なし）
  -- 2) sequence 採番: SELECT COALESCE(MAX(sequence),0)+1 FROM domain_events WHERE world_id=$1 （行ロック/advisory lock）
  --    ※ (world_id, sequence) UNIQUE を衝突させないよう world 単位で直列化
  -- 3) payload を type で分岐し、inventory_entries/item_instances/currency_ledger/world_items/world_blueprints を反映（0.3 表）
  -- 4) outbox_messages に NATS 発行分を INSERT（topic = world.{id}.event.resource|actor, payload=event）
COMMIT;
```

- **重複排除は `event_id`（UUID/ULID を格納。0001 は event_id UUID。ULID を UUID 列に入れるなら 16byte 表現で統一）**。ULID 文字列運用にする場合は 0003 で `domain_events.event_id` を TEXT へ変更するか、別途 `event_ulid TEXT UNIQUE` を追加（**A の ULID 形式と整合させる**・下記落とし穴参照）。
- **sequence 採番の直列化**: `pg_advisory_xact_lock(hashtext(world_id))` または `SELECT ... FOR UPDATE` で world 単位に直列化し、`(world_id, sequence)` UNIQUE 衝突を防ぐ。衝突検知時は **CONFLICT** を返し当該 event を適用しない（DB 変更なし）。
- **event 効果適用**（0.3 表・完了系のみ inventory 書換え, 0.3 注）:
  - `resource.mined` / `hunting.carcass_butchered` / `farm.crop_harvested`: `grants[]/drops[]/produced[]` を `inventory_entries` に加算（stack 物は quantity 加算、`is_instance` は `item_instances` を INSERT し slot へ）。
  - `station.job_completed` / `cooking.completed`: `consumed[]` を減算、`produced[]` を加算。
  - `inventory.item_consumed`: 対象 item を減算。
  - `item.discarded`: 対象を減算 + `world_items` に INSERT。
  - `cleaning.completed`: `world_items` から DELETE（Disposal 記録）+ `reward_amount>0` なら `currency_ledger` に追記（BIGINT）。
  - `development.blueprint_unlocked`: `world_blueprints` upsert。
  - `resource.node_*` / `hunting.animal_killed` / `station.job_started|cancelled` / `farm.crop_planted` / `character.vitals_changed`: `domain_events` 記録＋NATS 発行のみ（inventory 非書換え）。
- Inventory 反映は `inventories.version` を +1 する（7.1 Version）。owner 単位で直列化（M2 の InventoryService 相当が API 側にあるならそれを使う）。
- **actor_runtime_states / Hunger・Health** は本 RPC ではなく `ActorState.Save`（worlddata.proto の `ActorStateService.Save`）経路で永続（付録C）。`character.vitals_changed` は NATS 通知＋監査記録に留める。

### 3.3 NATS 発行（Outbox Relay 拡張）と購読土台

**発行（api）:**
- AppendEvents Tx で積んだ `outbox_messages`（topic=`world.{id}.event.resource|actor`）を Relay が JetStream へ publish（M2 の Relay 骨格を M3 subject に対応）。
- **必ず永続確定後**（同一 Tx で outbox に積み、Relay が別途 publish→published_at 更新）。JetStream ストリームは `world.*.event.>` を束ねる設定（03B 4章の `-js` 前提, R8）。

**購読土台（worldstate, Python）:**
- `world.*.event.resource` と `world.*.event.actor` を購読する **consumer スケルトン**を追加（`services/worldstate/app`）。M3 では**受信して inbox_dedup で冪等記録＋ログ**まで（投影本体は M5）。
- Durable Consumer + `inbox_dedup(consumer_id, message_id)` で At-least-once の重複を吸収（R9）。重い処理を handler に置かない（03B R7）。

### 3.4 マスタデータ配信（LoadBootstrap payload）

- `WorldData.LoadBootstrap` の応答 `snapshot_payload`（または追加フィールド/別 RPC）に **ItemDefinition/Recipe(+ingredients)/ResourceNodeDef** を含めて DS へ渡す（06A 0.4-2）。
- 実装は `item_definitions`/`recipes`/`recipe_ingredients`/`resource_node_defs` を SELECT し JSON 化して payload に同梱。`worlds.content_version` をマスタ版として付与し、DS がキャッシュ更新判定に使えるようにする。
- proto 追加が必要なら（例: `LoadBootstrapResponse` に master data フィールド）WSL2 が proto を更新し `make proto`（C# 生成含む）→ 06A に通知。**最小化のため、まずは `snapshot_payload` JSON に相乗り**でよい。

### 3.5 通貨・整合の原則（13.1 / 付録C の再確認）

- `currency_ledger.delta/balance_after` は **BIGINT**。清掃報酬（`cleaning.completed.reward_amount`）も整数。float 混入を `.golangci.yml`/レビューで警戒（03B 5.2）。
- **単一 Writer**: inventory/item/currency は API のみが書く。DS からの二重 Write を作らない（AT-021 の思想を M3 の採掘/料理/清掃にも適用）。

---

## 4. 実装順序（WSL2側）

| # | タスク | 完了確認 |
|---|---|---|
| L-1 | migration 0003 作成（item_definitions/recipes/recipe_ingredients/resource_node_defs/world_items/world_blueprints）＋seed（3.1） | `make migrate` up/down 成功、seed 冪等 |
| L-2 | マスタ Read 層（Go）＋`LoadBootstrap` payload へ同梱（3.4） | DS 起動でマスタ受領（06A と結合） |
| L-3 | `AppendEvents` の冪等・sequence 採番（world 単位直列化）（3.2-1/2） | 二重送信で 2 回目 DUPLICATE、seq 単調 |
| L-4 | event 効果適用（resource.mined / station/cooking / carcass / discard / cleaning / blueprint）（3.2-3） | 各 type で DB が正しく変化 |
| L-5 | `outbox_messages` へ NATS 発行分を同一 Tx で積む＋Relay 拡張（3.3 発行） | 確定後に world.{id}.event.* 発行 |
| L-6 | worldstate 購読土台（resource/actor, inbox_dedup）（3.3 購読） | 受信ログ・重複吸収 |
| L-7 | Go Unit（採番/冪等/Recipe 消費/整数）＋Integration（api+PG, AppendEvents 一連, 再起動復元）（第5章） | `make test` 緑 |
| L-8 | `make ci` / `make smoke` 全緑 | health 200・E2E 通過 |

---

## 5. テスト・受入（WSL2側）

### 5.1 Go Unit（18.1: Inventory version / Recipe / Drop Table / ...）

- **冪等**: 同一 `event_id` 2 回で 2 回目 `DUPLICATE`、`domain_events`/`inventory_entries` は 1 回分（AT-003）。
- **sequence 採番**: 並行 AppendEvents で `(world_id, sequence)` が単調・衝突なし（advisory lock/UNIQUE で保証）。
- **Recipe 消費**: `station.job_completed` の consumed/produced が inventory を正しく増減、`inventories.version`+1（AT-005 の永続面）。
- **Drop 反映**: `hunting.carcass_butchered` の drops が加算（AT-008 の永続面）。
- **料理**: `cooking.completed` で raw_meat 減・cooked_meat/waste 加（AT-009 の永続面）。
- **清掃**: `item.discarded`→`world_items` INSERT、`cleaning.completed`→DELETE＋reward の currency 追記（AT-010 の永続面）。
- **整数**: currency/weight が float でないこと。

### 5.2 Integration（18.1: Go API+PostgreSQL, Outbox, World bootstrap）

- **AppendEvents 一連**: 実 PostgreSQL で採掘→料理→廃棄→清掃の一連 event を投入し DB 最終状態を検証。
- **Outbox→NATS**: 確定後に `world.{id}.event.resource|actor` が発行され、worldstate 購読土台が受信（重複を inbox_dedup で吸収）。
- **再起動復元**（AT-018）: 最新 Snapshot＋Event tail で World 状態が復元（`LoadBootstrap`→event 再適用の整合）。
- **単一 Writer**（AT-021 思想）: 同一付与が 2 回永続化されない（event_id 冪等＋DS が二重 Write しない前提）。
- **マスタ配信**: `LoadBootstrap` payload にマスタが含まれ、値が seed と一致。

### 5.3 CI

- `make ci`（proto/lint/test/assets）緑、`make smoke`（全サービス health 200）緑。proto 変更した場合は drift 検査（`git diff --exit-code`、C# 出力先含む）に通す（03B 5.4）。

---

## 6. 落とし穴（WSL2側）

- **event_id の型不整合（最重要）**: A は ULID（Crockford Base32 の 26 文字 or 128bit）。0001 の `domain_events.event_id` は **UUID**。**A の ULID 形式と B の格納形式を必ず一致**させる。方針を 1 つに固定: (a) ULID を 128bit として UUID 列に格納、または (b) 0003 で `event_id` を TEXT 化。**A/B で同一表現**にしないと重複排除が効かず二重反映（AT-003 破綻）。実装前に 06A と表現を合意すること。
- **sequence 採番の競合**: world 単位の直列化（advisory lock / FOR UPDATE）を怠ると `(world_id, sequence)` UNIQUE 違反で AppendEvents が散発失敗。採番と INSERT を同一 Tx・同一ロック下で。
- **予約の二重適用**: `job_started`/`cancelled` で inventory を確定書換えすると `job_completed` と二重になる。**完了系のみで確定**（0.3 注）。
- **NATS を確定前に発行**: 永続失敗時に幽霊イベントを撒く。必ず Tx 確定後（outbox 経由）に publish。
- **JetStream 未有効/Subject 設定漏れ**: `-js` と `world.*.event.>` のストリーム設定を確認（03B 4/10章）。
- **Recipe/精錬/マスタ値の A/B 齟齬**: 数量・時間・ツール要件を 06A 3.3/3.2 と**同値**に。齟齬は製作不能・容量計算ずれの原因。**本書（B の seed）を正**とし A を合わせる運用（06A 0.3 と一致）。
- **通貨/重量に float**（13.1）: currency は BIGINT、重量は `weight_milli`（整数）。静的解析/レビューで防ぐ。
- **DS が inventory を直接書く前提のコード**を api 側に作らない（付録C）。api が唯一の Writer。
- **seed 非冪等**: `ON CONFLICT DO NOTHING` を付けないと再 migrate/再起動で重複。up.sql を冪等に。
- **`.sh` の CRLF / /mnt/c I/O**（03B 0.3/5.5 の再掲）: LF 固定、ビルドキャッシュは WSL2 ホームへ。
- **`unity/` を編集**しない（0.2）。C# 生成物は buf 生成のみ。

---

## 参考資料

[R-MVP] docs/02_MVP詳細設計書_v0.2.2.md（8.1〜8.6 生産ループ, 12.1 Bootstrap/Save, 12.2.1 単一Writer, 13 DB論理設計, 13.1 採番/整数, 14.2 gRPC, 14.3 NATS, 18 テスト, 19 DoD, 付録C Writer）
[R-BSD] docs/01_基本設計書_v0.2.1.md（6.2/6.3 非干渉, 9.4 Writer 原則）
[R-06A] docs/prompts/06A_M3実装指示書_Windows側_v0.1.md（Domain Event 生成・ULID/local_sequence 採番・Outbox・DS ロジック）
[R-03B] docs/prompts/story_0000/linux/03B_M0実装指示書_WSL2側_v0.1.md（Makefile/scripts/proto/migration/NATS 基盤）
[R-PROTO] proto/survival/v1/{worlddata,common,gameplay}.proto
[R8] [NATS JetStream](https://docs.nats.io/nats-concepts/jetstream)
[R9] [NATS JetStream Consumers](https://docs.nats.io/nats-concepts/jetstream/consumers)
[R10] [PostgreSQL JSON types](https://www.postgresql.org/docs/current/datatype-json.html)
[R-MIGRATE] [golang-migrate](https://github.com/golang-migrate/migrate)
