# M0 ローカルCI エントリポイント（WSL2 で実行）— 03B 5章
# CIサーバーを使わず、この Makefile を単一エントリポイントにする。

.DEFAULT_GOAL := help
COMPOSE := docker compose -f infra/docker-compose.yml

.PHONY: help bootstrap up down stop-ds migrate proto lint test build assets smoke e2e-m2 e2e-m6 ci clean logs \
        load load-at020 soak-short soak-full recovery security ci-hardening reports

help: ## ターゲット一覧
	@grep -E '^[a-zA-Z0-9_-]+:.*?## .*$$' $(MAKEFILE_LIST) | sort | \
	  awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-22s\033[0m %s\n",$$1,$$2}'

bootstrap: ## 必要ツールの存在確認
	@bash scripts/check_tools.sh

# up は --wait で postgres の healthcheck(pg_isready) が healthy になるまで待つ。
# これで直後の migrate が「起動中でクエリ不可」タイミングに当たらない。
up: ## ローカルインフラ起動(postgres/nats)
	$(COMPOSE) up -d --wait postgres nats

down: stop-ds ## 停止（ローカルDSプロセス + コンテナ）
	$(COMPOSE) down

stop-ds: ## ローカルDS(survival-server.x86_64)を停止（Graceful→強制）
	@bash scripts/stop_ds.sh

migrate: ## DBマイグレーション適用
	@bash scripts/migrate.sh up

proto: ## proto生成 + drift検査
	@bash scripts/ci_proto.sh

lint: ## 全言語Lint
	@bash scripts/ci_go.sh lint
	@bash scripts/ci_python.sh lint

test: ## 全言語ユニットテスト
	@bash scripts/ci_go.sh test
	@bash scripts/ci_python.sh test

build: ## サービスビルド(Linuxコンテナ)
	$(COMPOSE) build auth api worldstate llm-worker

assets: ## Blenderアセット生成+検査
	@bash scripts/ci_assets.sh

smoke: up migrate build ## 全サービス起動+health確認
	$(COMPOSE) up -d
	@bash scripts/smoke.sh

e2e-m2: ## M2手動E2Eスモーク(DS模擬→実apid: bootstrap→AppendEvents→outbox/NATS→snapshot→復元)
	@bash scripts/e2e_m2_smoke.sh

e2e-m6: ## M6手動E2Eスモーク(DS模擬→実apid: RegisterBuyer→CommitPurchase→冪等再送→Despawn→NATS)
	@cd services/api && go run ./cmd/m6check

# --- M7 Hardening（10B 3.6） ------------------------------------------------
# 重い順: soak-full(4h) > load-at020 > soak-short(10分) > recovery > security。
# security は軽量・高速なので ci に含める。soak-full は CI に載せない（PR を止める）。

load: ## M7 負荷試験(ローカルスケール PLAYERS=2 AI=20)+Gate判定
	@bash scripts/load_test.sh
	@python3 scripts/load_assert.py

load-at020: ## M7 負荷試験(AT-020 目標スケール 16Player/20AI/80Animals)+Gate判定
	@PLAYERS=16 AI=20 ANIMALS=80 bash scripts/load_test.sh
	@python3 scripts/load_assert.py

soak-short: ## M7 短縮Soak(10分)+リーク/滞留判定 — nightly 用
	@SOAK_MINUTES=$${SOAK_MINUTES:-10} bash scripts/soak.sh
	@python3 scripts/soak_assert.py "$$(ls -t build/reports/soak_*.csv | head -1)"

soak-full: ## M7 full Soak(既定4時間, 第18.1) — 夜間/手動のみ。CI には載せない
	@SOAK_HOURS=$${SOAK_HOURS:-4} bash scripts/soak.sh
	@python3 scripts/soak_assert.py "$$(ls -t build/reports/soak_*.csv | head -1)"

recovery: ## M7 再起動復旧テスト(5シナリオ, AT-018/019)+判定
	@bash scripts/recovery_test.sh
	@python3 scripts/recovery_assert.py

security: ## M7 Security検査(静的スキャン+ログ秘匿+Integration, MVP-SEC-001〜009)
	@bash scripts/security_scan.sh
	@bash scripts/log_secret_scan.sh
	@bash scripts/ci_go.sh security

ci-hardening: security recovery load ## ★M7追加分を一括(重い soak-full は除外)
	@echo "M7 hardening checks passed."

reports: ## build/reports/ の最新レポートを一覧
	@ls -lt build/reports/ 2>/dev/null || echo "レポートはまだありません（make load / soak-short / recovery / security）"

ci: proto lint test assets security ## ★ローカルCI一括(サーバー不要)
	@echo "Local CI passed."

logs: ## サービスログ表示
	$(COMPOSE) logs -f --tail=100

clean: ## コンテナ・生成物・キャッシュ削除
	-$(COMPOSE) down -v
	rm -rf build services/*/coverage.out
