-- M6: Economy（Buyer / 有限Stock / 購入Transaction / 資産ランキング）— 09B 3.3。
-- 0001-0005 は変更せず追記する。通貨は float を使わず BIGINT（最小通貨単位・MVP 13.1）。
-- Owner（付録C / 13章）: buyer_instances / buyer_stock = API、asset_rankings = Batch。
-- Buyer の spawn/despawn は DS が起点だが、永続 Writer は常に API（単一 Writer 原則）。

-- ---------------------------------------------------------------------------
-- (0) DS 生成の識別子は proto 上すべて string（0003 の既定方針）。0001 の
--     purchase_transactions.buyer / purchaser は UUID のままなので、DS が送る
--     actor_id（"connection:0" 等の非UUID）で INSERT すると 22P02 になる。
--     0003 が inventories.owner_id / currency_ledger.owner_id を TEXT 化したのと
--     同じ理由で TEXT へ拡張する（購入者は inventories.owner_id と突き合わせる）。
-- ---------------------------------------------------------------------------
ALTER TABLE purchase_transactions ALTER COLUMN buyer     TYPE TEXT USING buyer::text;
ALTER TABLE purchase_transactions ALTER COLUMN purchaser TYPE TEXT USING purchaser::text;

-- ---------------------------------------------------------------------------
-- (1) Buyer 個体（Owner=API, MVP 8.7 / 13）。
--     status: spawning / active / preparing / despawned。
--     preparing 以降は新規購入を拒否する（09B 3.6 検証ステップ）。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS buyer_instances (
    buyer_instance_id  UUID        PRIMARY KEY,           -- API 発番
    idempotency_key    TEXT        NOT NULL UNIQUE,       -- RegisterBuyer の冪等キー（DS が決定的に生成）
    world_id           TEXT        NOT NULL,              -- DS 生成 id は非 UUID もあり得る（0004/0005 と同方針）
    region_id          TEXT        NOT NULL,
    seed               BIGINT      NOT NULL,              -- 在庫生成を決定的にする（B.3）
    inventory_table_id TEXT        NOT NULL,              -- 付録B.3 の table id
    price_modifier_bp  INTEGER     NOT NULL DEFAULT 10000, -- 10000 = ×1.0（basis point 整数・float禁止）
    spawn_at           TIMESTAMPTZ NOT NULL,
    despawn_at         TIMESTAMPTZ NOT NULL,
    status             TEXT        NOT NULL DEFAULT 'active',
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS buyer_instances_world_status_idx
    ON buyer_instances (world_id, status);

-- ---------------------------------------------------------------------------
-- (2) Buyer 在庫（Owner=API）。unit_price は在庫生成時に確定した整数で、
--     購入時に再計算しない（09B 3.4）。remaining_quantity と version で楽観制御し、
--     購入は SELECT ... FOR UPDATE + version 条件 UPDATE の両方で競合を検知する。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS buyer_stock (
    stock_entry_id     UUID    PRIMARY KEY,
    buyer_instance_id  UUID    NOT NULL REFERENCES buyer_instances (buyer_instance_id) ON DELETE CASCADE,
    item_definition_id TEXT    NOT NULL,
    unit_price         BIGINT  NOT NULL,
    remaining_quantity INTEGER NOT NULL,
    version            BIGINT  NOT NULL DEFAULT 0,
    CONSTRAINT buyer_stock_qty_nonneg CHECK (remaining_quantity >= 0)
);
CREATE INDEX IF NOT EXISTS buyer_stock_buyer_remaining_idx
    ON buyer_stock (buyer_instance_id, remaining_quantity);

-- ---------------------------------------------------------------------------
-- (3) 資産ランキング（Owner=Batch, MVP 12.3 / 13）。net_worth は BIGINT。
--     同一実行は同一 price_version で全行を書き、過去実行は世代として残す。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS asset_rankings (
    rank_id       UUID        PRIMARY KEY,
    owner_id      TEXT        NOT NULL,   -- inventories.owner_id と同型（0003 で TEXT 化済み）
    net_worth     BIGINT      NOT NULL,
    price_version BIGINT      NOT NULL,
    calculated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS asset_rankings_run_idx
    ON asset_rankings (price_version, net_worth DESC);

-- ---------------------------------------------------------------------------
-- (4) 冪等リプレイ用（MVP 12.2 / AT-019）。同一 idempotency_key の再送で
--     PurchaseResult を完全復元できるよう、確定した結果を保存する。
--     id 系は (0) と同じ理由で TEXT / TEXT[]（item_instance_id は 0003 で TEXT 化済み）。
-- ---------------------------------------------------------------------------
ALTER TABLE purchase_transactions
    ADD COLUMN IF NOT EXISTS stock_entry_id         TEXT,
    ADD COLUMN IF NOT EXISTS item_instance_ids      TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS granted_definition_ids TEXT[] NOT NULL DEFAULT '{}',
    ADD COLUMN IF NOT EXISTS new_inventory_version  BIGINT NOT NULL DEFAULT 0;

-- ---------------------------------------------------------------------------
-- (5) 売却額（MVP 8.7 / 12.3）。base_value（購入基準額）は 0002 で作成済み。
--     sell_price は売却時の proceeds とランキングの Item 評価額に用いる整数。
--     実体は data/item_definitions.json（A/B 単一の正）から Go 側 seeder が供給する。
-- ---------------------------------------------------------------------------
ALTER TABLE item_definitions
    ADD COLUMN IF NOT EXISTS sell_price BIGINT NOT NULL DEFAULT 0;
