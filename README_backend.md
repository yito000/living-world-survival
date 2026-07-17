# Living World Survival — バックエンド（WSL2 側）M0

このリポジトリの **バックエンド（Go / Python / Docker / proto / DB / assets）** は
WSL2 側で管理する。Unity クライアント / サーバービルドは Windows 側（`unity/`,
`scripts/unity_*.ps1`）が管理する。分担の詳細は
`docs/prompts/03B_M0実装指示書_WSL2側_v0.1.md` を参照。

## ツールチェーン（mise で版固定・推奨）

他プロジェクトと Go/Python/pip 版が衝突しないよう、`mise.toml` でこの repo 専用に
版を固定している。mise はディレクトリに入ると自動でその版へ切り替える。

```bash
mise trust && mise install    # go/python/uv/buf/golangci-lint/migrate/pre-commit を導入
eval "$(mise activate bash)"  # PATH 反映（shell rc に追記推奨。zsh は bash→zsh）
```

- `docker`(Docker Desktop) と `blender`(apt/公式) は OS 側で管理（mise 対象外）。
- 版の再現性: `lockfile = true` により `mise install` 時に `mise.lock` が生成される。**コミット推奨**。
- GitHub レート制限が出る場合は `GITHUB_TOKEN`（または `MISE_GITHUB_TOKEN`）を設定する。
- mise を使わない場合は個別導入でも可（`make bootstrap` で不足を確認）。

## クイックスタート（WSL2）

```bash
cp .env.example .env          # 実値を設定
make bootstrap                # 必要ツール確認
make up                       # postgres / nats 起動
make migrate                  # DB マイグレーション
make ci                       # proto + lint + test + assets（サーバー不要）
make smoke                    # 全サービスを Docker 起動し health 確認
```

## ディレクトリ

| パス | 内容 |
|---|---|
| `infra/` | docker-compose（postgres/nats/services）, nats.conf |
| `proto/` | buf 設定と `survival/v1/*.proto`（メッセージ/RPC の唯一の正） |
| `services/auth`, `services/api` | Go サービス（health + DB/NATS 接続, migrations） |
| `services/worldstate`, `services/llm-worker` | Python サービス（FastAPI / NATS consumer） |
| `services/gen/` | `buf generate` の Go/Python 出力先（コミットしてドリフト検査） |
| `assets-pipeline/` | Blender 生成（generate.py）と検査（validate.py） |
| `scripts/` | ローカル CI シェル（`ci_*.sh`, `migrate.sh`, `smoke.sh` 等） |

C# の proto 生成物は `unity/SurvivalWorld/Assets/Generated/` に出力される（Windows が消費）。

## Blender アセット生成（Windows / Linux 両対応）

`generate.py` / `validate.py` は共通。呼び出し口だけ環境ごとに用意している。

- **Linux / WSL2**: `make assets`（内部 `scripts/ci_assets.sh`）。
  `blender` が無ければ **WSL interop 経由で Windows の `blender.exe` を自動検出**して使う。
  明示指定は `make assets BLENDER=blender.exe` または `BLENDER=/mnt/c/.../blender.exe`。
- **Windows ネイティブ（PowerShell / cmd）**: `scripts/assets.ps1`。
  ```powershell
  .\scripts\assets.ps1                     # Blender を自動検出
  .\scripts\assets.ps1 -Seed 1 -ModuleSize 4 -Out build/assets
  .\scripts\assets.ps1 -Blender "C:\Program Files\Blender Foundation\Blender 4.5\blender.exe"
  ```
  cmd からは `powershell -ExecutionPolicy Bypass -File scripts\assets.ps1`。
  検査は standalone Python（`py`/`python`）優先、無ければ Blender 同梱 Python で実行。

> `scripts/assets.ps1` は **ASCII のみ + UTF-8 BOM + CRLF** で保存している。Windows
> PowerShell 5.1 は BOM 無しスクリプトを ANSI コードページ（日本語環境=CP932）として
> 読むため、非ASCIIバイトがあるとパースが壊れる。編集時はこの規約を維持すること。

## M7 Hardening（要件トレーサビリティ）

M7 は新機能を足さず、M1〜M6 を計測・検証して RC 品質へ上げる。DoD の
「要件 ID ↔ 受入試験 ↔ 検証手段」の対応表を以下に置く（10B 1.1 / 第19.1）。
実測値は `build/reports/`（gitignore・実行時生成）へ出る。

