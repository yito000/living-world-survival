-- 0002_auth_m1 の巻き戻し（追加した列/索引のみ削除。0001 の最小構成へ戻す）。

DROP INDEX IF EXISTS join_tickets_server_idx;
ALTER TABLE join_tickets
    DROP COLUMN IF EXISTS issued_at,
    DROP COLUMN IF EXISTS nonce,
    DROP COLUMN IF EXISTS build_id,
    DROP COLUMN IF EXISTS world_id;

ALTER TABLE game_servers
    DROP COLUMN IF EXISTS status;

DROP INDEX IF EXISTS refresh_tokens_family_idx;
DROP INDEX IF EXISTS refresh_tokens_token_hash_unique;

ALTER TABLE accounts
    DROP COLUMN IF EXISTS display_name;
