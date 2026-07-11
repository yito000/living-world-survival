---
title: "M4 実装指示書（WSL2 / Linux 側）"
subtitle: "投影・テンプレ配信・配管 — actor_state_projections / action_templates / ai_decisions / NATS"
document_id: "IMPL-M4-WSL-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M4 / WSL2側）"
baseline: "Python(FastAPI) / Go / PostgreSQL / NATS JetStream / buf / golang-migrate"
related_document: "07A_M4実装指示書_Windows側_v0.1.md, 06A_M3実装指示書_Windows側_v0.1.md, 06B_M3実装指示書_WSL2側_v0.1.md, 08A_M5実装指示書_Windows側_v0.1.md, 08B_M5実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M4 実装指示書：WSL2 / Linux 側 v0.1

本書は M4（AI）の作業を **WSL2（Ubuntu）側**（worldstate/llm-worker/API の投影・テンプレ配信・NATS/DB 配管・migrations・テスト）に限定して指示する。DS 上の fast-layer AI ランタイム（PersonalState/Template Runner/Utility Fallback/20体）は別冊 **07A（Windows側）** を参照。第0章「分担と連携」は両冊で共通・同一内容（M0 03A/03B 第0章の M4 版）。

M4 の主眼は **投影（actor_state_projections）・テンプレ供給（action_templates）・判断履歴（ai_decisions）・NATS 配管**である。**LLM 実呼び出し本体は M5**。M4 では llm-worker は `ai.decision.result.{server_id}` の**モック応答/未接続**でよく、request→（処理）→result の経路が骨格として通ることを DoD とする。

---

## 0. 分担と連携（共通・両冊同一）

### 0.1 環境別 責務分担（M4 範囲）

| 領域 | 担当環境 | 主なタスク |
|---|---|---|
| AI Actor 構成（9.1） | **Windows**（Unity DS C#） | Controller / PersonalState / Runner / Registry / DecisionClient / Fallback |
| PersonalState 計算（9.2 urgency式） | **Windows** | Needs 更新・urgency 算出 |
| fast-layer Template Runner（9.3） | **Windows** | Step 実行・Retry/Timeout/Interrupt |
| Decision 適用規則（9.4） | **Windows** | 検証・冪等・Lease |
| Utility Fallback（7.4） | **Windows** | urgency ベース切替 |
| 20体 AI スポーン・同時実行 | **Windows** | 時間分割更新 |
| `ai.decision.request` 発行 / `ai.decision.result.{server_id}` 購読 | **Windows**（DS） | 骨格の疎通 |
| `actor_runtime_states` の DS 生成 → API 永続化（`ActorState.Save`） | **Windows**（生成）→ **WSL2**（API が永続化） | gRPC 受け口は API |
| `actor_state_projections` 投影（projection_version） | **WSL2**（worldstate Consumer） | NATS 購読で投影再構築 |
| `action_templates`（template_id+version, status, tags, definition JSONB）管理・配信 | **WSL2**（worldstate） | テンプレ供給。DS/LLM が Reader |
| `ai_decisions` 記録 | **WSL2**（worldstate/llm-worker） | 判断履歴 |
| `ai.decision.request/result` の受け口・NATS 配管 | **WSL2** | worldstate 購読 / llm-worker 応答（本体 LLM は M5） |
| migrations（action_templates/ai_decisions/actor_state_projections 拡張）・テスト | **WSL2** | DB・CI |

> データ所有権（MVP 付録C / 基本 9.4）: `actor_state_projections` の Writer=**WorldState Consumer**、`action_templates` の Writer=**WorldState**、`ai_decisions` の Writer=**WorldState / LLM Worker**、`actor_runtime_states` の永続 Writer=**API**（DS 生成）。worldstate は `domain_events` へ**直接書かず**投影専用テーブルのみ更新（13.1 v0.2.1 注記）。

### 0.2 リポジトリ配置・Git/LFS・境界成果物

- 配置・改行/LFS 規約は M0 03B 第0.2〜0.3 に同じ。本冊が触るのは **`services/,proto/,infra/,migrations,scripts/*.sh,Makefile`** のみ。**`unity/` は触らない**（`Assets/Generated/` の proto C# 出力＝buf 生成のみ WSL2 が生成する）。
- 境界成果物は 07A 0.3 と同一表。`ai.proto` は本冊が `buf generate` し、C# を `unity/SurvivalWorld/Assets/Generated/` へ、Go/Python を各サービスへ出力（M0 03B 5.4）。