| 要件 / AT | 検証手段（コマンド） | 実装 |
|---|---|---|
| MVP-SEC-001 経路を公開しない | `make security` | `scripts/security_scan.sh`（RC プロファイルの実効 port を `docker compose config` で確認） |
| MVP-SEC-002 秘匿値をログに出さない | `make security` | `obs` のキー名 redaction（Go/Python）＋ `scripts/log_secret_scan.sh` |
| MVP-SEC-003 Refresh ローテーション/再利用検知 | `bash scripts/ci_go.sh security` | `services/auth/internal/token`（`TestRefreshReuseDetectionRevokesFamily`） |
| MVP-SEC-004 Join Ticket 単回/期限/不一致 | `bash scripts/ci_go.sh security` | `services/auth/internal/integration`＋`ticket` |
| MVP-SEC-005 Rate Limit | `bash scripts/ci_go.sh security` | `services/common/ratelimit`＋`TestLoginRateLimit*`（失敗のみ計数） |
| MVP-SEC-006 価格を Client 入力から採らない | `bash scripts/ci_go.sh security` | `economy/security_test.go`（proto に価格フィールドが無いことを固定） |
| MVP-SEC-007 Secret を Repo に置かない | `make security` | `scripts/security_scan.sh`＋`.env.rc.example`（sslmode=require・共有シークレット必須） |
| MVP-SEC-009 監査ログ | 実行時 | 購入/売却/Ticket/ログインに `audit=true` 付き構造化ログ |
| AT-018 Server 再起動 | `make recovery` | `scripts/recovery_test.sh` シナリオ1 |
| AT-019 購入直後 crash | `make recovery` | シナリオ2（`idempotency_key` で再現・二重付与なし） |
| AT-020 負荷 | `make load` / `make load-at020` | `services/tools/loadgen`＋`scripts/load_assert.py`（Gate は PASS/RECORD） |
| 第18.1 Soak | `make soak-short` / `make soak-full` | `scripts/soak.sh`＋`soak_assert.py`（RSS 傾きでリーク疑いを名指し） |
| 第13章 監視 | `make smoke` | 全サービスの `/healthz`・`/readyz`・`/metrics`＋JSON 構造化ログ |
| 第15章 アセット検査 | `make assets` | `assets-pipeline`（negative scale 実測 / non-manifold / Kit 別 budget / `UCX_` 命名） |

- `make ci` = `proto lint test assets security`（サーバー不要）。
- `make ci-hardening` = `security recovery load`（重い `soak-full` は含めない）。
- 夜間は `.github/workflows/nightly.yml`（load-at020 / soak-short / recovery）。
- **Tick Gate（P95≤40ms）は実 DS が要る**。`DS=1` を付けないと loadgen の合成
  tick になり、レポートは `tick_source: "synthetic"` として PASS にしない。

## 注意点

- **Go は 1.22 固定**: `go mod tidy` は放っておくと `go` ディレクティブと依存を
  新しい版へ上げてしまう（pgx や x/text が go≥1.24/1.25 を要求する）。Dockerfile
  （`golang:1.22-bookworm`）と CI（setup-go 1.22）が 1.22 なので、上がると
  ビルドが壊れる。`GOTOOLCHAIN=local` で作業し、`go.mod` の `go 1.22` と
  pgx `v5.7.2` / x/text `v0.21.0` を維持すること。
- **go.sum は初回生成**: `services/{auth,api}` は `go.mod` のみコミット済み。
  `make test` / `make lint`（内部で `go mod tidy`）を初回に一度実行すると
  `go.sum` が生成される。生成後はコミットして固定する。Docker ビルドも
  内部で `go mod tidy` を行うため単独で成立する。
- **proto 生成物のコミット漏れ**が最頻の CI 失敗要因。`make proto` 後の差分は必ずコミット。
- **`.sh` は LF 固定**（`.gitattributes`）。CRLF だと WSL2 で `bad interpreter`。
- **Blender 未導入時**は `make assets` が自動 skip する。導入するか
  `make assets BLENDER=blender.exe`（Windows の Blender）で実行する。
- ビルドキャッシュ（`GOCACHE`, uv cache）は WSL2 ホーム（`~/.cache`）へ置き、
  `/mnt/c`(`/mnt/d`) の I/O 遅延を避ける（03B 5.5）。
