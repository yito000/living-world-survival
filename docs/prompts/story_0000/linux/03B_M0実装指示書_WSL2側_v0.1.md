---
title: "M0 実装指示書（WSL2 / Linux 側）"
subtitle: "バックエンド・Docker・proto・DB・ローカルCI"
document_id: "IMPL-M0-WSL-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M0 / WSL2側）"
baseline: "Go / Python(FastAPI) / PostgreSQL / NATS JetStream / buf / Blender"
related_document: "03A_M0実装指示書_Windows側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M0 実装指示書：WSL2 / Linux 側 v0.1

本書は M0（基盤）の作業を **WSL2（Ubuntu）側**（バックエンドサービス、Docker、proto生成、DB、ローカルCI、Blenderアセット）に限定して指示する。Unity/クライアントは別冊 **03A（Windows側）** を参照。第0章「分担と連携」は両冊で共通・同一内容。

---

## 0. 分担と連携（共通・両冊同一）

### 0.1 環境別 責務分担

| 領域 | 担当環境 | 主なタスク |
|---|---|---|
| Unity Editor / Client ビルド | **Windows** | プロジェクト設定、クライアント基盤ライブラリ導入、Windows Client ビルド |
| Unity Dedicated Server ビルド | **Windows** | Linux 向けクロスビルド（`-standaloneBuildSubtarget Server`） |
| Unity テスト（EditMode/PlayMode） | **Windows** | `-runTests` をローカル実行 |
| proto の C# 生成物の取り込み | **Windows**（消費） | `unity/SurvivalWorld/Assets/Generated/` を参照しコンパイル |
| Go サービス（auth/api） | **WSL2** | 実装・Lint・テスト・Dockerビルド |
| Python サービス（worldstate/llm-worker） | **WSL2** | 実装・Lint・テスト |
| proto 生成（buf） | **WSL2**（生成） | Go/Python/C# を生成し出力 |
| Docker Compose / DB / NATS | **WSL2** | インフラ起動、マイグレーション、smoke |
| ローカルCI（`make ci`） | **WSL2** | Go/Python/proto/assets を一括実行 |
| Blender アセット生成（headless） | **WSL2**（既定） | `make assets`。Windows Blender でも可（6章） |
| Git / LFS | **両方** | 下記 0.3 の規約に従う |

### 0.2 リポジトリ配置（既定）

- **単一クローンを Windows ファイルシステムに置く**。例: `C:\dev\living-world-survival`
- **WSL2 からは `/mnt/c/dev/living-world-survival` で同じクローンにアクセス**する。
- 理由: Unity は I/O に最も敏感でネイティブ配置が必須。WSL2内配置＋Unity `\\wsl$` 参照は低速なため採らない。
- トレードオフ: `/mnt/c` は WSL2 からの I/O がやや遅い。M0 の Go/Python ビルドでは許容範囲。ホットな反復が重い場合は 5.5 の対処を参照。

> 単一リポジトリを2環境から触るため、改行コードとLFSの規約（0.3）を厳守する。

### 0.3 Git / LFS / 改行コード規約

- Git と **Git LFS を両環境にインストール**し、各自 `git lfs install` を一度実行。
- Windows 側は `git config --global core.autocrlf false`（改行は .gitattributes で制御）。
- リポジトリ直下 `.gitattributes`（**shは必ずLF、ps1はCRLF**）:

```gitattributes
* text=auto eol=lf
*.sh   text eol=lf
*.ps1  text eol=crlf
*.cs   text
*.meta text eol=lf
# Unity/バイナリ資産は LFS
*.png filter=lfs diff=lfs merge=lfs -text
*.jpg filter=lfs diff=lfs merge=lfs -text
*.fbx filter=lfs diff=lfs merge=lfs -text
*.glb filter=lfs diff=lfs merge=lfs -text
*.wav filter=lfs diff=lfs merge=lfs -text
```

- `.sh` が CRLF になると WSL2 で実行不可（`bad interpreter` 等）。上記を最初のコミットに含める。

