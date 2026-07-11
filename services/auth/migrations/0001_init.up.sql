-- Auth 所有テーブル（MVP 13章）。M0 は接続確認に必要な最小構成。
-- 一意制約（email, ticket_id 等）は初期から入れる（MVP 13.1）。

CREATE TABLE IF NOT EXISTS accounts (
    account_id  UUID PRIMARY KEY,
    email       TEXT NOT NULL,
    status      TEXT NOT NULL DEFAULT 'active',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT accounts_email_unique UNIQUE (email)
);

CREATE TABLE IF NOT EXISTS password_credentials (
    account_id     UUID PRIMARY KEY REFERENCES accounts (account_id) ON DELETE CASCADE,
    password_hash  TEXT NOT NULL,
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS refresh_tokens (
    token_id    UUID PRIMARY KEY,
    account_id  UUID NOT NULL REFERENCES accounts (account_id) ON DELETE CASCADE,
    token_hash  TEXT NOT NULL,
    family_id   UUID NOT NULL,
    expires_at  TIMESTAMPTZ NOT NULL,
    revoked_at  TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS refresh_tokens_account_idx ON refresh_tokens (account_id);

CREATE TABLE IF NOT EXISTS game_servers (
    server_id   UUID PRIMARY KEY,
    world_id    UUID NOT NULL,
    build_id    TEXT NOT NULL,
    endpoint    TEXT NOT NULL,
    capacity    INTEGER NOT NULL DEFAULT 0,
    ready       BOOLEAN NOT NULL DEFAULT false,
    last_seen   TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS join_tickets (
    ticket_id    UUID PRIMARY KEY,
    account_id   UUID NOT NULL,
    character_id UUID NOT NULL,
    server_id    UUID NOT NULL,
    expires_at   TIMESTAMPTZ NOT NULL,
    used_at      TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS join_tickets_account_idx ON join_tickets (account_id);
