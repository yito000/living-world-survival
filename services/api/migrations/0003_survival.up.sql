-- M3: Survival Vertical Slice の永続基盤（06B 3.1）。0001/0002 は最小限で追記する。
-- 通貨・重量は float 禁止・整数（BIGINT / milli 整数, MVP 13.1）。seed は冪等
-- （ON CONFLICT DO NOTHING）で再適用しても重複しない。

-- ---------------------------------------------------------------------------
-- (0) DS 生成の識別子（ULID / actor_id "connection:0" 等）を素直に保持するため id 系を
--     TEXT 化する。0002 で event_id/aggregate_id を TEXT 化済み。M3 では api が
--     inventory_entries/item_instances/world_items を実際に永続 Writer として書く。
--     UUID 列のままだと DS の ULID/actor_id 挿入で 22P02 になるため（既定方針=DS の
--     id系は全て proto string, 06B 6章）owner_id / item_instance_id も TEXT へ拡張する。
-- ---------------------------------------------------------------------------
-- item_instances.item_instance_id は inventory_entries から FK 参照されるため、FK を
-- 一旦落として両側を TEXT 化し、FK を張り直す（型不一致だと再作成できないため）。
ALTER TABLE inventory_entries DROP CONSTRAINT IF EXISTS inventory_entries_item_instance_id_fkey;
ALTER TABLE item_instances    ALTER COLUMN item_instance_id TYPE TEXT USING item_instance_id::text;
ALTER TABLE inventory_entries ALTER COLUMN item_instance_id TYPE TEXT USING item_instance_id::text;
ALTER TABLE inventory_entries
  ADD CONSTRAINT inventory_entries_item_instance_id_fkey
  FOREIGN KEY (item_instance_id) REFERENCES item_instances (item_instance_id);
-- Inventory / 通貨元帳の owner_id は DS の actor_id（string）で引くため TEXT 化。
ALTER TABLE inventories     ALTER COLUMN owner_id TYPE TEXT USING owner_id::text;
ALTER TABLE currency_ledger ALTER COLUMN owner_id TYPE TEXT USING owner_id::text;
-- worldstate 購読土台は event_id(ULID/TEXT) を message_id として At-least-once の重複を
-- 吸収する（inbox_dedup, R9）。UUID 列だと ULID を入れられないため TEXT 化する。
ALTER TABLE inbox_dedup ALTER COLUMN message_id TYPE TEXT USING message_id::text;

-- ---------------------------------------------------------------------------
-- (1) マスタ拡張: item_definitions（0002 で作成済み。M3 の列を追記する）。
--     seed の実体は data/item_definitions.json（A/B 単一の正・Go 側 seeder）で供給し、
--     SQL には item 行を焼き込まない（二重の正を作らないため, 3.8）。
--     primary_tag=主タグ、consume_hunger=Consume 時 Hunger 回復、waste_output=料理で
--     産出する waste 数、is_instance=品質/耐久を持つ個体か。
-- ---------------------------------------------------------------------------
ALTER TABLE item_definitions ADD COLUMN IF NOT EXISTS primary_tag    TEXT    NOT NULL DEFAULT '';
ALTER TABLE item_definitions ADD COLUMN IF NOT EXISTS consume_hunger INTEGER NOT NULL DEFAULT 0;
ALTER TABLE item_definitions ADD COLUMN IF NOT EXISTS waste_output   INTEGER NOT NULL DEFAULT 0;
ALTER TABLE item_definitions ADD COLUMN IF NOT EXISTS is_instance    BOOLEAN NOT NULL DEFAULT FALSE;

-- ---------------------------------------------------------------------------
-- (2) マスタ: recipes / recipe_ingredients（8.4・06A 3.3 と同値。齟齬時は本書=正）。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS recipes (
    recipe_id          TEXT PRIMARY KEY,
    kind               TEXT NOT NULL,            -- known | development | crafted_after_dev
    station_type       TEXT NOT NULL,            -- forge | anvil | cooking_station | farm_plot
    output_item        TEXT,                     -- development は NULL 可
    output_quantity    INTEGER NOT NULL DEFAULT 1,
    craft_seconds      INTEGER NOT NULL,
    unlock_blueprint   TEXT,                     -- development が解放する blueprint_id
    required_blueprint TEXT                      -- 製作に必要な blueprint_id（NULL=常時可）
);
CREATE TABLE IF NOT EXISTS recipe_ingredients (
    recipe_id          TEXT NOT NULL REFERENCES recipes (recipe_id),
    item_definition_id TEXT NOT NULL,
    quantity           INTEGER NOT NULL,
    PRIMARY KEY (recipe_id, item_definition_id)
);

