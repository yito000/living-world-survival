---
title: "M7 Hardening 実装指示書（WSL2 / Linux 側）"
subtitle: "負荷・Soak・再起動復旧・Security・Blender Import 検査・Release Candidate（バックエンド/CI）"
document_id: "IMPL-M7-WSL-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M7 / WSL2側）"
baseline: "Go / Python(FastAPI) / PostgreSQL / NATS JetStream / buf / Blender / Docker"
related_document: "10A_M7実装指示書_Windows側_v0.1.md, 09A_M6実装指示書_Windows側_v0.1.md, 09B_M6実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M7 Hardening 実装指示書：WSL2 / Linux 側 v0.1

本書は M7（Hardening）の作業を **WSL2（Ubuntu）側**（負荷/Soak ハーネス、再起動復旧テスト、Security 検証、監視、CI 拡張、Blender アセット最終検査、Release Candidate 用サービスイメージ確定）に限定して指示する。Unity/クライアント/DS ビルドは別冊 **10A（Windows側）** を参照。第0章「分担と連携」は両冊で要点共通。

M7 は新機能を追加しない。**M1〜M6 で実装済みの全機能**（Auth/Matchmaking、Inventory/Save、Survival、AI、WorldState/LLM、Economy）を、MVP第3章の容量・性能目標と第17章のセキュリティ要件、第18章の受入条件に対して **計測・検証・厳格化し、Release Candidate（RC）品質へ引き上げる**ことが目的である。

---

## 0. 分担と連携（要点・両冊共通）

### 0.1 M7 環境別 責務分担

| 観点 | 担当環境 | 主なタスク |
|---|---|---|
| 負荷試験ハーネス（バックエンド計測） | **WSL2** | tick_ms/レイテンシ収集、DB/NATS/Outbox メトリクス、負荷投入ドライバ |
| 負荷試験（描画/同期/クライアント） | **Windows** | 2 Client + 20 AI の PlayMode 負荷、fps/同期計測 |
| Soak（長時間連続稼働） | **両方** | WSL2=memory/outbox 滞留/event lag、Windows=Client/DS 常駐の tick/memory |
| 再起動復旧テスト | **WSL2**（サービス/DB/NATS） | snapshot staging→checksum→active、outbox flush、inbox_dedup 重複排除の整合 |
| 再起動後のクライアント復帰 | **Windows** | セッション/Join 再取得、Full Snapshot 再同期 |
| Security（サーバー/秘密/経路） | **WSL2** | JWT/JoinTicket 署名検証、Refresh rotation、sslmode、Rate Limit、audit、secrets |
| Security（Authenticator） | **Windows** | FishNet Authenticator の期限切れ/再利用/build 不一致/無効 Character 拒否 |
| 監視（health/metrics/log） | **WSL2** | liveness/readiness/dependency、Prometheus 形式 metrics、JSON 構造化ログ |
| CI 拡張（`make ci` / GitHub Actions） | **WSL2** | 負荷/セキュリティ/soak の一部を CI へ組込 |
| Blender アセット最終検査 | **WSL2** | 15章 CI 検査の厳格化（generate.py/validate.py 拡張） |
| Unity Import Processor / Batchmode Import | **Windows** | Manifest→Client/Server Prefab 生成、Batchmode Import 成功（受入） |
| Release Candidate ビルド | **両方** | WSL2=サービスイメージ確定、Windows=Client/Linux DS ビルド |

### 0.2 リポジトリ配置・境界・競合回避（M0 03B 0.2〜0.4 に準拠）

- 単一クローンを Windows 側に置き、WSL2 は `/mnt/c/dev/living-world-survival` で同一クローンを共有（03B 0.2）。
- 改行/LFS 規約（`.sh`=LF、`.ps1`=CRLF、バイナリ資産=LFS）は 03B 0.3 のまま厳守。
- **WSL2 側が触るのは `services/`, `proto/`, `infra/`, `assets-pipeline/`, `scripts/*.sh`, `Makefile`, `.github/` のみ**。`unity/` は触らない（`unity/SurvivalWorld/Assets/Generated/` の proto C# 出力を除く＝WSL2 生成）。
- Windows 側は `unity/` と `scripts/*.ps1` のみ。負荷/Soak の Unity 側は 10A が担当し、本書はバックエンド計測とハーネス駆動に限定する。

