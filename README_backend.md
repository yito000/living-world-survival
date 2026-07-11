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

## 注意点

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