-- recipes seed（8.4 + 精錬。06A 3.3 と数量・時間を一致させること）。
INSERT INTO recipes
    (recipe_id, kind, station_type, output_item, output_quantity, craft_seconds, unlock_blueprint, required_blueprint)
VALUES
    ('stone_pickaxe',       'known',             'anvil',   'stone_pickaxe',      1,  30, NULL,         NULL),
    ('stone_spear',         'known',             'anvil',   'stone_spear',        1,  20, NULL,         NULL),
    ('iron_ingot',          'known',             'forge',   'iron_ingot',         1,  40, NULL,         NULL),
    ('rare_ingot',          'known',             'forge',   'rare_ingot',         1,  60, NULL,         NULL),
    ('iron_spear_research', 'development',       'anvil',   NULL,                 1, 120, 'iron_spear', NULL),
    ('iron_hunting_spear',  'crafted_after_dev', 'anvil',   'iron_hunting_spear', 1,  60, NULL,         'iron_spear'),
    ('rare_weapon_craft',   'crafted_after_dev', 'anvil',   'rare_weapon',        1,  90, NULL,         NULL)
ON CONFLICT (recipe_id) DO NOTHING;

INSERT INTO recipe_ingredients (recipe_id, item_definition_id, quantity)
VALUES
    ('stone_pickaxe',       'stone',      5),
    ('stone_pickaxe',       'wood',       2),
    ('stone_spear',         'stone',      3),
    ('stone_spear',         'wood',       2),
    ('iron_ingot',          'iron_ore',   2),
    ('rare_ingot',          'rare_ore',   2),
    ('iron_spear_research', 'iron_ore',   5),
    ('iron_spear_research', 'rare_ore',   1),
    ('iron_hunting_spear',  'iron_ingot', 3),
    ('iron_hunting_spear',  'wood',       1),
    ('iron_hunting_spear',  'leather',    1),
    ('rare_weapon_craft',   'rare_ingot', 3),
    ('rare_weapon_craft',   'iron_ingot', 5)
ON CONFLICT (recipe_id, item_definition_id) DO NOTHING;

-- ---------------------------------------------------------------------------
-- (3) マスタ: resource_node_defs（8.3・暫定値。DS と同値）。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS resource_node_defs (
    resource_type       TEXT PRIMARY KEY,        -- stone | iron | rare
    drop_item           TEXT NOT NULL,           -- stone->stone, iron->iron_ore, rare->rare_ore
    required_tool_tags  TEXT[] NOT NULL,         -- {tool.mining}
    hardness            INTEGER NOT NULL,
    maximum_amount      INTEGER NOT NULL,
    quality             INTEGER NOT NULL DEFAULT 0,
    regeneration_policy JSONB NOT NULL DEFAULT '{}'::jsonb
);
INSERT INTO resource_node_defs
    (resource_type, drop_item, required_tool_tags, hardness, maximum_amount, quality, regeneration_policy)
VALUES
    ('stone', 'stone',    '{tool.mining}', 2, 50, 0, '{"type":"linear","amount_per_min":5,"cooldown_sec":30}'::jsonb),
    ('iron',  'iron_ore', '{tool.mining}', 4, 30, 0, '{"type":"linear","amount_per_min":3,"cooldown_sec":60}'::jsonb),
    ('rare',  'rare_ore', '{tool.mining}', 6, 10, 2, '{"type":"cooldown","amount_per_min":1,"cooldown_sec":600}'::jsonb)
ON CONFLICT (resource_type) DO NOTHING;

-- ---------------------------------------------------------------------------
-- (4) 永続: world_items（Drop / Carcass / Discard で残す WorldItem, AT-010）。
--     world_item_id / item_instance_id / owner_id は DS の string id を許容するため TEXT。
--     item_instance_id は個体を残す場合のみ設定（存在保証は無いので FK は張らない）。
--     座標は数量/通貨ではないため float(REAL) 許容（13.1）。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS world_items (
    world_item_id      TEXT PRIMARY KEY,
    world_id           UUID NOT NULL,
    item_definition_id TEXT NOT NULL,
    item_instance_id   TEXT,
    quantity           INTEGER NOT NULL DEFAULT 1,
    pos_x REAL NOT NULL DEFAULT 0,
    pos_y REAL NOT NULL DEFAULT 0,
    pos_z REAL NOT NULL DEFAULT 0,
    owner_id           TEXT,
    tags               TEXT[] NOT NULL DEFAULT '{}',
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS world_items_world_idx ON world_items (world_id);

-- ---------------------------------------------------------------------------
-- (5) 永続: world_blueprints（Development 解放・World 共通推奨 8.4）。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS world_blueprints (
    world_id     UUID NOT NULL,
    blueprint_id TEXT NOT NULL,
    unlocked_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (world_id, blueprint_id)
);
