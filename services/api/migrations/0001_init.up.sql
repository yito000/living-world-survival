-- API 所有テーブル（MVP 13章）。M0 は接続確認に必要な最小構成。
-- 通貨は float を使わず BIGINT（最小単位・MVP 13.1）。
-- 一意制約（idempotency_key, event_id, (world_id, sequence) 等）を初期から入れる。

CREATE TABLE IF NOT EXISTS worlds (
    world_id           UUID PRIMARY KEY,
    active_snapshot_id UUID,
    content_version    BIGINT NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS characters (
    character_id UUID PRIMARY KEY,
    account_id   UUID NOT NULL,
    display_name TEXT NOT NULL,
    world_id     UUID REFERENCES worlds (world_id),
    version      BIGINT NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS characters_account_idx ON characters (account_id);

CREATE TABLE IF NOT EXISTS world_snapshots (
    snapshot_id UUID PRIMARY KEY,
    world_id    UUID NOT NULL REFERENCES worlds (world_id),
    sequence    BIGINT NOT NULL,
    payload     JSONB NOT NULL,
    checksum    TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT world_snapshots_seq_unique UNIQUE (world_id, sequence)
);

-- domain_events: DS が event_id/local_sequence を採番、API が sequence を確定（MVP 13.1）。
CREATE TABLE IF NOT EXISTS domain_events (
    event_id       UUID PRIMARY KEY,
    world_id       UUID NOT NULL,
    aggregate_id   UUID NOT NULL,
    local_sequence BIGINT NOT NULL,
    sequence       BIGINT NOT NULL,
    type           TEXT NOT NULL,
    payload        JSONB NOT NULL,
    occurred_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT domain_events_seq_unique UNIQUE (world_id, sequence)
);
CREATE INDEX IF NOT EXISTS domain_events_world_time_idx ON domain_events (world_id, occurred_at);

CREATE TABLE IF NOT EXISTS item_instances (
    item_instance_id UUID PRIMARY KEY,
    definition_id    TEXT NOT NULL,
    quality          INTEGER NOT NULL DEFAULT 0,
    durability       INTEGER NOT NULL DEFAULT 0,
    attributes       JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE IF NOT EXISTS inventories (
    inventory_id    UUID PRIMARY KEY,
    owner_type      TEXT NOT NULL,
    owner_id        UUID NOT NULL,
    slot_capacity   INTEGER NOT NULL,
    weight_capacity BIGINT NOT NULL,
    version         BIGINT NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS inventory_entries (
    inventory_id       UUID NOT NULL REFERENCES inventories (inventory_id) ON DELETE CASCADE,
    slot_index         INTEGER NOT NULL,
    item_definition_id TEXT NOT NULL,
    item_instance_id   UUID REFERENCES item_instances (item_instance_id),
    quantity           INTEGER NOT NULL DEFAULT 0,
    reserved           INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (inventory_id, slot_index)
);

-- 通貨元帳。delta / balance_after は BIGINT（最小単位）。
CREATE TABLE IF NOT EXISTS currency_ledger (
    entry_id       UUID PRIMARY KEY,
    owner_id       UUID NOT NULL,
    delta          BIGINT NOT NULL,
    balance_after  BIGINT NOT NULL,
    reason         TEXT NOT NULL,
    correlation_id UUID,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS currency_ledger_owner_idx ON currency_ledger (owner_id);

CREATE TABLE IF NOT EXISTS purchase_transactions (
    purchase_id     UUID PRIMARY KEY,
    idempotency_key TEXT NOT NULL,
    buyer           UUID NOT NULL,
    purchaser       UUID NOT NULL,
    amount          BIGINT NOT NULL,
    status          TEXT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT purchase_transactions_idem_unique UNIQUE (idempotency_key)
);

-- Outbox / Inbox（信頼性のあるメッセージング, MVP 13章）。
CREATE TABLE IF NOT EXISTS outbox_messages (
    message_id   UUID PRIMARY KEY,
    topic        TEXT NOT NULL,
    payload      JSONB NOT NULL,
    available_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    published_at TIMESTAMPTZ,
    retry_count  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS outbox_unpublished_idx ON outbox_messages (available_at) WHERE published_at IS NULL;

CREATE TABLE IF NOT EXISTS inbox_dedup (
    consumer_id  TEXT NOT NULL,
    message_id   UUID NOT NULL,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer_id, message_id)
);