### 0.3 連携フロー（M4 代表例）

- **テンプレ配信**: worldstate が `action_templates`（status=active, tags, definition JSONB）を管理し、DS 起動時に取得できる API/経路を提供。DS はキャッシュして fast-layer / Utility Fallback で参照（07A 0.4）。
- **判断要求の配管**: DS が `ai.decision.request` を発行 → worldstate が購読し、必要 Projection を DB から取得、候補 Template をルールで絞る（10.2 手順2）。**M4 は LLM 本体を呼ばず**、llm-worker がモック `ActionDecision` を `ai.decision.result.{server_id}` へ返す/未接続でよい。
- **投影**: DS 由来の Actor イベント（`world.{id}.event.actor` 等）を worldstate が購読し `actor_state_projections`（projection_version）を再構築。
- **判断履歴**: 発行・結果を `ai_decisions`（decision_id, actor_id, state_version, template_id, status, payload）に記録。

---

## 1. 対象と前提（WSL2側）+ M4 DoD

- 環境: WSL2（Ubuntu）+ Docker Desktop、リポジトリ `/mnt/c/dev/living-world-survival`（M0 03B）。
- 本書の完了で、worldstate が Actor イベントを購読して `actor_state_projections` を投影し、`action_templates` を管理・配信し、`ai_decisions` を記録し、`ai.decision.request/result` の NATS 配管が骨格として通り、`ActorState.Save`（API）で `actor_runtime_states` を永続化できる状態にする。

### 1.1 WSL2側 M4 DoD

- migrations で `action_templates` / `ai_decisions` / `actor_state_projections` が MVP 13章スキーマに沿って作成/拡張され、`make migrate` が適用される。
- `action_templates` に MVP 9.3 の 13テンプレを **seed 投入**（status/tags/definition JSONB）、worldstate から取得できる（DS が起動時取得できる API/経路）。
- worldstate が `world.{id}.event.actor` 等を購読し `actor_state_projections`（projection_version 単調増加、payload JSONB）を投影・再構築できる。
- worldstate が `ai.decision.request` を購読し、候補 Template をタグ/前提でルール絞り込みする土台がある（LLM へ渡す入力整形。本体呼び出しは M5）。
- llm-worker が `ai.decision.result.{server_id}` に**モック `ActionDecision`**（proto）を返せる（または未接続で DS の fallback を妨げない）。`ai_decisions` に記録される。
- API に `ActorState.Save`（14.2）の受け口があり `actor_runtime_states` を永続化する（DS 生成、API 書き込み）。
- `make ci`（proto/lint/test）緑、`make smoke` で worldstate/llm-worker/api が health を返す。

---

## 2. 前提成果物（M0–M3、これらに依存）

- **M0**: `infra/docker-compose.yml`（postgres/nats、JetStream `-js`）、`make up/migrate/ci/smoke`、`buf`（`buf.gen.yaml` の C#/Go/Python 出力）、worldstate（FastAPI, NATS 購読土台）、llm-worker（NATS consumer, モック LLM）、`services/{auth,api}` 雛形、`inbox_dedup`/`outbox_messages`。
- **M1**: API の gRPC（WorldData 系）と DS 連携、game_servers（server_id）。
- **M2**: `inventories`/`inventory_entries`/`item_instances`、`characters`/`worlds`/`world_snapshots`、API の Save 系。`ActorState.Save` の inventory_summary はここの要約。
- **M3**: 採掘/製作/狩猟/料理/Hunger/Waste/清掃の Domain Event 種別（`world.{id}.event.actor`/`.resource`/`.economy`）。M4 の投影・テンプレ前提条件はこれらのイベントを入力にする。
- **proto**: `proto/survival/v1/ai.proto`（`DecisionRequest`/`ActionStep`/`ActionDecision`）。本冊が生成物を各サービスと Unity へ出力。

---

## 3. 実装対象（WSL2 / バックエンド）

### 3.1 migrations（`action_templates` / `ai_decisions` / `actor_state_projections` 拡張）

MVP 13章のスキーマを正とする。`services/worldstate/migrations/`（または集約 migrations 方針に従い）に golang-migrate 形式 `NNNN_name.up.sql` / `.down.sql` を追加。