### 0.3 M7 境界成果物

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| Linux Dedicated Server ビルド（RC） | Windows（クロスビルド） | WSL2/Docker（負荷/Soak/復旧で実行） | `unity/SurvivalWorld/Build/Server/` |
| サービスイメージ（RC タグ確定） | WSL2 | 両方（実行/配布） | `infra/docker-compose.yml`, レジストリ |
| アセット Manifest（厳格検査済み） | WSL2（Blender） | Windows（Import Processor） | `build/assets/manifest.json` |
| 負荷/Soak/Security レポート | 各環境 | 両方（受入判定） | `build/reports/` |

---

## 1. 対象と前提（WSL2側）+ M7 DoD

- 環境: WSL2（Ubuntu 22.04）+ Docker Desktop（WSL2 バックエンド）。リポジトリは `/mnt/c/dev/living-world-survival`。
- 前提: M0 の DoD が全緑（`make ci`/`make smoke`）。M1〜M6 の全機能が実装済みで、`domain_events`/`outbox_messages`/`inbox_dedup`/`world_snapshots`（staging→checksum→active）が稼働している。
- 本書の完了で、負荷・Soak・再起動復旧・Security・Blender 最終検査が計測/検証可能になり、その一部が `make ci` に組込まれ、RC 用サービスイメージが確定する。

### 1.1 M7 DoD（MVP第19.1 の Definition of Done を M7 に反映）

- [ ] **要件トレーサビリティ**: M7 で実装/検証する項目が Requirement ID（BR-xxx / MVP-SEC-xxx）と受入試験（AT-018〜AT-021 等）に対応し、Repository 内（`build/reports/` と本書）で参照できる。
- [ ] **サーバー権威の再確認**: Client 入力から Damage/Loot/Drop/Craft Result/Purchase Price を採用しない経路であることを Security レビューで確認（MVP-SEC-005/006）。Client Cache に重要データが無いことも確認（第19.1）。
- [ ] **運用要素の完備**: DB Migration/Rollback 方針、Config Default、運用ログ（JSON 構造化）が揃う。
- [ ] **テスト緑**: Unit/Integration（Go API+PostgreSQL 購入・Outbox・Auth Refresh rotation・World bootstrap）が CI で成功。負荷/Security/Soak の一部が CI で自動実行される。
- [ ] **DS Headless 起動**: RC の Linux DS が Headless で起動し、Readiness/Graceful Shutdown/Snapshot を確認できる（10A が生成、本書が Docker 上で実行検証）。
- [ ] **容量目標の記録**: Profiler/Metric で第3章の目標に対する結果を数値で記録し、未達は既知 Issue として数値付きで残す（AT-020）。
- [ ] **復旧損失目標**: 購入0件・非経済状態5秒以内（第3章）を再起動復旧テストで満たす。

---

## 2. 前提成果物（M0〜M6 で構築済み）

| Milestone | 本書が依存する成果物 |
|---|---|
| M0 基盤 | Docker Compose（postgres/nats+services）、`Makefile`/`scripts/ci_*.sh`/`smoke.sh`、`.github/workflows/ci.yml`、`mise.toml`、assets-pipeline（generate/validate/asset_spec、CI 検査済み） |
| M1 接続 | Auth/Matchmaking、Join Ticket（署名・単回・server/build 固定）、gRPC RedeemJoinTicket（`used_at` 原子更新）、game_servers/join_tickets |
| M2 Inventory/Save | 共通 Inventory、Item Definition、World Bootstrap/Save、`world_snapshots`（staging→checksum→active）、Cache 削除復旧 |
| M3 Survival | 採掘/Development/製作/狩猟/料理/Hunger/Waste/清掃、`domain_events`＋DS Outbox＋API 確定 |
| M4 AI | PersonalState、Template Runner、Utility Fallback、20 AI、`actor_runtime_states`（DS 生成→API 永続化） |
| M5 WorldState/LLM | Projection（`actor_state_projections`）、Decision Worker、構造化出力、3 World Event、`inbox_dedup` |
| M6 Economy | Buyer、有限 Stock、購入 Transaction（idempotency_key UNIQUE、row lock/version）、AI 購入、ランキング Batch |

