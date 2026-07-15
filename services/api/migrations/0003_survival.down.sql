-- 0003 の逆操作（golang-migrate down）。作成の逆順で戻す。seed 行もテーブル DROP で消える。

-- (5)(4)(3)(2) 新規テーブルを削除。
DROP TABLE IF EXISTS world_blueprints;
DROP TABLE IF EXISTS world_items;
DROP TABLE IF EXISTS resource_node_defs;
DROP TABLE IF EXISTS recipe_ingredients;
DROP TABLE IF EXISTS recipes;

-- (1) item_definitions の M3 追加列を削除（0002 の列は残す）。
ALTER TABLE item_definitions DROP COLUMN IF EXISTS is_instance;
ALTER TABLE item_definitions DROP COLUMN IF EXISTS waste_output;
ALTER TABLE item_definitions DROP COLUMN IF EXISTS consume_hunger;
ALTER TABLE item_definitions DROP COLUMN IF EXISTS primary_tag;

-- (0) id 系の TEXT 化を UUID へ戻す。
-- 注: TEXT 値が UUID 形式でない行（ULID / actor_id "connection:0" 等）が既にある場合は
--     ::uuid キャストに失敗し得る（0002 の down と同種の制約）。実運用の巻き戻しでは
--     先に該当行を退避/削除すること。
ALTER TABLE inbox_dedup     ALTER COLUMN message_id TYPE UUID USING message_id::uuid;
ALTER TABLE currency_ledger ALTER COLUMN owner_id TYPE UUID USING owner_id::uuid;
ALTER TABLE inventories     ALTER COLUMN owner_id TYPE UUID USING owner_id::uuid;
ALTER TABLE inventory_entries DROP CONSTRAINT IF EXISTS inventory_entries_item_instance_id_fkey;
ALTER TABLE inventory_entries ALTER COLUMN item_instance_id TYPE UUID USING item_instance_id::uuid;
ALTER TABLE item_instances    ALTER COLUMN item_instance_id TYPE UUID USING item_instance_id::uuid;
ALTER TABLE inventory_entries
  ADD CONSTRAINT inventory_entries_item_instance_id_fkey
  FOREIGN KEY (item_instance_id) REFERENCES item_instances (item_instance_id);