```sql
-- action_templates: template_id+version PK, status, tags, definition JSONB（Owner: WorldState）
CREATE TABLE IF NOT EXISTS action_templates (
  template_id   TEXT        NOT NULL,
  version       INTEGER     NOT NULL,
  status        TEXT        NOT NULL DEFAULT 'active',  -- active / draft / retired
  tags          TEXT[]      NOT NULL DEFAULT '{}',
  definition    JSONB       NOT NULL,                   -- preconditions/interrupts/steps/min-max_duration
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (template_id, version)
);
CREATE INDEX IF NOT EXISTS idx_action_templates_status ON action_templates(status);
CREATE INDEX IF NOT EXISTS idx_action_templates_tags   ON action_templates USING GIN (tags);

-- ai_decisions: decision_id PK, actor_id, state_version, template_id, status, payload（Owner: WorldState/LLM Worker）
CREATE TABLE IF NOT EXISTS ai_decisions (
  decision_id   TEXT        PRIMARY KEY,                -- ULID
  actor_id      TEXT        NOT NULL,
  state_version BIGINT      NOT NULL,                   -- personal_state_version（proto state_version）
  template_id   TEXT        NOT NULL,
  template_version INTEGER,                             -- B.1 の template_version（DS 突合用）
  status        TEXT        NOT NULL,                   -- requested / produced / applied / rejected / superseded
  payload       JSONB       NOT NULL,                   -- ActionDecision（steps/params/lease 等）
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_ai_decisions_actor ON ai_decisions(actor_id, created_at DESC);

-- actor_state_projections: actor_id PK, world_id, projection_version, payload JSONB（Owner: WorldState Consumer）
CREATE TABLE IF NOT EXISTS actor_state_projections (
  actor_id           TEXT        NOT NULL,
  world_id           TEXT        NOT NULL,
  projection_version BIGINT      NOT NULL DEFAULT 0,    -- 投影の単調増加バージョン
  payload            JSONB       NOT NULL,              -- PersonalState/Inventory Summary/現在行動/最近イベント
  rebuilt_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (actor_id)
);

-- actor_runtime_states: actor_id PK, world_id, version, payload JSONB（Owner: DS生成 → API永続化）
CREATE TABLE IF NOT EXISTS actor_runtime_states (
  actor_id   TEXT        PRIMARY KEY,
  world_id   TEXT        NOT NULL,
  version    BIGINT      NOT NULL,
  payload    JSONB       NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

- 13.1: `decision_id` は一意（PK）。JSONB 検索は必要 Path のみ GIN/Expression Index（`tags` は GIN）。通貨列があれば BIGINT（M4 では net_worth 等は projection payload 内、float 禁止列は作らない）。
- `actor_runtime_states` は M0 で最小があれば拡張、なければ本 migration で作成（Owner は DS生成→API 永続化）。API サービス側 migrations に置くか worldstate 側かはリポジトリの既定に合わせる（Writer は API）。

### 3.2 action_templates の管理・配信（worldstate、MVP 9.3 / 基本 7.3）

- MVP 9.3 の **13テンプレ**を seed（`services/worldstate` の初期データ or migration の INSERT）。definition JSONB は基本 7.3 のスキーマ（`tags`/`preconditions`/`interrupts`/`steps`/`min_duration_sec`/`max_duration_sec`）。tags は 9.3 の「主要Tag/条件」を反映。例:

```json
{
  "template_id": "cleaning.clean_nearby",
  "version": 1,
  "tags": ["cleanliness_high", "waste_nearby", "cleanup"],
  "preconditions": ["cleanliness.pressure > threshold", "waste.nearby_count > 0"],
  "interrupts": ["health_critical", "target_missing"],
  "steps": ["FindWaste", "MoveTo", "CleanWaste"],
  "min_duration_sec": 15,
  "max_duration_sec": 300
}
```

- **配信 API/経路**: DS 起動時に status=active のテンプレ一式を取得できるようにする。FastAPI の内部エンドポイント（例 `GET /internal/action_templates?status=active`）または起動 bootstrap（M1 の WorldData.LoadBootstrap に相乗り）で供給。**Utility Fallback 用にも同じテンプレ集合を供給**（07A 3.6 のタグ→テンプレ解決に使う）。
- Version 管理: 同 template_id は version で世代管理、status で active/retired を切替（DS はキャッシュ更新時に version 突合）。

### 3.3 actor_state_projections の投影（worldstate Consumer、MVP 10.1 / 13）

- worldstate が NATS の Actor イベント（`world.{id}.event.actor`：Actor needs/Inventory/行動結果、14.3）を購読し、`actor_state_projections` を更新。`inbox_dedup` で冪等（event_id 重複排除）。
- `projection_version` を単調増加させ、payload に MVP 10.1 の actor_state 相当（PersonalState、Inventory Summary、現在行動、最近イベント）を格納。
- **イベントを正として投影を再構築できる**こと（8.1）。`domain_events` へは直接書かない（投影専用、13.1）。
- 重い処理を FastAPI request handler 内で完結させない（M0 R7）。購読 Consumer は独立ループ/タスクで処理。

### 3.4 ai.decision.request/result の NATS 配管（worldstate 購読 / llm-worker 応答）

subject（14.3）:

| subject | 向き | M4 での扱い |
|---|---|---|
| `ai.decision.request` | DS→worldstate（購読） | 受信し必要 Projection 取得＋候補 Template をルール絞り込み（10.2 手順2）。**LLM 本体は M5** |
| `ai.decision.result.{server_id}` | llm-worker→DS（応答） | **モック `ActionDecision`** を返す or 未接続（DS は fallback で成立） |

- **worldstate（request 購読）**: `DecisionRequest`（`actor_id, world_id, state_versions, reason`）を受け、`actor_state_projections` から状態要約を作り、`action_templates`（tags/preconditions）で候補を絞る（Allowed Template のみ）。M4 はここまで（LLM 入力整形の土台）。整形結果と候補を M5 で LLM Worker へ渡す。
- **llm-worker（result 応答）**: M4 は**モック LLM**（M0 のモック consumer を流用）。候補から 1件を単純ルール（例: reason に対応するタグ最上位）で選び、proto `ActionDecision`（`decision_id, actor_id, state_version, template_id, steps[], created_at_unix_ms`）を組んで `ai.decision.result.{server_id}` へ publish。**未接続でも DS 側 DoD は成立**するため、モックはテスト疎通用と位置づける。
- **記録**: request 受信と result 生成の双方を `ai_decisions`（status: requested/produced）に記録。DS 側の applied/rejected は M5 で結果イベントを受けて更新（M4 は produced までで可）。
- **冪等/配管**: JetStream の consumer、`inbox_dedup`。`server_id` は request の world/server コンテキストから解決（result subject に埋める）。

### 3.5 ActorState.Save の受け口（API、MVP 14.2 / 付録C）

- API に gRPC `ActorState.Save(actor_id, version, personal_state, inventory_summary)`（14.2）を実装し、`actor_runtime_states`（`actor_id, world_id, version, payload JSONB, updated_at`）へ **upsert**。
- **API が唯一の永続 Writer**（DS はフィールド権威だが直接 DB 書き込みしない、付録C）。version が古い上書きを拒否（単調増加 or 条件付き更新）。
- inventory_summary は M2 の Inventory から DS が要約したもの（API は受領・永続のみ）。

### 3.6 proto（`ai.proto`）の生成・整合

- M4 では `ai.proto` は既存（`DecisionRequest`/`ActionStep`/`ActionDecision`）を利用。**M4 での proto 変更は原則しない**（B.1 の world_version/template_version/lease_until は当面 payload/別カラムで扱う。07A 3.4 マップ表と一致させる）。
- 変更が必要なら本冊が `buf lint`→`buf generate`→drift 検査（`git diff --exit-code`、C# 出力 `unity/SurvivalWorld/Assets/Generated` を含む）→`buf breaking`（M0 03B 5.4）。**生成物のコミット漏れ**が最頻 CI 失敗。

---

## 4. 実装順序（WSL2側）

| # | タスク | 完了確認 |
|---|---|---|
| L-1 | migrations 追加（action_templates/ai_decisions/actor_state_projections/actor_runtime_states）+ `make migrate` | テーブル作成、GIN/Index |
| L-2 | action_templates に 9.3 の 13テンプレ seed（tags/definition JSONB） | SELECT で確認、status=active |
| L-3 | worldstate: テンプレ配信 API/経路（status=active 取得） | DS 起動時取得できる |
| L-4 | worldstate: `world.{id}.event.actor` 購読 → actor_state_projections 投影（projection_version, inbox_dedup） | 投影が単調増加で再構築 |
| L-5 | worldstate: `ai.decision.request` 購読 + 候補 Template ルール絞り込み（Projection 取得） | request で候補が絞られる |
| L-6 | llm-worker: モック `ActionDecision` を `ai.decision.result.{server_id}` へ publish（M0 モック流用） | result 疎通、ai_decisions 記録 |
| L-7 | API: `ActorState.Save` gRPC → actor_runtime_states upsert（version 条件） | 永続化される、古い version 拒否 |
| L-8 | proto 整合（変更あれば buf generate + drift）、pytest/go test | `make ci` 緑 |
| L-9 | `make smoke`（worldstate/llm-worker/api health）+ 07A と結合疎通 | health 200、request→result 経路 |

---

## 5. テスト・受入（WSL2側）

MVP 18.1 の区分に沿う。

- **Unit**（18.1 Need score / Event budget 等の隣接）:
  - action_templates definition のスキーマ検証（tags/preconditions/interrupts/steps 必須、version 整合）。
  - ai_decisions 記録の冪等（同一 decision_id で重複記録しない）。
  - actor_state_projections の projection_version 単調増加、同一 event_id 二重適用なし（inbox_dedup）。
  - 候補 Template 絞り込みルール：urgency タグ/preconditions で Allowed のみ残す（候補外を除外）。
- **Integration**（Go API + PostgreSQL / worldstate + NATS）:
  - `ActorState.Save` → actor_runtime_states upsert、version 条件で古い上書き拒否。
  - worldstate が Actor イベント購読 → actor_state_projections 更新（NATS 実接続）。
  - `ai.decision.request` → llm-worker モック → `ai.decision.result.{server_id}` の往復、ai_decisions に requested/produced 記録。
- **受入**: `make smoke` で worldstate/llm-worker/api が health を返し、DS（07A）の `ai.decision.request` を worldstate が受信、llm-worker がモック result を返せる（または未接続でも DS の fallback を妨げない）。action_templates が DS へ配信され、20体が fallback で自律行動できる（結合は 07A 5章と合同）。

---

## 6. 落とし穴（WSL2側）

1. **worldstate が domain_events を直接書く**のは禁止。投影専用（actor_state_projections）のみ更新（13.1 v0.2.1、付録C）。Writer 境界を越えない。
2. **actor_runtime_states の Writer は API**。worldstate/DS が直接書かない。DS は `ActorState.Save` 経由（付録C）。
3. **LLM 本体を M4 で呼ばない**。llm-worker はモック/未接続。重い LLM 処理を request handler に置かない（M0 R7）。M4 の目的は配管の骨格。
4. **proto 生成物のコミット漏れ**（C# 出力先 `unity/SurvivalWorld/Assets/Generated` 含む）が最頻 CI 失敗（M0 03B 10章）。`git diff --exit-code`。
5. **B.1 と proto の差**：`ai_decisions` に `template_version` 列を持ち、`state_version` に personal_state_version を格納、`lease_until` は payload/DS 計算（07A 3.4 と一致させる）。proto を勝手に拡張しない（両冊合意）。
6. **JetStream 未有効/consumer 設計不備**：`-js`（M0）、`inbox_dedup` で冪等、subject の `{server_id}`/`{world_id}` ワイルドカードを取り違えない。
7. **projection_version の非単調**：投影再構築で version を巻き戻さない。イベント順序と冪等を担保。
8. **action_templates の status 未考慮**：DS へ配信するのは status=active のみ。retired/draft を混ぜない。version で世代管理。
9. **`/mnt/c` I/O とキャッシュ**：ビルド/テストキャッシュは WSL2 ホームへ（M0 03B 5.5）。
10. `.sh` は LF、migrations の up/down 対で用意（M0 03B）。

---

## 参考資料

- MVP 詳細設計 v0.2.2：9章（9.1〜9.4）、10章（10.1 Projection / 10.2 LLM Job）、13章（action_templates/ai_decisions/actor_state_projections/actor_runtime_states）、13.1（Index/Constraint）、14.2（ActorState.Save）、14.3（NATS `ai.decision.request` / `ai.decision.result.{server_id}`）、付録B.1（ActionDecision）、付録C（データ所有権）、18.1（テスト区分）、19（DoD）。
- 基本設計 v0.2.1：7章（7.1 二層ループ / 7.2 PersonalState / 7.3 テンプレ / 7.4 urgency式・フォールバック）、8.1（イベント＋スナップショット）、9.4（所有権）、図7-1。
- `proto/survival/v1/ai.proto`（DecisionRequest / ActionStep / ActionDecision）。
- [R7] FastAPI Background Tasks caveat / [R8] NATS JetStream / [R9] JetStream Consumers / [R10] PostgreSQL JSON types / [R11] Table partitioning / [R-BUF] Buf / [R-MIGRATE] golang-migrate（M0 03B 参考資料）。