### 0.4 境界成果物（環境をまたぐファイル）

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| proto → C# 生成コード | WSL2（`buf generate`） | Windows（Unity） | `unity/SurvivalWorld/Assets/Generated/` |
| proto → Go/Python 生成コード | WSL2 | WSL2 | 各サービス配下 |
| Linux Dedicated Server ビルド成果物 | Windows（クロスビルド） | WSL2/Docker（実行） | `unity/SurvivalWorld/Build/Server/` |
| DBスキーマ（migrations） | WSL2 | 参照: 両方 | `services/*/migrations/` |

### 0.5 連携フロー（代表例）

- **proto を変更したとき**: WSL2で `make proto`（`buf generate`）→ `unity/SurvivalWorld/Assets/Generated/` にC#が出力 → Windowsの Unity がコンパイル。生成物のコミット漏れは WSL2 の CI が検出。
- **サーバー起動確認**: Windowsで `unity-build-server` → 生成した Linux バイナリを WSL2/Docker 上で実行（M1以降）。
- **日次**: Windows=Unityの作業、WSL2=`make ci`。両者は同一リポジトリを push/pull で同期。

---

## 1. 対象と前提（WSL2側）

- 環境: Windows 上の **WSL2（Ubuntu 22.04 推奨）** + **Docker Desktop（WSL2 バックエンド）**。
- リポジトリ: `/mnt/c/dev/living-world-survival`（0.2）。
- 本書の完了で、Postgres/NATS がローカル起動し、Go/Python/proto/assets の **`make ci`** が緑になり、`make smoke` で全サービスが Docker 上で health を返す状態にする。

### 1.1 WSL2側 DoD

- `make bootstrap` で必要ツールが揃っていることを確認できる。
- `make up` で Postgres/NATS が healthy になる。
- `make migrate` で DB マイグレーションが適用される。
- **`make ci`（proto/lint/test/assets）が成功**する。
- `make smoke` で auth/api/worldstate/llm-worker が起動し health を返す。
- proto 生成物が `unity/SurvivalWorld/Assets/Generated/`（C#）と各サービス（Go/Python）へ出力され、ドリフト検査に通る。

---

## 2. 必要ツール（WSL2）

| ツール | 目安 | 用途 | 導入 |
|---|---|---|---|
| Docker Desktop（WSL2連携） | 最新 | Postgres/NATS/サービス | Windows側でインストール→対象ディストロで Integration を有効化 |
| Git + Git LFS | 最新 | 版管理 | `sudo apt install git git-lfs` → `git lfs install` |
| Go | 1.22+ | auth/api | 公式tarball or apt |
| golangci-lint | 最新 | Go Lint | 公式インストールスクリプト |
| Python | 3.11+ | worldstate/llm-worker | `apt` or pyenv |
| uv | 最新 | Python依存管理 | `pip install uv` or 公式 |
| buf | 最新 | proto lint/generate/breaking | 公式バイナリ |
| golang-migrate | 最新 | DBマイグレーション | 公式バイナリ |
| Blender | 4.x | アセット生成(headless) | `sudo apt install blender`（または公式）。6章 |
| make / bash | - | ローカルCI | 標準 |
| pre-commit | 最新 | コミット時整形/Lint | `pip install pre-commit` |
| （任意）act | 最新 | GitHub Actions ローカル実行 | 9章 |

> Docker Desktop の Settings → Resources → WSL Integration で、使用するUbuntuディストロを有効化しておくこと。`docker` / `docker compose` が WSL2 から使えるようになる。

---

## 3. リポジトリ構成（WSL2側が主に扱う部分）