- `outbox_messages`（各 Service）と `inbox_dedup`（各 Consumer）による重複排除、`domain_events` の `event_id`（ULID）採番＝DS / 永続 `sequence` 確定＝API の分担（MVP第13.1）は M3〜M6 で成立済みとする。

---

## 3. 実装対象（WSL2側・観点別）

### 3.1 負荷試験ハーネス（MVP第3章 / AT-020）

**目的**: 第3章の暫定容量・性能目標に対し、バックエンド側の `tick_ms`・レイテンシ・DB/NATS/Outbox 負荷を計測し、ボトルネックを数値付きで記録する。

**計測対象の目標値（第3章）**:

| 指標 | 暫定 Gate | 収集元 |
|---|---|---|
| Server Tick | 20 Hz 目標、**Tick P95 ≤ 40ms / P99 ≤ 50ms** | DS Heartbeat `tick_ms`（gRPC）+ DS metrics |
| 通常操作応答（採取/使用/Pickup） | **P95 ≤ 200ms**（Server 処理） | API/DS ハンドラの計測ヒストグラム |
| 購入 | **P95 ≤ 500ms**（DB commit 含む） | API Purchase Transaction のスパン |
| AIアクター | 20 active（遠距離は低頻度） | DS metrics（AI 数・decision lag） |
| 動物 | 80 active（イベント時120上限） | DS metrics |
| World Item | 500 active | DS metrics |
| Snapshot | 30秒間隔 | PersistenceAgent metrics |
| LLM 判断 | 通常30秒以内、Hard timeout 60秒、進行を Block しない | llm-worker metrics（AT-014） |

**実装**:

- `scripts/load_test.sh`（新規）: `make up` で postgres/nats を起動、RC の Linux DS（`unity/.../Build/Server/`）を Docker/Headless で起動、負荷ドライバを投入。規模は環境変数で可変にする。
  - **ローカル開発スケール**: `PLAYERS=2 AI=20`（10A の PlayMode 負荷と同一構成。ローカル 1 台で再現可能）。
  - **AT-020 目標スケール**: `PLAYERS=16 AI=20 ANIMALS=80`（第3章基準。Gate 判定は「基準を満たすか、または測定結果とボトルネックを記録」）。
- 負荷ドライバ（Go、`services/tools/loadgen/`）: 合成 Client（FishNet 実接続 or Command 直送の軽量スタブ）を N 本起動し、採取/使用/Pickup/購入の Gameplay Command を送出。**Client 権威を持たせない**（入力のみ、MVP-SEC-005/006）。
- 収集: DS/API/worldstate の Prometheus 形式 metrics（3.7）を一定間隔でスクレイプし、`build/reports/load_<scale>_<ts>.json`（P50/P95/P99、tick_ms 分布、DB latency、Outbox depth、NATS pending）へ出力。
- 判定: `scripts/load_assert.py`（新規）が JSON を読み、Gate（Tick P95≤40ms/P99≤50ms、操作 P95≤200ms、購入 P95≤500ms）に対し PASS/RECORD を判定。未達は既知 Issue として数値付きで残す。

### 3.2 Soak（長時間連続稼働 / MVP第18.1 Soak）

**目的**: 4時間以上の連続稼働で memory リーク・Outbox 滞留・event lag を監視する（第18.1）。

**実装**:

- `scripts/soak.sh`（新規）: 全サービス＋DS を起動し、AI/Buyer/World Event を繰り返し発生させながら `SOAK_HOURS`（既定4）常駐。定期（例60秒）に以下を `build/reports/soak_<ts>.csv` へ追記。
  - **memory**: 各コンテナ RSS（`docker stats` / cgroup）。**単調増加＝リーク疑い**を線形回帰の傾きで判定。
  - **Outbox 滞留**: `SELECT count(*) FROM outbox_messages WHERE published_at IS NULL`。閾値超で警告（Relay が追随できていない）。
  - **event lag**: `domain_events` の最新 `occurred_at` と WorldState Consumer 処理位置（`inbox_dedup.processed_at` 最新 or projection `rebuilt_at`）の差。
  - **tick**: DS `tick_ms` P95 の時間推移（劣化＝リーク/断片化の兆候）。
- 判定: `scripts/soak_assert.py`（新規）が RSS 傾き閾値・Outbox depth 上限・event lag 上限で PASS/FAIL。**リーク疑い時はどのサービスかを CSV から特定**して既知 Issue 化。
- CI は full soak を回さない（4時間）。**短縮 Soak**（例 `SOAK_MINUTES=10`、`make soak-short`）を夜間ワークフローに載せる（3.6）。

### 3.3 再起動復旧テスト（MVP第12.1 / 16章 / AT-018・AT-019）

**目的**: サービス/DB/NATS の再起動をまたいで、**snapshot staging→checksum→active・Outbox flush・inbox_dedup による `event_id` 重複排除**の整合を検証し、復旧損失目標（購入0件・非経済状態5秒以内）を満たすことを示す。

**実装**（`scripts/recovery_test.sh` 新規、シナリオごとにサブコマンド）:

1. **DS crash → 別 DS で復元（AT-018）**: World/AI/Buyer を動かした状態で DS を kill。最新 `world_snapshots`（active pointer）＋以降の `domain_events` tail から別 DS が復元することを確認。復元後の World/AI/Buyer 数・状態が一致すること。
2. **Purchase 応答直後の crash（AT-019）**: `Economy.CommitPurchase` 成功応答直後に DS を kill。再起動後も `inventory_entries` の購入 Item と `currency_ledger` が保持され、**二重付与も欠落もない**（AT-021 と整合）。
3. **NATS 再起動**: NATS を停止して DS が Gameplay 変更を続行 → `outbox_messages` に蓄積（ゲームは止めない、第16章）。NATS 復旧後に順送 Flush され、WorldState Consumer が `inbox_dedup` で `event_id` を重複排除して**一度だけ**処理すること。
4. **DB 再起動**: postgres を再起動しても Outbox Relay が接続を回復し、未 publish の `outbox_messages` を欠落なく送出、`purchase_transactions.idempotency_key` により再送が二重確定しないこと。
5. **Corrupt Snapshot（第16章）**: `world_snapshots` の checksum を意図的に壊し、staging→checksum 検証で弾かれ、一つ前の Snapshot＋Event tail へ Fallback すること。

- 各シナリオで **復旧損失**を計測: 購入は 0 件失う（idempotency で再現）ことを assert、非経済状態（座標/Hunger 等）は 5 秒以内の Snapshot＋tail で復元されることを時刻差で記録。
- `scripts/recovery_assert.py`（新規）で PASS/FAIL を集計し `build/reports/recovery_<ts>.json` に出力。

### 3.4 Security（MVP第17章 / 基本設計第11章 / AT-006・AT-007 等）

各要件に対し **自動検証**（Integration テスト or スクリプト）を用意し、`build/reports/security_<ts>.json` に結果を残す。

