-- 0005 の逆操作（golang-migrate down）。作成の逆順で戻す。

-- World Event Template の seed を取り除く（0004 の 13 テンプレは残す）。
DELETE FROM action_templates
 WHERE template_id IN (
   'world_event.great_hunt',
   'world_event.rare_resource',
   'world_event.rare_buyer_rush'
 );

DROP TABLE IF EXISTS world_event_instances;