```text
living-world-survival/
├─ Makefile                      # ★ローカルCIのエントリポイント（WSL2で実行）
├─ .gitattributes .gitignore .env.example
├─ .pre-commit-config.yaml
├─ infra/
│  ├─ docker-compose.yml         # postgres, nats (+ 各サービス)
│  └─ nats/nats.conf
├─ proto/
│  ├─ buf.yaml  buf.gen.yaml
│  └─ survival/v1/*.proto
├─ services/
│  ├─ auth/   (Go: cmd/authd, internal, migrations, Dockerfile)
│  ├─ api/    (Go: cmd/apid, internal, migrations, Dockerfile)
│  ├─ worldstate/ (Python/FastAPI: app, tests, Dockerfile)
│  └─ llm-worker/ (Python: worker, tests, Dockerfile)
├─ assets-pipeline/ (generate.py, validate.py, tests)
├─ scripts/         (ci_go.sh, ci_python.sh, ci_proto.sh, ci_assets.sh, migrate.sh, smoke.sh, check_tools.sh)
└─ unity/SurvivalWorld/           # ★Windows側が主管理。proto C#出力先のみ共有
   └─ Assets/Generated/           # buf generate の C# 出力先
```

---

## 4. ローカルインフラ（Docker Compose）

`infra/docker-compose.yml`（抜粋）:

```yaml
services:
  postgres:
    image: postgres:16
    environment:
      POSTGRES_USER: survival
      POSTGRES_PASSWORD: survival
      POSTGRES_DB: survival
    ports: ["5432:5432"]
    volumes: ["pgdata:/var/lib/postgresql/data"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U survival"]
      interval: 5s
      timeout: 3s
      retries: 20

  nats:
    image: nats:2
    command: ["-js", "-m", "8222"]        # JetStream + monitoring
    ports: ["4222:4222", "8222:8222"]
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:8222/healthz"]
      interval: 5s
      timeout: 3s
      retries: 20

  auth:
    build: ../services/auth
    env_file: ../.env
    depends_on: { postgres: { condition: service_healthy } }
    ports: ["8081:8081"]

  api:
    build: ../services/api
    env_file: ../.env
    depends_on:
      postgres: { condition: service_healthy }
      nats: { condition: service_healthy }
    ports: ["8082:8082"]

  worldstate:
    build: ../services/worldstate
    env_file: ../.env
    depends_on:
      nats: { condition: service_healthy }
      postgres: { condition: service_healthy }
    ports: ["8083:8083"]

  llm-worker:
    build: ../services/llm-worker
    env_file: ../.env
    depends_on: { nats: { condition: service_healthy } }

volumes:
  pgdata:
```

- M0では auth/api/worldstate は **health エンドポイント + DB/NATS接続確認だけの最小実装**で良い。
- `.env.example` を用意し `DATABASE_URL`, `NATS_URL`, 各 `*_PORT`, `JWT_SIGNING_KEY`(dev用) を定義。実値は `.env`（gitignore）。

---

## 5. ローカルCI（Makefile）

CIサーバーを使わず、**Makefile を単一エントリポイント**にする。WSL2 で実行する。

`Makefile`（抜粋）:

```makefile
.DEFAULT_GOAL := help

help: ## ターゲット一覧
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | sort | \
	  awk 'BEGIN{FS=":.*?## "}{printf "  \033[36m%-22s\033[0m %s\n",$$1,$$2}'

bootstrap: ## 必要ツールの存在確認
	@bash scripts/check_tools.sh

up: ## ローカルインフラ起動(postgres/nats)
	docker compose -f infra/docker-compose.yml up -d postgres nats

down: ## 停止
	docker compose -f infra/docker-compose.yml down

migrate: ## DBマイグレーション適用
	bash scripts/migrate.sh up

proto: ## proto生成 + drift検査
	bash scripts/ci_proto.sh

lint: ## 全言語Lint
	bash scripts/ci_go.sh lint
	bash scripts/ci_python.sh lint

test: ## 全言語ユニットテスト
	bash scripts/ci_go.sh test
	bash scripts/ci_python.sh test

build: ## サービスビルド(Linuxコンテナ)
	docker compose -f infra/docker-compose.yml build auth api worldstate llm-worker

assets: ## Blenderアセット生成+検査
	bash scripts/ci_assets.sh

smoke: up migrate build ## 全サービス起動+health確認
	docker compose -f infra/docker-compose.yml up -d
	bash scripts/smoke.sh

ci: proto lint test assets ## ★ローカルCI一括(サーバー不要)
	@echo "Local CI passed."
```

