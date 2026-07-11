---
title: "M5 実装指示書（WSL2 / Linux 側）"
subtitle: "WorldState Projection / Decision Worker / 構造化出力(実LLM) / World Event Director / WorldEventService gRPC / 3 Event"
document_id: "IMPL-M5-WSL-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M5 / WSL2側）"
baseline: "Go / Python(FastAPI) / PostgreSQL / NATS JetStream / buf / Anthropic API (claude-opus-4-8)"
related_document: "08A_M5実装指示書_Windows側_v0.1.md, 07A_M4実装指示書_Windows側_v0.1.md, 07B_M4実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M5 実装指示書：WSL2 / Linux 側 v0.1

本書は M5（WorldState/LLM）の作業を **WSL2（Ubuntu）側**（Python/Go サービス、proto/DB/NATS、Anthropic API 呼び出し）に限定して指示する。Unity Dedicated Server 側（decision の適用・イベント現地効果）は別冊 **08A（Windows側）** を参照。第0章「分担と連携」は両冊で共通・要点のみ再掲し、詳細は M0（03A/03B）第0章を正とする。

M5 の中心は **WSL2 側**である。llm-worker のモックを **実 LLM（Anthropic API・構造化出力）** に置き換え、worldstate の Projection と World Event Director、WorldEventService gRPC を実装する。

---

## 0. 分担と連携（要点・詳細は M0 第0章）

### 0.1 環境別 責務分担（M5 該当分）

| 領域 | 担当環境 | 主なタスク |
|---|---|---|
| worldstate Projection（NATS購読→投影再構築） | **WSL2** | `actor_state_projections` 更新、`projection_version` 採番 |
| llm-worker Decision Worker（実LLM構造化出力） | **WSL2** | `ai.decision.request` 購読→ActionDecision 生成→`ai.decision.result.{server_id}` |
| World Event Director（評価→提案） | **WSL2** | `worldevent.evaluation.request` 購読→EventProposal→`worldevent.proposal.{server_id}` |
| WorldEventService gRPC（Register/UpdateState） | **WSL2** | 承認提案の `world_event_instances` 登録・状態遷移 |
| proto 生成（buf） | **WSL2**（生成） | Go/Python/C# を生成し出力（0.4） |
| DB マイグレーション | **WSL2** | `world_event_instances` / `action_templates` / `ai_decisions` / `actor_state_projections` |
| DS での decision 適用・イベント現地効果 | **Windows** | 08A が担当。WSL2 は `unity/` を触らない |

### 0.2 競合回避（厳守）

- **WSL2（本書）は `services/`, `proto/`, `infra/`, `assets-pipeline/`, `scripts/*.sh`, `Makefile` のみ**を編集する。
- `unity/` は触らない（**例外は `unity/SurvivalWorld/Assets/Generated/`＝buf 生成物のみ**。0.4）。
- Unity で書く/ビルドする全て（decision 適用・イベント現地効果・`*.ps1`）は 08A（Windows側）が担当。

### 0.3 境界成果物（M5 で増える分）

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| `ai.proto` / `worldevent.proto` → C# 生成 | WSL2（`make proto`） | Windows（Unity DS） | `unity/SurvivalWorld/Assets/Generated/` |
| `ai.proto` / `worldevent.proto` → Go/Python 生成 | WSL2 | WSL2（api/worldstate/llm-worker） | `services/gen/{go,python}` |
| DB migrations（M5 追加テーブル） | WSL2 | 参照: 両方 | `services/*/migrations/` |

> proto は M0 で `ai.proto`（DecisionRequest/ActionStep/ActionDecision/EvaluationRequest/EventProposal）・`worldevent.proto`（WorldEventService Register/UpdateState, WorldEventState）が定義済み。M5 は **これらを実装で満たす**のが原則で、proto の破壊的変更は避ける（拡張が必要な場合は追加フィールドのみ・`buf breaking` で検査）。

---

## 1. 対象と前提（WSL2側）+ M5 DoD

- 本書の完了で、llm-worker が **実 LLM（`claude-opus-4-8`）の構造化出力**で型安全な ActionDecision / EventProposal を生成し、worldstate が NATS イベントから `actor_state_projections` を再構築し、WorldEventService gRPC と 3 種の World Event Template が動作する状態にする。
- **`LLM_MOCK=1` の経路は残す**（テストとオフライン開発用のゴールデン）。実 LLM は `LLM_MOCK=0`（既定 or 未設定時は API キーの有無で判定）。

