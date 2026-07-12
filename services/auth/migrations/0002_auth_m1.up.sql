-- M1: Auth 所有テーブルに接続/認証で必要な列・制約を追加する（3.6, MVP第13章）。
-- 0001_init の最小 DDL に積む差分。既存行は dev では空だが、冪等・非破壊で書く。

-- accounts: M1 では account に紐づく表示名を保持する（3.1）。
ALTER TABLE accounts
    ADD COLUMN IF NOT EXISTS display_name TEXT;

-- refresh_tokens: 盗用検知のため token_hash は一意、family ローテーション参照用に
-- (account_id, family_id) を索引する（3.3 / RFC 9700）。
CREATE UNIQUE INDEX IF NOT EXISTS refresh_tokens_token_hash_unique
    ON refresh_tokens (token_hash);
CREATE INDEX IF NOT EXISTS refresh_tokens_family_idx
    ON refresh_tokens (account_id, family_id);

-- game_servers: matchmaking 対象/ドレインを表す状態列（3.5 G2-G4）。
ALTER TABLE game_servers
    ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'active';

-- join_tickets: JoinTicketClaims の全フィールドを DB でも参照/監査できるよう追加する
-- （3.4 / MVP 11.3）。world_id/build_id/nonce/issued_at。既存 0001 は
-- ticket_id, account_id, character_id, server_id, expires_at, used_at を持つ。
ALTER TABLE join_tickets
    ADD COLUMN IF NOT EXISTS world_id   UUID,
    ADD COLUMN IF NOT EXISTS build_id   TEXT,
    ADD COLUMN IF NOT EXISTS nonce      TEXT,
    ADD COLUMN IF NOT EXISTS issued_at  TIMESTAMPTZ NOT NULL DEFAULT now();

-- 単回消費の条件更新（used_at IS NULL AND expires_at > now()）を高速化。
CREATE INDEX IF NOT EXISTS join_tickets_server_idx
    ON join_tickets (server_id);