> `make ci` は Go/Python/proto/assets を対象にする（Unityは含めない。Unityは Windows側 03A が担当）。

### 5.1 実行順序（推奨）

```text
初回:  make bootstrap → make up → make migrate
日常:  （コード変更）→ make ci
統合:  make smoke        # Docker上で全サービス起動確認
```

### 5.2 Go（auth / api）— `scripts/ci_go.sh`

```bash
gofmt -l .                      # 差分あれば失敗
go vet ./...
golangci-lint run ./...
go test ./... -race -count=1 -coverprofile=coverage.out
GOOS=linux GOARCH=amd64 go build ./...
```

- `.golangci.yml`（errcheck, govet, staticcheck, gofmt, revive 等）。
- 通貨は BIGINT（整数）。float 混入を静的解析で警戒（MVP第13.1）。

### 5.3 Python（worldstate / llm-worker）— `scripts/ci_python.sh`

```bash
uv sync                         # or python -m venv + pip install -e .[dev]
ruff check .
ruff format --check .
mypy app worker                 # 任意だが推奨
pytest -q
python -c "import app.main"     # import時例外がないこと
```

- LLM 実呼び出しは M0 ではモック。重い処理を request handler に置かない（BSD第4.3, R7）。

### 5.4 proto / gRPC（buf）— `scripts/ci_proto.sh`

```bash
buf lint
buf generate                                  # Go/Python/C# を生成
git diff --exit-code -- services proto unity/SurvivalWorld/Assets/Generated   # ドリフト検出
buf breaking --against '.git#branch=main'     # mainがある場合
```

`buf.gen.yaml`（C#の出力先を Unity プロジェクトへ）:

```yaml
version: v2
plugins:
  - remote: buf.build/protocolbuffers/go
    out: services/gen/go
  - remote: buf.build/grpc/go
    out: services/gen/go
  - remote: buf.build/protocolbuffers/python
    out: services/gen/python
  - remote: buf.build/grpc/csharp        # gRPC C#
    out: unity/SurvivalWorld/Assets/Generated
  - remote: buf.build/protocolbuffers/csharp
    out: unity/SurvivalWorld/Assets/Generated
```

- **proto をメッセージ/RPCの唯一の正**とし、MVP第14章の Command/RPC/NATS Subject をここで表現。
- 生成した C# は Windows 側 Unity がコンパイルする（0.4/0.5）。**コミット漏れが最頻の CI 失敗要因**。

### 5.5 /mnt/c I/O が重いときの対処

- Docker のビルド/実行は Docker Desktop 側で処理されるため影響小。
- Go/Python のビルドキャッシュ（`GOCACHE`, `uv` cache）を WSL2 のホーム（`~/.cache`）に置く（`/mnt/c` を避ける）。
- それでも重い場合のみ、CIワークだけ WSL2 ホームにワークツリーを別途 clone する運用を検討（ただし単一リポジトリ原則を崩さないよう、正はWindows側とし push/pull で同期）。

---

## 6. Blender アセット（headless）— `scripts/ci_assets.sh`

```bash
BLENDER=${BLENDER:-blender}     # WSL2の blender。Windows Blenderを使う場合は BLENDER=blender.exe
"$BLENDER" --background --python assets-pipeline/generate.py -- \
  --seed 1 --module-size 4 --out build/assets
python assets-pipeline/validate.py --in build/assets
```

- 決定的生成（同じ seed/size で同一 asset_id/version）。検査は manifest/socket/collider/triangle/negative scale（MVP第15章）。
- WSL2 に Blender を入れられない場合は `make assets BLENDER=blender.exe`（Windowsの Blender を WSL2 から呼ぶ）でも可。

---

## 7. DBマイグレーション

- `services/{auth,api}/migrations/` に `NNNN_name.up.sql` / `.down.sql`（golang-migrate 形式）。
- `scripts/migrate.sh` が `DATABASE_URL`（例: `postgres://survival:survival@localhost:5432/survival?sslmode=disable`）で `migrate ... up` を実行。
- M0 は接続確認に必要な最小テーブル（accounts, game_servers, worlds, outbox_messages 等）から。スキーマは MVP第13章を正とする。
- 通貨列は BIGINT、一意制約（email, idempotency_key, event_id, ticket_id 等）を初期から入れる。