### 1.1 WSL2側 M5 DoD

- worldstate が `world.{id}.event.*`（actor/resource/economy）を JetStream 購読し、`actor_state_projections`（`projection_version` 単調増加）を再構築する。イベントを正として**再構築可能**であること。
- llm-worker が `ai.decision.request` を購読し、実 LLM の構造化出力で **JSON Schema 検証・Allowed ID 検証を通過した** ActionDecision を `ai.decision.result.{server_id}` へ発行する（付録B.1 準拠）。
- World Event Director が `worldevent.evaluation.request` を評価し、**3 テンプレのいずれか**の EventProposal（付録B.2 準拠）を `worldevent.proposal.{server_id}` へ発行する。
- WorldEventService gRPC の `Register` / `UpdateState`（14.2）が実装され、`world_event_instances` を PROPOSED→ACTIVE→COMPLETED の順に条件付き更新できる。
- Event Proposal 承認（10.4）検査が実装され、Reject 時に `reason_code`、承認時に `worldevent.result` を発行する。
- LLM 実呼び出しに **リトライ・タイムアウト・トークン/コスト記録**があり、重い処理を FastAPI request handler に置かない（R7）。
- migrations が適用され、`make ci` / `make smoke` が緑。モック LLM のゴールデンテストが緑。

---

## 2. 前提成果物（M0〜M4）

| Milestone | 参照する成果物（M5 で前提とする） |
|---|---|
| M0 基盤 | services/worldstate（FastAPI health + NATS土台）, services/llm-worker（NATS consumer + `build_mock_decision`）, proto（`ai.proto`/`worldevent.proto`）, NATS JetStream, api（Go）, migrations 基盤 |
| M1 接続 | Auth/Matchmaking/Join Ticket, FishNet 接続 |
| M2 Inventory/Save | 共通 Inventory, World Load/Save, `domain_events`（API が唯一のWriter・13.1） |
| M3 Survival | 採掘/製作/狩猟/料理/Hunger/Waste/清掃 |
| M4 AI | PersonalState, Template Runner, Utility Fallback, 20 AI, **fast-layer と projection 土台** |

- 既存: `services/llm-worker/worker/main.py`（`build_mock_decision`・`ai.decision.request` 購読・health）, `services/worldstate/app/main.py`（health + NATS lifespan）。M5 はこれらを**拡張**する。
- 既存 proto（正）: `proto/survival/v1/ai.proto`, `proto/survival/v1/worldevent.proto`（本書 3 章で参照する message はここが唯一の正）。

---

## 3. 実装対象（サービス/ドメインごと）

### 3.1 WorldState Projection（worldstate・MVP 10.1 / 8.1 / 13.1）

**購読 Subject**（14.3）:

| Subject | Payload概要 |
|---|---|
| `world.{world_id}.event.actor` | Actor needs、Inventory、行動結果 |
| `world.{world_id}.event.resource` | 採掘、枯渇、再生成 |
| `world.{world_id}.event.economy` | 購入、売却、Buyer出現/消滅 |

- JetStream の **durable pull/push consumer** で `world.*.event.*` を購読する（ワイルドカード）。Consumer 名は `worldstate-projection`。再配信前提で **冪等**に処理する。
- 各イベントは基本設計 8.1 の必須フィールド（`event_id`, `world_id`, `aggregate_type`, `aggregate_id`, `event_type`, `sequence`, `occurred_at`, `schema_version`, `payload`）を持つ前提。
- **`actor_state_projections`**（13章・WorldState Consumer が唯一のWriter）を更新する:
  - `actor_id` PK, `world_id`, `projection_version BIGINT NOT NULL`, `payload JSONB`, `rebuilt_at TIMESTAMPTZ`。
  - `payload` は 10.1 の `actor_state`（PersonalState、Inventory Summary、現在行動、最近イベント）を投影する。
  - **`projection_version` は actor 単位で単調増加**。イベント適用ごとに `projection_version = projection_version + 1`。
