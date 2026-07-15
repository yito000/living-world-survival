-- 0002 の逆操作（golang-migrate down）。作成の逆順で戻す。

DROP TABLE IF EXISTS item_definitions;
DROP TABLE IF EXISTS actor_state_projections;
DROP TABLE IF EXISTS actor_runtime_states;

DROP INDEX IF EXISTS inventories_owner_uq;

DROP INDEX IF EXISTS domain_events_type_idx;
DROP INDEX IF EXISTS domain_events_payload_gin;
DROP INDEX IF EXISTS world_snapshots_payload_gin;

DROP INDEX IF EXISTS domain_events_agg_local_uq;
-- event_id / aggregate_id を UUID へ戻す（0001 の型）。
-- 注: TEXT 値が UUID 形式でない行（ULID / "connection:0" 等）が既にある場合は失敗し得る。
ALTER TABLE domain_events ALTER COLUMN aggregate_id TYPE UUID USING aggregate_id::uuid;
ALTER TABLE domain_events ALTER COLUMN event_id TYPE UUID USING event_id::uuid;
