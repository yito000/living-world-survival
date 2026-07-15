-- M2: Inventory / Save の永続基盤（05B 3.7）。0001 は変更せず追記する。
-- 通貨・重量は float を使わず整数（BIGINT / milli 整数）で保持する（MVP 13.1）。

-- (a) domain_events.event_id を ULID(文字列26桁) 保持へ。
--     proto/生成物は event_id を文字列 ULID で送るため TEXT 化して素直に保持する（落とし穴6.5）。
--     PK は既存のまま（TEXT PK として dedup に使う）。
ALTER TABLE domain_events ALTER COLUMN event_id TYPE TEXT USING event_id::text;
-- aggregate_id も proto は string で、DS は "connection:0" 等の非UUID識別子を送る。
-- UUID 列のままだと AppendEvents が INSERT で 22P02 になるため TEXT 化する（event_id と同類）。
ALTER TABLE domain_events ALTER COLUMN aggregate_id TYPE TEXT USING aggregate_id::text;
-- aggregate 内順序（local_sequence）の一意性。二層順序保証の下層（world 全体は sequence）。
CREATE UNIQUE INDEX IF NOT EXISTS domain_events_agg_local_uq
  ON domain_events (world_id, aggregate_id, local_sequence);

-- (b) GIN / Expression Index（MVP 13.1 [R10]）。
CREATE INDEX IF NOT EXISTS world_snapshots_payload_gin
  ON world_snapshots USING gin (payload jsonb_path_ops);
CREATE INDEX IF NOT EXISTS domain_events_payload_gin
  ON domain_events USING gin (payload jsonb_path_ops);
-- 頻出 JSON Path 絞り込み補助（event type）。
CREATE INDEX IF NOT EXISTS domain_events_type_idx ON domain_events (type);

-- (c) inventories の一意性（1 owner 1 inventory・MVP 前提）と索引。
CREATE UNIQUE INDEX IF NOT EXISTS inventories_owner_uq
  ON inventories (owner_type, owner_id);

-- (d) actor_runtime_states（新規・付録C / MVP 13章）。DS 生成 → API 永続化。
CREATE TABLE IF NOT EXISTS actor_runtime_states (
    actor_id   TEXT PRIMARY KEY,   -- proto actor_id は string（DS 生成の非UUID識別子を許容）
    world_id   UUID NOT NULL,
    version    BIGINT NOT NULL,
    payload    JSONB NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS actor_runtime_states_world_idx ON actor_runtime_states (world_id);

-- (e) actor_state_projections（新規・WorldState Consumer 用・投影専用）。
--     M2 では API は書かない。将来 WorldState が Writer。テーブルだけ用意。
CREATE TABLE IF NOT EXISTS actor_state_projections (
    actor_id           TEXT PRIMARY KEY,   -- proto actor_id は string
    world_id           UUID NOT NULL,
    projection_version BIGINT NOT NULL,
    payload            JSONB NOT NULL,
    rebuilt_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- (f) item_definitions（新規・Item Definition マスタ・MVP 7.2）。
--     重量は float 回避のため milli 整数、通貨は BIGINT。
CREATE TABLE IF NOT EXISTS item_definitions (
    item_definition_id TEXT PRIMARY KEY,
    tags               TEXT[] NOT NULL DEFAULT '{}',
    stack_limit        INTEGER NOT NULL,
    weight_milli       INTEGER NOT NULL,
    rarity             INTEGER NOT NULL DEFAULT 0,
    base_value         BIGINT NOT NULL DEFAULT 0,
    use_effect         JSONB NOT NULL DEFAULT '{}'::jsonb
);