- **冪等**: `inbox_dedup(consumer_id, message_id)` で `event_id` による重複排除（既処理なら副作用なしで ACK）。`consumer_id='worldstate-projection'`。
- **domain_events へ書き込まない**（13.1・基本設計 4.3）。WorldState は投影専用テーブルのみ更新する。
- **再構築可能**: `world.{id}.event.*` を先頭から replay して `actor_state_projections` を再構成できる CLI/関数を用意（`projection_version` はリプレイ順で再採番）。
- **R7**: 重い投影ロジックを FastAPI request handler 内で完結させない。Projection は **lifespan で起動する常駐 consumer タスク**（`asyncio.create_task`）で処理し、health/readyz ハンドラは軽量のまま保つ。

`app/main.py` の構成（既存を拡張）:

```text
worldstate/app/
├─ main.py            # FastAPI(health/readyz) + lifespan で consumer/director を起動
├─ projection.py      # world.*.event.* 購読 → actor_state_projections 再構築（冪等・projection_version）
├─ director.py        # worldevent.evaluation.request 購読 → 評価 → EventProposal（3.4）
├─ db.py              # asyncpg プール（repository 層で抽象化・移植性: 基本設計 9.1）
└─ rebuild.py         # replay による projection 再構築（テスト/運用）
```

### 3.2 Decision Worker（llm-worker・MVP 10.2 / 9.4 / 付録B.1）

**フロー**（10.2）:

1. DS が発行した **DecisionRequest** を `ai.decision.request` で受信（`ai.proto`: `actor_id`, `world_id`, `state_versions`, `reason`。加えて宛先解決用に `server_id` をペイロードへ含める＝既存モックの `request.get("server_id")` を踏襲）。
2. WorldState API/DB から必要 Projection（`actor_state_projections`）を取得し、`action_templates` から**候補テンプレをルールで絞る**（9.3 のタグ/前提条件・自由形式のゲーム命令を LLM に生成させない）。
3. LLM Worker が候補・短い状態要約・制約を入力し、**構造化結果**を生成（3.3）。
4. **JSON Schema 検証・Allowed ID 検証・Token/Cost/Timeout 記録**（3.7）。
5. 結果を `ai.decision.result.{server_id}` へ発行（DS が最終検証・08A 3.1）。

**発行 message**（`ai.proto`・付録B.1）: `ActionDecision`
- `decision_id`（ULID）, `actor_id`, `state_version`, `template_id`, `steps[]{action_template_id, params}`, `created_at_unix_ms`。
- 付録B.1 の拡張フィールド（`world_version`, `personal_state_version`, `template_version`, `parameters`, `lease_until`）は proto に無いものは **JSON ペイロード側で保持**するか proto 追加フィールドで表現（破壊的変更は避ける）。DS の 9.4 検証（Version/Precondition/Target/鮮度/重複）に必要な値を必ず含める。
- **`ai_decisions`**（13章・WorldState/LLM Worker が Writer）へ判断履歴を保存: `decision_id` PK, `actor_id`, `state_version`, `template_id`, `status`, `payload JSONB`, `created_at`。`status ∈ {generated, published, rejected, mock}`。

**Subject**（14.3）: `ai.decision.request`（購読） / `ai.decision.result.{server_id}`（発行）。

`llm-worker/worker/` の構成（既存を拡張）:

```text
llm-worker/worker/
├─ main.py            # NATS 購読ループ + health（既存を拡張）
├─ decision.py        # build_mock_decision（既存・保持）+ build_decision（実LLM）
├─ event_director.py  # World Event 評価（3.4）※worldstate に置く場合は本書 3.1 の director.py に集約
├─ llm.py             # Anthropic API クライアント（3.7・tool use / 構造化出力・retry/timeout/cost）
├─ schemas.py         # ActionDecision / EventProposal の Pydantic モデル + JSON Schema
└─ candidates.py      # Projection 取得 + 候補テンプレのルール絞り込み（9.3）
```

> Director を worldstate に置くか llm-worker に置くかは、**LLM 呼び出しを llm-worker に集約**する方針で決める（LLM クライアントの重複を避ける）。本書では **評価要求の受信・ルール絞り込みは worldstate（director.py）**、**LLM 呼び出し本体は llm-worker（llm.py）**とし、worldstate → NATS `worldevent.llm.request`（内部）で llm-worker に委譲してもよい。単純化を優先し、**Decision も Event も llm-worker で LLM を呼ぶ**構成を既定とする。

### 3.3 構造化出力スキーマ（型安全な ActionDecision / EventProposal）

