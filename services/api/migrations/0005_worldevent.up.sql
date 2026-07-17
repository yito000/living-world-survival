-- M5: World Event インスタンス台帳（08B 3.5/3.8）。0001-0004 は変更せず追記する。
-- Owner（付録C / 13章）: world_event_instances = API。Register で PROPOSED 登録し、
-- UpdateState で expected_state 一致を条件に ACTIVE→COMPLETED へ遷移させる。
-- state は worldevent.proto の WorldEventState enum の数値（1=PROPOSED, 2=ACTIVE,
-- 3=COMPLETED, 4=REJECTED）をそのまま持つ。

CREATE TABLE IF NOT EXISTS world_event_instances (
    event_instance_id UUID        PRIMARY KEY,
    proposal_id       TEXT        UNIQUE,                       -- Register の冪等キー
    template_id       TEXT        NOT NULL,
    world_id          TEXT        NOT NULL,                     -- DS 生成 id は非 UUID もあり得る（0004 と同方針）
    region_id         TEXT,
    state             INTEGER     NOT NULL DEFAULT 1,           -- WorldEventState enum（1=PROPOSED）
    params            JSONB       NOT NULL DEFAULT '{}'::jsonb, -- 付録B.2 の提案パラメータ
    stats             JSONB       NOT NULL DEFAULT '{}'::jsonb, -- 終了集計（spawned/harvested/...）
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_world_event_instances_world_state
    ON world_event_instances (world_id, state);
-- 10.4 の同種 Cooldown 判定（直近の同 template の終了時刻）を引くための索引。
CREATE INDEX IF NOT EXISTS idx_world_event_instances_template_time
    ON world_event_instances (world_id, template_id, created_at DESC);

-- ---------------------------------------------------------------------------
-- World Event Template の seed（10.3 の 3 種）。action_templates に相乗りし、
-- 暫定制約値（duration/cap/budget）を definition に持つ。実値の強制は DS 側（08A 3.2）
-- だが、承認検査（3.6）と提案スキーマ（3.3）の Allowed ID 集合はここが正。
-- ---------------------------------------------------------------------------
INSERT INTO action_templates (template_id, version, status, tags, definition) VALUES
  ('world_event.great_hunt', 1, 'active',
   ARRAY['world_event','hunt','event_available'],
   '{"template_id":"world_event.great_hunt","version":1,"kind":"world_event","tags":["world_event","hunt","event_available"],"constraints":{"duration_sec":900,"alive_cap_delta":40,"total_cap":100},"spawn":"rare_deer"}'::jsonb),
  ('world_event.rare_resource', 1, 'active',
   ARRAY['world_event','resource','event_available'],
   '{"template_id":"world_event.rare_resource","version":1,"kind":"world_event","tags":["world_event","resource","event_available"],"constraints":{"duration_sec":900,"node_cap":20,"total_yield_budget":400},"spawn":"rare_ore_node"}'::jsonb),
  ('world_event.rare_buyer_rush', 1, 'active',
   ARRAY['world_event','economy','event_available'],
   '{"template_id":"world_event.rare_buyer_rush","version":1,"kind":"world_event","tags":["world_event","economy","event_available"],"constraints":{"duration_sec":600,"buyer_count":3,"independent_stock":true,"rare_guaranteed":false},"spawn":"rare_buyer"}'::jsonb)
ON CONFLICT (template_id, version) DO NOTHING;