| ID | 検証内容（WSL2側） | 実装 |
|---|---|---|
| MVP-SEC-001 | Client から API/WorldState への経路を公開しない | compose の port 公開範囲を確認する `scripts/security_scan.sh`（api/worldstate は内部ネットワークのみ、外部 publish しない）。auth/DS だけが外部境界。 |
| MVP-SEC-002 | パスワード Argon2id、ログ/Trace/Error に Password/Token を出さない | Argon2id パラメータ（OWASP 下限）を単体テストで固定。ログ出力に `password`/`token`/`refresh` 文字列が現れないログスキャナ（`scripts/log_secret_scan.sh`）。 |
| MVP-SEC-003 | Refresh Token ローテーション＋再利用検知 | Integration（Go）: refresh 後に旧 token を再利用 → family 失効（RFC 9700）。M0/03B 5.2 の test に追加。 |
| MVP-SEC-004 | Join Ticket 60秒前後・単回・server/build 固定・署名 | Auth 秘密鍵署名、DS 公開鍵検証。RedeemJoinTicket が `used_at IS NULL` 条件更新で単回消費。期限切れ/再利用/server・build 不一致を拒否する gRPC Integration テスト。 |
| MVP-SEC-005 | 全 Gameplay Command で ownership/Rate Limit/Sequence 検証 | 負荷ドライバから他 Client 所有 Character への Command を送り拒否されること（AT-001 の所有外操作不可の裏取り）。Rate Limit 超過が弾かれること。 |
| MVP-SEC-006 | Damage/Loot/Drop/Craft Result/Purchase Price を Client 入力から採用しない | Purchase Price を改竄した CommitPurchase が API 側で拒否/無視されること（価格は API 権威）。DamageService が Player↔AI を拒否（AT-006/007、DamageService 側 Integration）。 |
| MVP-SEC-007 | 内部 RPC は TLS＋サービス認証。Secret を Repo に保存しない | 内部 gRPC を TLS 化（mTLS 望ましい）。secrets は `.env`（gitignore）/環境変数/Docker secret 経由。Repo に平文鍵が無いことを `scripts/security_scan.sh` の grep で確認。 |
| MVP-SEC-008 | LLM 入力から個人認証情報を除外、出力を Allowed Schema/ID で検証 | llm-worker の入力サニタイズ（email/account_id を含めない）と出力の JSON Schema/Template ID allowlist 検証テスト。 |
| MVP-SEC-009 | 購入・通貨・所有権・Ticket 消費を監査ログへ | `currency_ledger`/`purchase_transactions`/`join_tickets.used_at` と相関 ID 付き監査ログの存在を Integration で確認。 |

- **sslmode**: 本番相当の `DATABASE_URL` は `sslmode=require`（M0 のローカルは `disable`）。RC 用 `.env` テンプレートで `sslmode` を分離し、`scripts/security_scan.sh` が RC プロファイルで `disable` を検出したら失敗させる。
- **Rate Limit**: Auth（ログイン失敗、第16章 Auth 失敗の Rate Limit）と Gameplay Command（MVP-SEC-005）の両方。閾値は Config Default にする。

### 3.5 監視（基本設計第13章）