---

## 8. WSL2側 実装順序

| # | タスク | 完了確認 |
|---|---|---|
| L-1 | Ubuntu(WSL2) + Docker Desktop 連携有効化、`git lfs install` | `docker ps` 動作 |
| L-2 | `/mnt/c/dev/living-world-survival` を認識（Windowsのクローンを共有） | `ls` で内容確認 |
| L-3 | `infra/docker-compose.yml`（postgres/nats）+ `make up` | 両者 healthy |
| L-4 | `proto/` 初期定義 + `buf` 生成 + drift検査（`make proto`） | 生成物が各所へ出力 |
| L-5 | Go auth/api 雛形（health, DB/NATS接続）+ Dockerfile | `make build` 緑 |
| L-6 | Python worldstate/llm-worker 雛形（health, NATS購読）+ Dockerfile | import/pytest 緑 |
| L-7 | Makefile/scripts/pre-commit/check_tools | `make ci` 緑 |
| L-8 | DBマイグレーション初期 + `make migrate` | テーブル作成 |
| L-9 | `make smoke`（全サービス起動+health） | 全 health 200 |
| L-10 | Blender generate/validate + `make assets` | 生成物が検査通過 |

---

## 9. 将来のホスト型CIへの移行（任意）

ローカルCIと同じ `make` target を呼ぶ GitHub Actions を用意しておけば、ランナー入手後にそのまま有効化できる。

`.github/workflows/ci.yml`（バックエンドのみ・Unity除く）:

```yaml
name: ci
on: [push, pull_request]
jobs:
  backend:
    runs-on: ubuntu-latest
    services:
      postgres:
        image: postgres:16
        env: { POSTGRES_USER: survival, POSTGRES_PASSWORD: survival, POSTGRES_DB: survival }
        ports: ["5432:5432"]
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-go@v5
        with: { go-version: '1.22' }
      - uses: actions/setup-python@v5
        with: { python-version: '3.11' }
      - run: make proto lint test assets   # ← ローカルと同じ target
```

- Unityビルドはこの段階では含めない（ライセンス/ランナーが別途必要）。
- ローカルで試すには `act -j backend`。

---

## 10. 落とし穴（WSL2側）

- **Docker Desktop の WSL Integration 未有効**だと `docker` が使えない。Settings で対象ディストロを有効化。
- **`.sh` が CRLF**だと `bad interpreter`。`.gitattributes` で LF 固定（0.3）。
- **proto 生成物のコミット漏れ**が最頻の CI 失敗。`git diff --exit-code` で必ず検出。C# 出力先（`unity/SurvivalWorld/Assets/Generated`）も対象に含める。
- **`/mnt/c` の I/O 遅延**。ビルドキャッシュは WSL2 ホームへ（5.5）。
- **NATS の JetStream 未有効**（`-js` 忘れ）だと永続ストリームが使えない。compose の command を確認。
- **golang-migrate/buf のパス**が通っているか（`make bootstrap` で確認）。

---

## 参考資料

[R8] [NATS JetStream](https://docs.nats.io/nats-concepts/jetstream)
[R9] [NATS JetStream Consumers](https://docs.nats.io/nats-concepts/jetstream/consumers)
[R10] [PostgreSQL JSON types](https://www.postgresql.org/docs/current/datatype-json.html)
[R11] [PostgreSQL table partitioning](https://www.postgresql.org/docs/current/ddl-partitioning.html)
[R-BUF] [Buf docs](https://buf.build/docs)
[R-MIGRATE] [golang-migrate](https://github.com/golang-migrate/migrate)
[R7] [FastAPI Background Tasks caveat](https://fastapi.tiangolo.com/tutorial/background-tasks/)
[R14] [Blender Python: --background --python](https://docs.blender.org/api/current/info_tips_and_tricks.html)
