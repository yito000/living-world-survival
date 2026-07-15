# M0 ローカルCI エントリポイント（WSL2 で実行）— 03B 5章
# CIサーバーを使わず、この Makefile を単一エントリポイントにする。

.DEFAULT_GOAL := help
COMPOSE := docker compose -f infra/docker-compose.yml

.PHONY: help bootstrap up down stop-ds migrate proto lint test build assets smoke e2e-m2 ci clean logs

help: ## ターゲット一覧
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | sort | \
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

ci: proto lint test assets ## ★ローカルCI一括(サーバー不要)
	@echo "Local CI passed."

logs: ## サービスログ表示
	$(COMPOSE) logs -f --tail=100

clean: ## コンテナ・生成物・キャッシュ削除
	-$(COMPOSE) down -v
	rm -rf build services/*/coverage.out