- **Health**: 全サービスに liveness/readiness/**dependency health**（DB/NATS 接続）を分離実装。DS は Ready 後のみマッチ対象（Heartbeat `ready`）。M0 の `/healthz` を liveness、新規 `/readyz` を readiness とする。`scripts/smoke.sh` に `/readyz` を追加。
- **Metrics**（第13章の指標）: 接続数、Tick 時間、AI 数、NavMesh 失敗、イベント Lag、DB latency、Buyer sold-out、LLM latency/cost を Prometheus 形式 `/metrics` で公開。負荷/Soak ハーネス（3.1/3.2）はこれをスクレイプする。
- **Logging**: JSON 構造化。`world_id, server_id, account_id, actor_id, correlation_id` を付与（第13章）。Password/Token を出さない（MVP-SEC-002、3.4）。
- （任意）**Tracing**: Auth→Matchmaking→Join、DS→Purchase、WorldEvent→LLM→Decision の相関 ID 連携。MVP は相関 ID 付きログで代替可。

### 3.6 CI 拡張（`make ci` / `.github/workflows/`）

- **Makefile に target 追加**:
  - `load` … `scripts/load_test.sh`（ローカルスケール `PLAYERS=2 AI=20`）＋`load_assert.py`。
  - `soak-short` … `scripts/soak.sh SOAK_MINUTES=10`＋`soak_assert.py`。
  - `recovery` … `scripts/recovery_test.sh` 全シナリオ＋`recovery_assert.py`。
  - `security` … `scripts/security_scan.sh`＋`log_secret_scan.sh`＋Security Integration テスト。
  - `ci-hardening: security recovery load` … M7 追加分を一括（重い soak-full は除外）。
- **`make ci` への組込**: 既存 `ci: proto lint test assets` に **`security`（軽量・高速）を追加**し、`recovery` は DS バイナリ依存のため PR CI では条件付き（DS 成果物がある時のみ）。負荷/soak は下記ワークフローへ。
- **`.github/workflows/ci.yml` 拡張**: 既存 backend job に `security` step を追加（Blender 不要・postgres service で完結する範囲）。
- **`.github/workflows/nightly.yml`（新規）**: 夜間に `load`（AT-020 目標スケール）/`soak-short`（or full）/`recovery`/Network E2E（第18.1、10A の 2 Client+DS 起動と連携）を実行し、`build/reports/` をアーティファクト化。Unity/DS ビルドが要る job は Windows/self-hosted runner 前提のため、ローカルでは `make` target を直接叩けるようにしておく（03B 9章の方針を踏襲）。

### 3.7 Blender アセット最終検査（MVP第15章 / assets-pipeline 拡張）

**現状**（既存）: `validate.py` は missing socket（`REQUIRED_SOCKET_MIN`）/ collider 存在 / negative scale / triangle budget / LOD / glb 存在を検査。ただし `generate.py` は `negative_scale` を常に `False`、`non-manifold` 未検査、`TRIANGLE_BUDGET=5000`（緩め）。

**M7 で厳格化**（15章 CI 検査項目: missing socket / negative scale / non-manifold / triangle budget / collider 存在）:

1. **negative scale 実測**: `generate.py` の `_build_module` で各オブジェクトのワールド行列 determinant / スケール符号を実測し、manifest の `negative_scale` に反映（ハードコード撤廃）。ミラー等で負スケールが混入したら検出できるようにする。
2. **non-manifold 検査（必要範囲）**: `generate.py`（bpy 側）で本体/Collider メッシュの non-manifold エッジ数を `bmesh`（`edges` の `is_manifold`）で数え、manifest に `non_manifold_edges` を出力。`validate.py` は Collider について `non_manifold_edges == 0` を要求（描画メッシュは必要範囲＝Collider と閉じ形状のみ厳格、装飾は緩和可）。
3. **triangle budget の厳格化**: `asset_spec.TRIANGLE_BUDGET` を Kit/用途別に見直し、MVP モジュール実形状に合わせて現実的上限へ。Server 用軽量メッシュ（Collider 中心）は別バジェット。
4. **collider 存在＋命名規約**: `UCX_` 命名の Collider が各モジュールに存在（既存 has_collider を命名規約チェックまで拡張）。
5. **socket 必須化**: Kit ごとに必要 Socket（例 production=forge の投入口）を定義し、`REQUIRED_SOCKET_MIN` を Kit 別に指定できるよう `asset_spec.py` を拡張。
6. **受入（第15章）**: 生成コマンド1回（`make assets`）で全 Kit が決定的に再生成され、`validate.py` が上記を全て通過し、10A の **Unity Batchmode Import が成功**すること。`ci_assets.sh` は Blender 未導入環境では skip（既存挙動）だが、RC 判定時は Blender ありで必ず実行する。

### 3.8 Release Candidate 用サービスイメージ確定

- **タグ確定**: `infra/docker-compose.yml` の base image（postgres:16、nats:2）と各サービスイメージを**具体的な版へピン留め**（できれば digest 固定）。RC 版タグ（例 `:rc-m7`）を付与。
- **イメージ品質**: 各サービス Dockerfile を multi-stage・**非 root 実行**・最小ベース（distroless/slim）へ。`HEALTHCHECK` を定義（`/readyz`）。
- **Config Default / Migration**: RC 用 `.env.example`（`sslmode=require`、実 secret は環境注入）、DB Migration/Rollback（`.down.sql`）が揃っていることを確認（第19.1）。
- **DS イメージ**: 10A が生成する Linux DS バイナリを実行するコンテナ（or compose service）を用意し、Headless 起動・Readiness・Graceful Shutdown・Snapshot 作成を `make smoke`/`recovery` で確認。
- **RC 検証**: `make ci-hardening`＋負荷/soak-short/recovery が緑、`build/reports/` に容量目標の実測が揃った状態を **RC の受入**とする。

---

## 4. 実装順序（WSL2側）

| # | タスク | 完了確認 |
|---|---|---|
| L-1 | 監視の下地（`/readyz`・`/metrics`・JSON 構造化ログ）を全サービス/DS 実行系に整備（3.5） | smoke に `/readyz` 追加、`/metrics` が値を返す |
| L-2 | `services/tools/loadgen/`（負荷ドライバ）と `scripts/load_test.sh`/`load_assert.py`（3.1） | ローカルスケールで `build/reports/load_*.json` 生成 |
| L-3 | 負荷を AT-020 目標スケールで実行しボトルネック記録（3.1） | Tick/操作/購入の P95/P99 が記録される |
| L-4 | `scripts/recovery_test.sh`＋`recovery_assert.py`（5 シナリオ、3.3） | AT-018/019 と Corrupt Snapshot が PASS、購入0件損失 |
| L-5 | Security 検証群（Integration＋`security_scan.sh`/`log_secret_scan.sh`、3.4） | MVP-SEC-001〜009 が PASS、Repo に平文 secret 無し |
| L-6 | sslmode/Rate Limit の Config 分離と RC プロファイル（3.4） | RC で `sslmode=require`、Rate Limit 発火 |
| L-7 | `scripts/soak.sh`＋`soak_assert.py`（短縮 Soak を先に、3.2） | `soak-short` PASS、CSV でリーク傾き判定 |
| L-8 | assets-pipeline 厳格化（negative scale 実測/non-manifold/budget/命名、3.7） | `make assets` が厳格検査を通過 |
| L-9 | Makefile target（load/soak-short/recovery/security/ci-hardening）＋CI 組込（3.6） | `make ci-hardening` 緑、`ci.yml` に security step |
| L-10 | `.github/workflows/nightly.yml`（負荷/soak/recovery/E2E）（3.6） | 夜間ジョブ定義、reports をアーティファクト化 |
| L-11 | RC サービスイメージ確定（版ピン/非root/HEALTHCHECK、3.8） | `:rc-m7` タグ、`make smoke` 緑 |
| L-12 | full Soak（4時間以上）実行と結果記録（3.2 / 第18.1） | memory/tick/event lag に劣化なし、既知 Issue 化 |

---

## 5. テスト・受入（MVP第18章の具体化）

### 5.1 本書が主担当/裏取りする受入試験

| AT | 主担当 | WSL2側での具体化 |
|---|---|---|
| AT-018 Server 再起動 | WSL2 | `recovery_test.sh` シナリオ1：最新 Snapshot＋Event で World/AI/Buyer を復元し数値一致 |
| AT-019 Purchase 応答直後 crash | WSL2 | シナリオ2：再起動後も購入 Item と `currency_ledger` 保持、二重付与/欠落なし |
| AT-020 負荷試験 | 両方 | `load_test.sh`：16 Player/20 AI/80 Animals で Tick 基準を満たすか計測＋ボトルネック記録 |
| AT-021 購入二重 Write 防止 | WSL2 | Integration：付与1回、DS runtime version と API `new_persisted_inventory_version` 一致（第12.2.1） |
| AT-006/007 Player↔AI 非干渉 | WSL2（DamageService） | 強制 Command が DamageService で Damage 0/拒否 |
| AT-014 LLM 停止 | WSL2 | llm-worker を止め、60秒超でも DS Tick 継続・Fallback 移行を metrics で確認 |
| AT-003/004/012 冪等・在庫 | WSL2 | 既存 Integration を M7 の Rate Limit/negative パスで再確認 |

### 5.2 自動テスト区分（第18.1）での位置づけ

- **Unit/Integration**（Go+PostgreSQL 購入/Outbox/Auth Refresh rotation/World bootstrap）を M7 で漏れなく緑に。
- **Soak**（4時間以上、AI/Buyer/Event 反復、memory/tick/event lag 監視）を `soak.sh` で実現。
- **Network E2E**（2 Client + DS + Backend を CI/夜間起動）はバックエンド起動側を本書が用意し、Client 側は 10A と連携。

### 5.3 受入判定

- `build/reports/` に load/soak/recovery/security の JSON/CSV が揃い、Gate 未達は数値付き既知 Issue として残っていること（第19.1）。
- `make ci-hardening` が緑、`make assets`（Blender あり）が厳格検査を通過、RC イメージが `make smoke` で全 health/ready を返すこと。

---

## 6. 落とし穴（WSL2側）

- **負荷ドライバに Client 権威を持たせない**。Command 直送スタブでも Damage/価格/座標を確定させると MVP-SEC-005/006 を破る。入力のみ送る。
- **Tick 計測の出所を DS に統一**。ハーネス側の RTT を tick_ms と混同しない。DS が自報告する `tick_ms`（Heartbeat/metrics）を正とする。
- **Outbox 滞留 vs リーク の切り分け**。Soak で RSS 増加と Outbox depth 増加は原因が別。CSV で両方を並記し、どのサービスかを RSS で特定する。
- **`inbox_dedup` の重複排除は再起動後こそ本番**。NATS/DS 再起動で同一 `event_id` が再配送されるので、Consumer は `(consumer_id, message_id)` の一意で必ず一度だけ処理する（重複処理は経済不整合＝RISK-04）。
- **Corrupt Snapshot は active pointer を汚さない**。staging→checksum で弾き、active は必ず健全な直前を指す（第12.1/第16章）。checksum 検証を staging 段階で行うこと。
- **sslmode をローカルと RC で混同しない**。ローカルは `disable`、RC は `require`。security_scan が RC プロファイルの `disable` を失敗にする。
- **secrets を Repo/ログに残さない**（MVP-SEC-002/007）。`.env` は gitignore、ログスキャナで token/password 文字列を検出。
- **Blender 未導入で assets が自動 skip される**（既存挙動）。RC 判定時は Blender ありで必ず実行し、skip を成功と誤認しない。
- **non-manifold 検査は Collider を厳格・装飾を緩和**。全メッシュに閉多様体を要求すると装飾が落ちる。必要範囲（Collider/衝突形状）に絞る（第15章「必要範囲」）。
- **RC の image を latest 参照にしない**。digest/版ピンを外すと復旧/負荷の再現性が崩れる。
- **full Soak は CI に載せない**。4時間は PR を止める。短縮 Soak を CI、full は夜間/手動。
- **`.sh` は LF・`Makefile` はタブインデント**（03B 0.3）。M7 で追加する `scripts/*.sh` も LF 固定。

---

## 参考資料

[R6] [FishNet: Authenticator](https://fish-networking.gitbook.io/docs/manual/guides/authentication)
[R8] [NATS JetStream](https://docs.nats.io/nats-concepts/jetstream)
[R9] [NATS JetStream Consumers](https://docs.nats.io/nats-concepts/jetstream/consumers)
[R10] [PostgreSQL JSON types](https://www.postgresql.org/docs/current/datatype-json.html)
[R11] [PostgreSQL table partitioning](https://www.postgresql.org/docs/current/ddl-partitioning.html)
[R12] [OWASP Password Storage Cheat Sheet (Argon2id)](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
[R13] [RFC 9700: Best Current Practice for OAuth 2.0 Security](https://datatracker.ietf.org/doc/rfc9700/)
[R14] [Blender Python: --background --python](https://docs.blender.org/api/current/info_tips_and_tricks.html)
[R-PROM] [Prometheus exposition format](https://prometheus.io/docs/instrumenting/exposition_formats/)
[R-DISTROLESS] [GoogleContainerTools/distroless](https://github.com/GoogleContainerTools/distroless)