**方針**: Anthropic API の **tool use（`strict: true`）** または **`output_config.format`（`json_schema`）** で型安全に生成する。Python は `client.messages.parse()` + Pydantic を推奨（`schemas.py` の Pydantic モデルから JSON Schema を導出）。

**ActionDecision の JSON Schema（`schemas.py`・proto + 付録B.1 に整合）**:

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "template_id":            { "type": "string" },
    "template_version":       { "type": "integer" },
    "steps": {
      "type": "array",
      "items": {
        "type": "object",
        "additionalProperties": false,
        "properties": {
          "action_template_id": { "type": "string" },
          "params":             { "type": "object", "additionalProperties": { "type": "string" } }
        },
        "required": ["action_template_id", "params"]
      }
    },
    "parameters":       { "type": "object", "additionalProperties": { "type": "string" } },
    "reason":           { "type": "string" }
  },
  "required": ["template_id", "steps"]
}
```

- **`decision_id`（ULID）, `actor_id`, `state_version`, `created_at_unix_ms`, `world_version`, `personal_state_version`, `lease_until` は Worker 側で確定**（LLM に生成させない。ID/バージョンの偽装を防ぐ）。LLM は `template_id` / `steps` / `parameters` のみ選択する。
- **Allowed ID 検証**（10.2 / 17章 MVP-SEC-008）:
  - `template_id ∈ action_templates`（status=active の候補集合に限定）。
  - 各 `steps[].action_template_id ∈` PrimitiveActionRegistry の許可 ID（`MoveTo`/`Interact`/`UseItem`/`Craft`/`Purchase` 等・9.1）。
  - 逸脱時は **その Decision を破棄し `ai_decisions.status='rejected'`**、DS 側は現行行動継続→Utility Fallback（08A 3.4）。

**EventProposal の JSON Schema（`schemas.py`・proto `EventProposal` + 付録B.2 に整合）**:

```json
{
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "event_template_id":  { "type": "string", "enum": ["world_event.great_hunt", "world_event.rare_resource", "world_event.rare_buyer_rush"] },
    "region_id":          { "type": "string" },
    "region_tags":        { "type": "array", "items": { "type": "string" } },
    "reason_tags":        { "type": "array", "items": { "type": "string" } },
    "requested_intensity":{ "type": "number", "minimum": 0, "maximum": 1 },
    "start_after_sec":    { "type": "integer" },
    "start_before_sec":   { "type": "integer" }
  },
  "required": ["event_template_id", "requested_intensity"]
}
```

- proto `EventProposal`（`proposal_id`, `template_id`, `world_id`, `region_id`, `params bytes(JSON)`, `score`）へマップする際、`proposal_id`（ULID）・`world_id` は Worker 確定、`params` は付録B.2 の JSON（`region_tags`/`reason_tags`/`requested_intensity`/`start_after_sec`/`start_before_sec`）を格納、`score` は `requested_intensity` またはルール評価値を格納。
- **具体スポーン数・供給予算・座標は LLM に決めさせない**（基本設計 8.2）。LLM はテンプレ/地域タグ/理由タグ/強度/開始 Window のみ提案。実値はルールエンジンと DS が決定。

### 3.4 World Event Director（worldstate/llm-worker・MVP 8.2 / 10.3）

- **購読 Subject**: `worldevent.evaluation.request`（`ai.proto`: `EvaluationRequest{world_id, reason}`）。periodic か reason 駆動。
- Projection（`world_summary`/`region_state`/`event_history`・10.1）と `action_templates`/`world_event_instances` を参照し、**候補テンプレをルールで絞る**→ LLM 構造化出力（3.3）→ **EventProposal** を `worldevent.proposal.{server_id}` へ発行。
- **World Event Templates 3 種（10.3・実値はルール側の暫定制約）**:

| template_id | MVP効果 | 暫定制約（ルール/DS 側で強制） |
|---|---|---|
| `world_event.great_hunt` | Rare Deer を段階 Spawn | duration **15分**、alive cap **+40**、total cap **100** |
| `world_event.rare_resource` | Rare Ore Node を追加 | duration **15分**、node cap **20**、total yield budget 設定 |
| `world_event.rare_buyer_rush` | Rare Buyer を **3体** Spawn | duration **10分**、各在庫独立・**Rare 保証なし** |

### 3.5 WorldEventService gRPC（api・MVP 14.2 / `worldevent.proto`）

- `worldevent.proto` の `WorldEventService` を **api（Go）に実装**（`world_event_instances` の Writer は API・13章/付録C）。
- `Register(RegisterRequest) → RegisterResponse`:
  - 入力: `proposal_id`, `template_id`, `world_id`, `region_id`, `params(bytes/JSON)`。
  - 承認された提案を `world_event_instances` に **state=WORLD_EVENT_STATE_PROPOSED**（enum は `worldevent.proto`）で登録し、`event_instance_id`（UUID）を返す。`proposal_id` で冪等（同一提案の再登録は既存 id を返す）。
- `UpdateState(UpdateStateRequest) → UpdateStateResponse`:
  - 入力: `event_instance_id`, `expected_state`, `new_state`, `stats(bytes/JSON)`。
  - **`expected_state` 一致を条件に**（楽観条件更新）`state` を遷移し `stats` を保存。不一致は `ResultStatus` を失敗で返す（冪等・二重遷移防止）。
- **`WorldEventState` enum**（`worldevent.proto`）: `UNSPECIFIED=0, PROPOSED=1, ACTIVE=2, COMPLETED=3, REJECTED=4`。基本設計 8.2 の詳細状態（Scheduled/Preparing/Active/Completing）は DS 側の進行（08A 3.2）で扱い、API へは主要状態（PROPOSED/ACTIVE/COMPLETED/REJECTED）を確定する。

`services/api` への追加（Go・既存サービスに gRPC サーバを追加）:

```text
api/internal/worldevent/  # WorldEventService 実装（Register/UpdateState + repository）
```

### 3.6 Event Proposal 承認（worldevent.result・MVP 10.4）

- **承認検査**（10.4）: 同一 Region 競合・同種 Cooldown・Server Tick 負荷・Spawn Budget・供給予算・Template Version を検査。
  - 検査は **DS 側でも行う**（負荷/競合/地域可否/Version は DS が最終判断・基本設計 8.2、08A 3.3）。WSL2 側は **DB で判定可能な条件**（同一 Region の ACTIVE 重複、同種 Cooldown、Template Version 妥当性）を `world_event_instances` から判定する。
- **Reject 時**: `reason_code` を WorldState へ返し、**LLM に自由な代替を再生成させず次回評価まで待つ**（10.4）。`worldevent.result` に Rejected + `reason_code` を発行。
- **Approved 後**: `WorldEventService.Register` で `event_instance` を PROPOSED 登録してから DS の Preparing へ進む（08A 3.3）。`worldevent.result` に Approved + `event_instance_id` を発行。
- **終了時**: DS が `UpdateState(new_state=COMPLETED, stats={spawned, harvested, purchased, remaining, participant_count})` を呼び、WSL2 側は `worldevent.result` に Completed + 集計を発行。

**Subject**（14.3）: `worldevent.result`（Approved/Rejected/Completed）。

### 3.7 LLM 実呼び出し（Anthropic API・claude-opus-4-8）

- **SDK**: Python `anthropic` パッケージ（`pyproject.toml` に `anthropic>=0.40` を追加）。クライアントは `anthropic.Anthropic()`（`ANTHROPIC_API_KEY` を環境から解決）または `AsyncAnthropic()`（Worker は asyncio なので Async 推奨）。
- **モデル既定**: `LLM_MODEL`（既定 **`claude-opus-4-8`**）。実装は最新 Claude 前提。モデル ID 文字列は完全形をそのまま使う（日付サフィックスを付けない）。
- **構造化出力**: `messages.parse()` + Pydantic（`schemas.py`）を推奨。生スキーマなら `output_config={"format": {"type": "json_schema", "schema": <SCHEMA>}}`。または tool use `strict: true` + `additionalProperties: false`。**型安全な ActionDecision / EventProposal を保証**する（3.3）。
- **thinking / effort（レイテンシ最適化）**: 意思決定は Tick を待たせない低レイテンシが要件（10.2 の非同期性）。既定は `output_config={"effort": "low"}`（短い意思決定向け）。難所は `medium` を検証。thinking は既定オフ（省略）で開始し、品質不足なら `thinking={"type":"adaptive"}` を検討。
- **max_tokens**: 意思決定/提案は小さい構造化出力のため `max_tokens=1024` 程度（非ストリーミングで可）。
- **リトライ**: SDK は 429/5xx/接続エラーを指数バックオフで自動リトライ（`max_retries` 既定 2、必要に応じ引き上げ）。**Decision Request 自体の再試行は指数 Backoff**（16章 LLM timeout 行）で、`ai.decision.request` の再受信/再発行を DS 側と協調（重複は `decision_id` 冪等）。
- **タイムアウト**: クライアント `timeout`（秒）を設定（既定 10 分は長すぎるため意思決定用に短く＝例 8 秒）。タイムアウト時は **結果を発行せず**、DS は現行行動継続→Utility Fallback（16章・08A 3.4）。
- **コスト/トークン記録**: `response.usage`（`input_tokens`/`output_tokens`/`cache_read_input_tokens` 等）を構造化ログに出力し、`ai_decisions.payload` にトークン数を記録。コスト目安は `claude-opus-4-8`（入力 $5 / 出力 $25 per 1M tokens）。
- **refusal 対応**: `response.stop_reason == "refusal"` を先に判定してから `content` を読む（安全分類による拒否時は Decision を破棄→Fallback）。
- **R7（重い処理を request handler に置かない）**: LLM 呼び出しは **NATS consumer コールバックから起動する専用タスク**で行い、FastAPI の request handler 内で LLM を完結させない。worldstate の HTTP ハンドラは health/readyz のみ。
- **LLM_MOCK**: `LLM_MOCK=1` で `build_mock_decision`（既存）等の決定的モックに切替（テスト/オフライン）。`ANTHROPIC_API_KEY` 未設定時も安全側でモックにフォールバックしログ警告。
- **入力から個人認証情報を除外**（17章 MVP-SEC-008）: プロンプトに個人情報を入れない。出力は Allowed Schema/ID で検証（3.3）。

`llm.py` の要点（擬似）:

```python
# Async Anthropic クライアント。timeout はコンストラクタ or with_options で短縮。
client = anthropic.AsyncAnthropic(timeout=8.0, max_retries=3)

