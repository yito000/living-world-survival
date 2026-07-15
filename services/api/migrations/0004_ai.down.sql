-- 0004 の逆操作（golang-migrate down）。作成の逆順で戻す。seed 行はテーブル DROP で消える。

-- (3) actor_state_projections 拡張を戻す（DEFAULT 解除・world_id を UUID へ）。
-- 注: TEXT 値が UUID 形式でない行がある場合は ::uuid キャストに失敗し得る（0003 down と同種）。
ALTER TABLE actor_state_projections ALTER COLUMN projection_version DROP DEFAULT;
ALTER TABLE actor_state_projections
  ALTER COLUMN world_id TYPE UUID USING world_id::uuid;

-- (2)(1) 新規テーブルを削除。
DROP TABLE IF EXISTS ai_decisions;
DROP TABLE IF EXISTS action_templates;
