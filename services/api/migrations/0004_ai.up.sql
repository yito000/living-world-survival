-- M4: AI 投影・テンプレ配信・判断履歴の永続基盤（07B 3.1）。0001-0003 は変更せず追記する。
-- 通貨・重量は float 禁止・整数（MVP 13.1）。seed は冪等（ON CONFLICT DO NOTHING）。
-- Owner（付録C）: action_templates=WorldState / ai_decisions=WorldState+LLM Worker /
-- actor_state_projections=WorldState Consumer。worldstate は domain_events を直接書かない。

-- ---------------------------------------------------------------------------
-- (1) action_templates（Owner: WorldState / MVP 13・9.3）。
--     template_id+version を PK に世代管理し、status で active/draft/retired を切替。
--     definition JSONB は基本 7.3 のスキーマ（tags/preconditions/interrupts/steps/
--     min-max_duration_sec）。tags 列は definition.tags のミラー（GIN 絞り込み用）。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS action_templates (
    template_id TEXT        NOT NULL,
    version     INTEGER     NOT NULL,
    status      TEXT        NOT NULL DEFAULT 'active',   -- active / draft / retired
    tags        TEXT[]      NOT NULL DEFAULT '{}',
    definition  JSONB       NOT NULL,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (template_id, version)
);
CREATE INDEX IF NOT EXISTS idx_action_templates_status ON action_templates (status);
CREATE INDEX IF NOT EXISTS idx_action_templates_tags   ON action_templates USING GIN (tags);