async def decide(candidates, state_summary) -> ActionDecisionModel:
    resp = await client.messages.parse(
        model=os.getenv("LLM_MODEL", "claude-opus-4-8"),
        max_tokens=1024,
        output_config={"effort": "low"},
        output_format=ActionDecisionModel,   # Pydantic
        messages=[{"role": "user", "content": build_prompt(candidates, state_summary)}],
    )
    if resp.stop_reason == "refusal":
        raise DecisionRefused(resp.stop_details)
    log_usage(resp.usage)               # トークン/コスト記録
    return resp.parsed_output           # 検証済み Pydantic インスタンス
```

### 3.8 migrations（worldstate 所有・MVP 13章）

`services/worldstate/migrations/NNNN_m5.up.sql`（golang-migrate 形式・`.down.sql` も用意）。通貨列があれば BIGINT、一意制約を初期から:

```sql
-- WorldState Consumer が唯一の Writer（13.1）。projection_version は actor 単位で単調増加。
CREATE TABLE IF NOT EXISTS actor_state_projections (
    actor_id           UUID PRIMARY KEY,
    world_id           UUID NOT NULL,
    projection_version BIGINT NOT NULL DEFAULT 0,
    payload            JSONB NOT NULL DEFAULT '{}'::jsonb,
    rebuilt_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS actor_state_projections_world_idx ON actor_state_projections (world_id);

-- Definition Data / Version 管理（WorldState）。template_id+version が PK。
CREATE TABLE IF NOT EXISTS action_templates (
    template_id  TEXT NOT NULL,
    version      BIGINT NOT NULL,
    status       TEXT NOT NULL DEFAULT 'active',
    tags         JSONB NOT NULL DEFAULT '[]'::jsonb,
    definition   JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (template_id, version)
);

-- 判断履歴（WorldState / LLM Worker）。decision_id 一意（13.1）。
CREATE TABLE IF NOT EXISTS ai_decisions (
    decision_id   TEXT PRIMARY KEY,
    actor_id      UUID NOT NULL,
    state_version BIGINT NOT NULL,
    template_id   TEXT NOT NULL,
    status        TEXT NOT NULL,
    payload       JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ai_decisions_actor_idx ON ai_decisions (actor_id);
```

`services/api/migrations/NNNN_m5.up.sql`（API 所有・`world_event_instances`）:

```sql
-- API が登録・状態確定（13章/付録C）。stats は終了集計を保持。
CREATE TABLE IF NOT EXISTS world_event_instances (
    event_instance_id UUID PRIMARY KEY,
    proposal_id       TEXT UNIQUE,                 -- Register の冪等キー
    template_id       TEXT NOT NULL,
    world_id          UUID NOT NULL,
    region_id         TEXT,
    state             INTEGER NOT NULL DEFAULT 1,  -- WorldEventState enum（1=PROPOSED）
    params            JSONB NOT NULL DEFAULT '{}'::jsonb,
    stats             JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS world_event_instances_world_state_idx ON world_event_instances (world_id, state);
```

- `action_templates` には 9.3 の MVP Action Templates（`survival.eat_owned_food` 等）と 10.3 の World Event Templates を **シード**しておく（候補絞り込み・Allowed ID 検証の元）。シードは `migrations` か `scripts/seed.sh` で投入。

### 3.9 テスト（モックLLMでゴールデン・MVP 18章）

- **Unit（llm-worker）**: `build_mock_decision`（既存・保持）に加え、実 LLM 経路を **モッククライアントで差し替え**た `build_decision` のゴールデンテスト（決定的入力→期待 ActionDecision）。JSON Schema 検証・Allowed ID 検証（許可外 `template_id`/`action_template_id` を破棄）のテスト。
- **Unit（worldstate）**: Projection の冪等性（同一 `event_id` 二重投入で `projection_version` が 1 回分のみ増加）・`rebuild.py` の replay 再構築一致テスト。
- **Unit（Director）**: EventProposal の 3 テンプレ enum 制約・`requested_intensity ∈ [0,1]` 検証。
- **Integration（api）**: `WorldEventService.Register`（`proposal_id` 冪等）・`UpdateState`（`expected_state` 不一致で失敗）を PostgreSQL 実 DB で検査（AT-015/016/017 の Spawn/供給予算超過なしは主に DS 側だが、状態遷移の整合はここで担保）。
- `make ci`（proto/lint/test/assets）・`make smoke`（health）が緑。**実 LLM を CI で呼ばない**（`LLM_MOCK=1` を CI 既定に）。

---

## 4. 実装順序（WSL2側）

| # | タスク | 完了確認 |
|---|---|---|
| L-1 | `make proto`（`ai.proto`/`worldevent.proto` の Go/Python/C# 再生成・drift 検査） | 生成物が各所へ出力・`git diff --exit-code` 通過 |
| L-2 | migrations 追加（3.8: actor_state_projections/action_templates/ai_decisions/world_event_instances）+ `make migrate` | テーブル作成・シード投入 |
| L-3 | worldstate Projection（3.1: `world.*.event.*` 購読・`projection_version`・冪等・`inbox_dedup`） | 同一 event 二重投入で version 1 回分のみ増加 |
| L-4 | `rebuild.py`（replay 再構築） | replay 結果が逐次適用と一致 |
| L-5 | llm-worker `llm.py`（3.7: Anthropic クライアント・retry/timeout/cost・refusal） | `LLM_MOCK=1` で決定的・実 LLM は手動疎通のみ |
| L-6 | `schemas.py` + `build_decision`（3.2/3.3: 構造化出力・Allowed ID 検証）→ `ai.decision.result.{server_id}` | ゴールデンテスト緑・許可外 ID 破棄 |
| L-7 | World Event Director（3.4: `worldevent.evaluation.request`→EventProposal→`worldevent.proposal.{server_id}`） | 3 テンプレの提案が生成される |
| L-8 | WorldEventService gRPC（3.5: api に Register/UpdateState） | Register 冪等・UpdateState 条件更新 |
| L-9 | Event Proposal 承認（3.6: 検査・`reason_code`・`worldevent.result`） | Reject/Approved/Completed 発行 |
| L-10 | `make ci` / `make smoke` | 全緑・health 200 |

---

## 5. テスト・受入（WSL2側）

- **AT-013（AI 自律動作・関連）**: 実 LLM（or モック）Decision で `template_id`/`steps` が生成され、`ai.decision.result.{server_id}` へ発行される。Allowed ID 検証を通過。
- **AT-014（LLM 停止）**: LLM タイムアウト時に **結果を発行しない**（DS 側で Fallback・08A）。WSL2 側は タイムアウト→未発行・`ai_decisions.status` 記録を確認。
- **AT-015/016/017（3 Event）**: `WorldEventService.Register`→`UpdateState(ACTIVE)`→`UpdateState(COMPLETED, stats)` の状態遷移が整合。Spawn Cap/供給予算の**強制は DS 側**（08A 3.2）だが、暫定制約値（great_hunt: alive+40/total100/15分、rare_resource: node20/yield budget/15分、rare_buyer_rush: 3体/独立在庫/10分）を `params`/検査に反映。
- **Projection 再構築**: `world.{id}.event.*` を全 replay して `actor_state_projections` が再構成できる（8.1・13.1）。
- 自動テスト区分（18.1）: Unit（Need score/Event budget 等）・Integration（Go API + PostgreSQL）を CI で実行。

---

## 6. 落とし穴（WSL2側・LLM コスト/レイテンシ/リトライ）

- **request handler で LLM を完結させない（R7）**: FastAPI ハンドラ内で `await client.messages...` を呼ぶと Tick/HTTP を止める。**NATS consumer タスク**で処理し、health は軽量に保つ。
- **レイテンシ暴走**: `effort` 未設定は既定 `high` で遅い。意思決定は `effort:"low"`（+ 短い `timeout`）で開始。thinking を安易にオンにしない（意思決定は低レイテンシ優先）。
- **コスト暴走**: `max_tokens` を大きく取りすぎない（意思決定は 1024 程度）。`response.usage` を必ず記録し、トークン/回数を監視。同一 Projection への繰り返し呼び出しは prompt caching（`cache_control`）でプレフィックスを共有し入力コストを削減できる。
- **リトライの二重適用**: SDK は 429/5xx を自動リトライ（`max_retries`）する。その上で `ai.decision.request` を自前で再発行すると多重実行になる。**`decision_id` による冪等**と、再試行は 16章の指数 Backoff に一本化。
- **モデル ID の日付サフィックス付与禁止**: `claude-opus-4-8` を完全形のまま使う。存在しない ID は 404。
- **構造化出力の未検証利用**: `stop_reason=="refusal"` や `max_tokens` 打ち切りで出力が不完全になり得る。**必ず JSON Schema/Pydantic 検証・Allowed ID 検証を通す**（未検証の DS 適用は 17章 MVP-SEC-008 違反）。LLM に `decision_id`/バージョン/座標/スポーン数を生成させない。
- **projection_version 競合**: 複数 consumer で同一 actor を並行更新すると version が飛ぶ。actor 単位で直列化（同一 subject partition or 行ロック）し、`inbox_dedup` で冪等を担保。
- **domain_events への誤書き込み**: WorldState は投影専用テーブルのみ更新（13.1）。`domain_events` の Writer は API のみ。
- **NATS JetStream の durable 未設定**: ephemeral consumer だと再起動で取りこぼす。durable + 明示 ACK（冪等前提）で構成。
- **proto 生成物のコミット漏れ**: `ai.proto`/`worldevent.proto` 変更後の C#/Go/Python 生成物を必ずコミット（`git diff --exit-code`・M0 と同様）。
- **`ANTHROPIC_API_KEY` の秘匿**: `.env`（gitignore）で管理し、リポジトリへ保存しない（17章 MVP-SEC-007）。未設定時はモックへ安全フォールバックしログ警告。

---

## 参考資料

[R7] [FastAPI Background Tasks caveat](https://fastapi.tiangolo.com/tutorial/background-tasks/)
[R8] [NATS JetStream](https://docs.nats.io/nats-concepts/jetstream)
[R9] [NATS JetStream Consumers](https://docs.nats.io/nats-concepts/jetstream/consumers)
[R10] [PostgreSQL JSON types](https://www.postgresql.org/docs/current/datatype-json.html)
[R-ANTH] [Anthropic API — Messages / 構造化出力 / tool use](https://platform.claude.com/docs/en/build-with-claude/structured-outputs.md)
[R-MODEL] [Claude models overview（claude-opus-4-8）](https://platform.claude.com/docs/en/about-claude/models/overview.md)
[R-BUF] [Buf docs](https://buf.build/docs)
[R-MIGRATE] [golang-migrate](https://github.com/golang-migrate/migrate)