-- ---------------------------------------------------------------------------
-- (2) ai_decisions（Owner: WorldState / LLM Worker / MVP 13・14.3）。
--     decision_id は一意（PK, ULID/合成 id）。state_version=personal_state_version。
--     template_version は B.1 の突合用（proto は state_version のみ・07B 落とし穴5）。
--     status は requested/produced/applied/rejected/superseded を単一行で遷移。
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS ai_decisions (
    decision_id      TEXT        PRIMARY KEY,
    actor_id         TEXT        NOT NULL,
    state_version    BIGINT      NOT NULL DEFAULT 0,     -- personal_state_version（proto state_version）
    template_id      TEXT        NOT NULL DEFAULT '',
    template_version INTEGER,                            -- DS 突合用（NULL 可）
    status           TEXT        NOT NULL,
    payload          JSONB       NOT NULL DEFAULT '{}'::jsonb,  -- ActionDecision / 候補 等
    created_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_ai_decisions_actor ON ai_decisions (actor_id, created_at DESC);

-- ---------------------------------------------------------------------------
-- (3) actor_state_projections 拡張（Owner: WorldState Consumer）。0002 で作成済み。
--     M4 で worldstate が Writer になる。DS 生成の world_id（非 UUID もあり得る, 06B 6章）を
--     素直に保持するため world_id を TEXT へ拡張し、投影の初期 version を DEFAULT 0 にする。
-- ---------------------------------------------------------------------------
ALTER TABLE actor_state_projections
  ALTER COLUMN world_id TYPE TEXT USING world_id::text;
ALTER TABLE actor_state_projections
  ALTER COLUMN projection_version SET DEFAULT 0;

-- ---------------------------------------------------------------------------
-- (4) action_templates seed（MVP 9.3 の 13 テンプレ）。tags は 9.3 の「主要Tag/条件」、
--     definition は基本 7.3 のスキーマ。status=active。version=1。冪等 INSERT。
-- ---------------------------------------------------------------------------
INSERT INTO action_templates (template_id, version, status, tags, definition) VALUES
  ('survival.eat_owned_food', 1, 'active',
   ARRAY['hunger_high','food_owned','food'],
   '{"template_id":"survival.eat_owned_food","version":1,"tags":["hunger_high","food_owned","food"],"preconditions":["needs.hunger > 50","inventory.food_count > 0"],"interrupts":["health_critical"],"steps":["SelectFood","ConsumeFood"],"min_duration_sec":5,"max_duration_sec":60}'::jsonb),
  ('survival.acquire_food_hunt', 1, 'active',
   ARRAY['hunger_high','weapon_owned','animal_available','food'],
   '{"template_id":"survival.acquire_food_hunt","version":1,"tags":["hunger_high","weapon_owned","animal_available","food"],"preconditions":["needs.hunger > 60","inventory.weapon_count > 0"],"interrupts":["health_critical","target_missing","path_failed"],"steps":["FindAnimal","MoveTo","Hunt","Butcher"],"min_duration_sec":30,"max_duration_sec":600}'::jsonb),
  ('survival.cook_meat', 1, 'active',
   ARRAY['raw_meat_owned','cooking_station','food'],
   '{"template_id":"survival.cook_meat","version":1,"tags":["raw_meat_owned","cooking_station","food"],"preconditions":["inventory.raw_meat_count > 0"],"interrupts":["health_critical","target_missing"],"steps":["MoveToStation","Cook"],"min_duration_sec":15,"max_duration_sec":300}'::jsonb),
  ('mining.acquire_iron', 1, 'active',
   ARRAY['iron_needed','pickaxe_owned','earn'],
   '{"template_id":"mining.acquire_iron","version":1,"tags":["iron_needed","pickaxe_owned","earn"],"preconditions":["inventory.pickaxe_count > 0"],"interrupts":["health_critical","target_missing","path_failed"],"steps":["FindOreNode","MoveTo","Mine"],"min_duration_sec":20,"max_duration_sec":600}'::jsonb),
  ('smithing.craft_stone_spear', 1, 'active',
   ARRAY['no_weapon','stone_owned','wood_owned'],
   '{"template_id":"smithing.craft_stone_spear","version":1,"tags":["no_weapon","stone_owned","wood_owned"],"preconditions":["inventory.weapon_count == 0","inventory.stone_count > 0","inventory.wood_count > 0"],"interrupts":["health_critical"],"steps":["ReserveMaterials","Craft"],"min_duration_sec":15,"max_duration_sec":300}'::jsonb),
  ('development.unlock_spear', 1, 'active',
   ARRAY['blueprint_locked','materials_available'],
   '{"template_id":"development.unlock_spear","version":1,"tags":["blueprint_locked","materials_available"],"preconditions":["blueprint.spear.locked == true"],"interrupts":["health_critical","target_missing"],"steps":["MoveToForge","Research"],"min_duration_sec":30,"max_duration_sec":600}'::jsonb),
  ('smithing.craft_spear', 1, 'active',
   ARRAY['weapon_needed','blueprint_unlocked'],
   '{"template_id":"smithing.craft_spear","version":1,"tags":["weapon_needed","blueprint_unlocked"],"preconditions":["blueprint.spear.unlocked == true"],"interrupts":["health_critical","target_missing"],"steps":["ReserveMaterials","MoveToForge","Craft"],"min_duration_sec":20,"max_duration_sec":400}'::jsonb),
  ('economy.visit_buyer', 1, 'active',
   ARRAY['wanted_item','buyer_available','cash_available','earn'],
   '{"template_id":"economy.visit_buyer","version":1,"tags":["wanted_item","buyer_available","cash_available","earn"],"preconditions":["wallet.cash > 0"],"interrupts":["health_critical","target_missing","path_failed"],"steps":["FindBuyer","MoveTo","Purchase"],"min_duration_sec":20,"max_duration_sec":600}'::jsonb),
  ('economy.sell_surplus', 1, 'active',
   ARRAY['inventory_overflow','sellable_item','sell','wealth'],
   '{"template_id":"economy.sell_surplus","version":1,"tags":["inventory_overflow","sellable_item","sell","wealth"],"preconditions":["inventory.free_slots < 3","inventory.sellable_count > 0"],"interrupts":["health_critical","target_missing","path_failed"],"steps":["SelectSellableItem","FindBuyer","MoveTo","RequestSale"],"min_duration_sec":20,"max_duration_sec":600}'::jsonb),
  ('inventory.discard_low_value', 1, 'active',
   ARRAY['inventory_overflow','no_buyer','cleanup'],
   '{"template_id":"inventory.discard_low_value","version":1,"tags":["inventory_overflow","no_buyer","cleanup"],"preconditions":["inventory.free_slots < 1"],"interrupts":["health_critical"],"steps":["SelectLowValueItem","DropItem"],"min_duration_sec":5,"max_duration_sec":60}'::jsonb),
  ('cleaning.clean_nearby', 1, 'active',
   ARRAY['cleanliness_high','waste_nearby','cleanup'],
   '{"template_id":"cleaning.clean_nearby","version":1,"tags":["cleanliness_high","waste_nearby","cleanup"],"preconditions":["cleanliness.pressure > threshold","waste.nearby_count > 0"],"interrupts":["health_critical","target_missing"],"steps":["FindWaste","MoveTo","CleanWaste"],"min_duration_sec":15,"max_duration_sec":300}'::jsonb),
  ('worldevent.join', 1, 'active',
   ARRAY['event_available','risk_acceptable'],
   '{"template_id":"worldevent.join","version":1,"tags":["event_available","risk_acceptable"],"preconditions":["event.available == true"],"interrupts":["health_critical","path_failed"],"steps":["PrepareEquipment","MoveToRegion","JoinEvent"],"min_duration_sec":30,"max_duration_sec":900}'::jsonb),
  ('safety.idle_at_camp', 1, 'active',
   ARRAY['fallback'],
   '{"template_id":"safety.idle_at_camp","version":1,"tags":["fallback"],"preconditions":[],"interrupts":["health_critical"],"steps":["MoveToCamp","Idle"],"min_duration_sec":10,"max_duration_sec":120}'::jsonb)
ON CONFLICT (template_id, version) DO NOTHING;
