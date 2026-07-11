---
title: "M4 実装指示書（Windows ネイティブ側）"
subtitle: "Dedicated Server fast-layer AI — PersonalState / Template Runner / Utility Fallback / 20体同時"
document_id: "IMPL-M4-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M4 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / R3 / VContainer / UniTask / NATS .NET client"
related_document: "07B_M4実装指示書_WSL2側_v0.1.md, 06A_M3実装指示書_Windows側_v0.1.md, 06B_M3実装指示書_WSL2側_v0.1.md, 08A_M5実装指示書_Windows側_v0.1.md, 08B_M5実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M4 実装指示書：Windows ネイティブ側 v0.1

本書は M4（AI）の作業を **Windows ネイティブ側**（Unity Editor と Unity Dedicated Server 上で動く AI ランタイム＝fast-layer）に限定して指示する。投影・テンプレ配信・NATS/DB 配管などバックエンドは別冊 **07B（WSL2側）** を参照。第0章「分担と連携」は両冊で共通・同一内容（M0 03A/03B 第0章の M4 版）。

M4 の主眼は **二層ループの fast-layer**（毎tick 実行）と **Utility(urgency) フォールバック**である。**LLM 実呼び出しは M5**。M4 では LLM 結果（`ai.decision.result.{server_id}`）が来なくても AI が自律行動できることを最優先とし、Decision Request/Result の NATS 経路は**骨格（skeleton）**として通す。

---

## 0. 分担と連携（共通・両冊同一）

### 0.1 環境別 責務分担（M4 範囲）

| 領域 | 担当環境 | 主なタスク |
|---|---|---|
| AI Actor 構成（9.1） | **Windows**（Unity DS C#） | AIActorController / AIPersonalState / ActionTemplateRunner / PrimitiveActionRegistry / AIDecisionClient / UtilityFallback |
| PersonalState 計算（9.2 urgency式） | **Windows** | Needs 固定周期更新、urgency/need_score/pressure 算出 |
| fast-layer Template Runner（9.3） | **Windows** | タグ付き Action Template の Step 実行、Retry/Timeout/Interrupt/Compensation |
| Decision 適用規則（9.4） | **Windows** | 検証・冪等・Lease・Cancel Policy |
| Utility Fallback（7.4） | **Windows** | LLM 不在時の urgency ベース切替 |
| 20体 AI スポーン・同時実行 | **Windows** | 時間分割更新（6.1-4）、AIActorSystem |
| `ai.decision.request` 発行 / `ai.decision.result.{server_id}` 購読 | **Windows**（DS の C# NATS client） | 骨格の疎通（本体 LLM は M5） |
| `actor_runtime_states` の DS 生成 → API 永続化（`ActorState.Save`） | **Windows**（生成側） | gRPC 呼び出し。永続書き込みは API |
| `actor_state_projections` 投影（projection_version） | **WSL2**（worldstate Consumer） | NATS 購読で投影再構築 |
| `action_templates`（template_id+version, status, tags, definition JSONB）管理・配信 | **WSL2** | テンプレ供給。DS/LLM が Reader |
| `ai_decisions` 記録 | **WSL2**（worldstate/llm-worker） | 判断履歴 |
| `ai.decision.request/result` の受け口・migrations・テスト | **WSL2** | NATS 配管（LLM 本体は M5） |

> データ所有権（MVP 付録C / 基本 9.4）: `actor_runtime_states` は **フィールド権威=DS、永続 Writer=API**。`actor_state_projections` の Writer は **WorldState Consumer**。`action_templates` / `ai_decisions` の Writer は **WorldState**。DS はこれらを**直接書かず**、NATS/gRPC を経由する。

### 0.2 リポジトリ配置・Git/LFS・境界成果物

- 配置・改行/LFS 規約は M0 03A 第0.2〜0.3 に同じ（単一クローンを Windows FS、WSL2 は `/mnt/c/...`、`.sh`=LF / `.ps1`=CRLF）。
- 本冊が触るのは **`unity/` と `scripts/*.ps1` のみ**。`services/,proto/,infra/,migrations,Makefile,scripts/*.sh` は 07B（WSL2）が担当。`unity/SurvivalWorld/Assets/Generated/`（proto C# 生成物）は **WSL2 が生成、Windows が消費**（0.4）。

### 0.3 M4 の境界成果物

| 成果物 | 生成側 | 消費側 | 置き場所 / 経路 |
|---|---|---|---|
| proto → C# 生成（`DecisionRequest`/`ActionDecision`/`ActionStep`） | WSL2（`buf generate`） | Windows（DS） | `unity/SurvivalWorld/Assets/Generated/` |
| `action_templates` の definition JSONB | WSL2（worldstate 配信） | Windows（DS が取得しキャッシュ） | gRPC/HTTP もしくは起動時 bootstrap（07B 3章） |
| `ai.decision.request`（発行） | Windows（DS） | WSL2（worldstate 購読） | NATS subject `ai.decision.request` |
| `ai.decision.result.{server_id}`（購読） | WSL2（llm-worker、M4はモック/未接続でも可） | Windows（DS 適用） | NATS subject `ai.decision.result.{server_id}` |
| `actor_runtime_states`（生成→永続） | Windows（DS 生成） | WSL2（API が永続化） | gRPC `ActorState.Save`（14.2） |

### 0.4 連携フロー（M4 代表例）

- **テンプレ取得**: DS 起動時に worldstate から `action_templates`（status=active, tags, definition）を取得しメモリキャッシュ（07B）。fast-layer と Utility Fallback はこのキャッシュを参照する。
- **判断要求**: 再判断トリガ（状態変化/Template完了/Lease期限/失敗/重大欲求）で DS が `ai.decision.request` を発行。結果が来るまでは現行 Template を継続、完了後は Utility Fallback（LLM 待ちでも停止しない）。
- **結果適用**: `ai.decision.result.{server_id}` の `ActionDecision` を検証（存在/Version/Precondition/Target/鮮度/重複）→ Lease 適用 → Runner が Step 実行。M4 では result が来ないケースを既定運用とし、fallback で成立させる。
- **永続化**: PersonalState/Inventory Summary を `ActorState.Save`（API）で永続化。DS はメモリ正本、永続 Writer は API。

---

## 1. 対象と前提（Windows側）+ M4 DoD

- OS: Windows。Unity Editor ネイティブ。DS は Linux 向けクロスビルド（M0 03A 5章の `BuildScript.BuildLinuxServer`）で生成し WSL2/Docker で実行。
- 本書の完了で、**20体の AI Actor が Dedicated Server 上で同時に PersonalState 更新・Template 実行・Utility Fallback を行い**、`ai.decision.request` を発行し `ai.decision.result.{server_id}` を購読・適用でき（結果がなくても自律行動）、`actor_runtime_states` を生成して API に永続化できる状態にする。

### 1.1 Windows側 M4 DoD

- AIActorSystem が起動し、**20体**の AIActor をスポーンして毎tick 更新（時間分割、6.1 手順4）できる。目標 tick 予算内で 20体が滞りなく更新される（数値は Profiler で記録、未達は既知Issue）。
- **PersonalState 計算（9.2）**が固定周期で更新され、`urgency(food)/urgency(cleanup)/urgency(earn)`、`need_score`、`inventory_pressure`、`cleanliness_pressure`、`wealth_score` が正しく算出される（EditMode Unit）。
- **ActionTemplateRunner** が Step/Retry/Timeout/Interrupt/Compensation を実装し、`action_templates`（9.3）の definition を解釈して Primitive Action を継続実行できる（PlayMode）。
- **Decision 適用規則（9.4）**: 検証・冪等（同一 decision_id 再受信で副作用なし）・Lease・Template 切替時の Cancel Policy（予約・Station Lock 解放）が動作する。
- **Utility Fallback（7.4）**: LLM 不在/失敗時に最大 urgency のタグに対応するテンプレへ切替、同点は `food > cleanup > earn > sell`。**LLM を一切繋がなくても 20体が生存・清掃・稼得・安全待機で自律行動する**。
- `ai.decision.request` 発行と `ai.decision.result.{server_id}` 購読が疎通する（骨格。M4 は結果なしでも DoD 成立）。
- `actor_runtime_states` を DS が生成し `ActorState.Save`（gRPC）で API 永続化する経路が通る。
- EditMode/PlayMode テスト（`scripts\unity_test.ps1`）が緑。DS Linux ビルドが Headless 起動する。

---

## 2. 前提成果物（M0–M3、これらに依存）

- **M0**: Unity プロジェクト・基盤ライブラリ（R3/VContainer/UniTask/FishNet）・`BuildScript`・`unity_test.ps1`/`unity_build_server.ps1`・proto C# 生成の受け皿（`Assets/Generated/`）。NATS/Postgres の Docker、`make ci/smoke`。
- **M1**: Auth/Matchmaking/JoinTicket、FishNet 接続、Dedicated Server 起動・Readiness・Graceful Shutdown、WorldRuntime/Tick/Clock/Region の骨格。
- **M2**: 共通 Inventory（InventoryService の Mutation 直列化）、Item Definition、World Load/Save、`inventories`/`inventory_entries`/`item_instances`。AI は **AIInventoryAdapter** 経由でこの InventoryService を使う（新規 Inventory 実装を作らない）。
- **M3**: 採掘（ResourceNode）・Development（Forge Research）・製作（Craft/Station Job）・狩猟・料理・Hunger・Waste・清掃の Primitive とドメイン。M4 の Action Template（9.3）の Step は **M3 で実装済みの Primitive/Station Job を組み合わせる**（未知 Primitive はコード追加、既存のみなら再ビルド不要）。
- **proto**: `proto/survival/v1/ai.proto` の `DecisionRequest` / `ActionStep` / `ActionDecision`（WSL2 が生成、`Assets/Generated/` に C#）。

> 依存の要点: M4 は Inventory（M2）と生産・生存ドメイン（M3）の Primitive を「テンプレ化」して回す層。Primitive 実体は M2/M3、テンプレ定義データは WorldState（07B）、それを実行する Runner とフォールバックが本書。

---

## 3. 実装対象（Windows / Unity DS）

配置は `unity/SurvivalWorld/Assets/Scripts/Server/AI/`（asmdef を切り DS 専用参照に）を想定。R3 はイベント境界のみ、毎tick はプレーン C#（MVP 5.5.3）。

### 3.1 AI Actor 構成（MVP 9.1）

以下のコンポーネントを実装する（責務は 9.1 準拠）。

| Component | 実装要点 |
|---|---|
| `AIActorController` | Network/Entity lifecycle（AIActor は Server owned、6.3）、現在 Template・Target の保持。 |
| `AIPersonalState` | Needs・Personality・Wanted List・Asset Goal・Inventory Pressure（3.2 の式）。 |
| `AIInventoryAdapter` | M2 の共通 `InventoryService` を AI 用途に公開（free_slots / sellable_count / used_slots / capacity を提供）。 |
| `ActionTemplateRunner` | Step・Retry・Timeout・Interrupt・Compensation（3.3）。 |
| `PrimitiveActionRegistry` | `MoveTo` / `Interact` / `UseItem` / `Craft` / `Purchase` 等。M2/M3 の実装へ委譲。M4 は Purchase をスタブ可（Buyer は M6）。 |
| `AIDecisionClient` | `ai.decision.request` 発行、`ActionDecision` 検証、Lease 適用（3.4/3.5）。 |
| `UtilityFallback` | LLM なしで生命維持・安全待機・現在行動継続（3.6）。 |
| `AIActorSystem`（DS の 6.2 コンポーネント） | 20体の PersonalState 更新・Template 実行・Decision 適用を**時間分割**（6.1 手順4）で束ねる。 |

VContainer で DS の LifetimeScope に登録。20体は `AIActorSystem` が Registry として保持し、update をラウンドロビン分散する。

### 3.2 PersonalState 計算（MVP 9.2 / 基本 7.4 の urgency式・そのまま実装）

Needs は Dedicated Server で**固定周期更新**（Needs 自体の減衰は M3 の Hunger と整合）。`clamp01(x)=min(1,max(0,x))`。全て float 計算（通貨のみ BIGINT 整数、混同しない）。

```
need_score           = clamp01((threshold - current_value) / threshold)
urgency(food)        = clamp01((60 - hunger) / 60)
urgency(cleanup)     = clamp01((used_slots - capacity) / capacity)
urgency(earn)        = clamp01((wealth_goal - net_worth) / max(wealth_goal, 1))
wealth_score         = clamp01((wealth_goal - net_worth) / max(wealth_goal, 1))
inventory_pressure   = used_slots / capacity_slots
cleanliness_pressure = nearby_waste_weight / configured_normalizer
```

- `hunger`・`used_slots`・`capacity`・`net_worth` は M3/M2 のライブ状態から取得（DS メモリ正本）。
- **Wanted List**: `item_tag, priority, max_budget, reason, expires_at, substitute_tags`。
- 購入品は `acquired_at, last_used_at, retention_score` を持ち、時間経過で Sell/Discard 候補（M4 は保持のみでよい、実売却は economy テンプレ）。
- 所有財産増加欲求は購入だけでなく採取・製作・売却・イベント参加を候補にする（テンプレ選択の入力）。
- **フォールバック順序（同点）**: `food > cleanup > earn > sell`（3.6）。
- PersonalState には行動状態（`active_template, template_version, started_at, lease_until, target_refs, failure_count`）と性格（`greed, tidiness, patience, curiosity, event_preference`）を持つ（基本 7.2）。`version`（personal_state_version）を単調増加で管理し Decision 鮮度検査に使う。

### 3.3 fast-layer Template Runner（MVP 9.3 / 基本 7.3）

`action_templates` の definition（07B が配信、JSONB）を解釈して実行する。definition のスキーマ例（基本 7.3）:

```json
{
  "template_id": "economy.sell_surplus",
  "version": 3,
  "tags": ["wealth", "inventory_overflow", "market_available"],
  "preconditions": ["inventory.free_slots < 3", "inventory.sellable_count > 0"],
  "interrupts": ["health_critical", "target_missing", "path_failed"],
  "steps": ["SelectSellableItem", "FindBuyer", "MoveTo", "RequestSale"],
  "min_duration_sec": 20,
  "max_duration_sec": 600
}
```

MVP の Action Templates（9.3、タグ・条件はこの表を正とする。M4 は Buyer 依存テンプレをスタブ可）:

| template_id | 主要Tag/条件 | 概要 | M4 実装 |
|---|---|---|---|
| survival.eat_owned_food | hunger_high, food_owned | 食料選択→Consume | 実装（M3 消費） |
| survival.acquire_food_hunt | hunger_high, weapon_owned, animal_available | 動物探索→狩猟→解体 | 実装（M3 狩猟） |
| survival.cook_meat | raw_meat_owned, cooking_station | Stationへ移動→Cook | 実装（M3 料理） |
| mining.acquire_iron | iron_needed, pickaxe_owned | 鉱脈探索→採掘 | 実装（M3 採掘） |
| smithing.craft_stone_spear | no_weapon, stone_owned, wood_owned | 初期狩猟武器を製作(leather不要) | 実装（M3 製作） |
| development.unlock_spear | blueprint_locked, materials_available | Forgeへ移動→Research | 実装（M3 Development） |
| smithing.craft_spear | weapon_needed, blueprint_unlocked | 材料予約→製作 | 実装（M3 製作） |
| economy.visit_buyer | wanted_item, buyer_available, cash_available | Buyerへ移動→購入 | **スタブ**（Buyer は M6） |
| economy.sell_surplus | inventory_overflow, sellable_item | 売却候補→Buyerへ売却 | **スタブ**（Buyer は M6） |
| inventory.discard_low_value | inventory_overflow, no_buyer | 低価値品を World Drop | 実装（M2/M3 Drop） |
| cleaning.clean_nearby | cleanliness_high, waste_nearby | Waste探索→清掃 | 実装（M3 清掃） |
| worldevent.join | event_available, risk_acceptable | 装備準備→Region移動→参加 | **スタブ**（Event は M5） |
| safety.idle_at_camp | fallback | 安全地点へ移動→待機 | 実装（フォールバック終端） |

Runner 要件:
- **Step 実行**は `PrimitiveActionRegistry` の Primitive へ委譲。未知 Primitive を含む step は起動時にリジェクト（コード追加が必要な旨をログ）。
- **Retry/Timeout**: `max_duration_sec` 超過で Interrupt、`min_duration_sec` 未満での安易な切替を抑止（判断頻度の安定化、7.4）。
- **Interrupt**: `interrupts` 条件（health_critical / target_missing / path_failed）を毎tick 監視。
- **Compensation / Cancel Policy**: Template 切替・失敗時に予約アイテム・Station Lock を解放（9.4）。M2/M3 の予約 API を必ず呼ぶ。
- **Precondition** は切替時と各 Step 前に評価（式は definition の `preconditions`）。

### 3.4 Decision 適用規則（MVP 9.4）

`ai.decision.result.{server_id}` で受けた `ActionDecision` を DS が最終検証する。検証項目（9.4）:

1. actor 存在 / template 存在 / Version 一致（template_version, personal_state_version）
2. Precondition 成立 / Target 参照有効 / Decision 鮮度（Lease 未失効・state_version が現行以上）
3. 重複（同一 decision_id は冪等：副作用を起こさず既処理結果を返す）

- **Lease 適用**: `lease_until` まで当該 Template を継続。Lease 期限で再判断トリガ。
- **Template 切替**: 現在 Step の Cancel Policy 実行→予約/Lock 解放→新 Template を Runner に投入。
- **LLM 結果がない間**: 現行 Template を継続し、完了後は **Utility Fallback**（3.6）。これが M4 の既定経路。

**proto と B.1 の対応（重要・落とし穴6.も参照）**: 送受信の wire message は `proto/survival/v1/ai.proto` の `ActionDecision`（`decision_id, actor_id, state_version, template_id, steps[], created_at_unix_ms`）を**唯一の正**とする。付録 B.1 の概念フィールドは以下にマップして扱う:

| B.1 概念フィールド | M4 での扱い |
|---|---|
| decision_id / actor_id / template_id | proto 同名フィールド |
| personal_state_version | proto `state_version` に格納 |
| world_version | M4 未使用（M5 で proto 拡張候補）。当面 0 許容 |
| template_version | `ai_decisions`（07B）側で管理。DS は取得済み template の version と突合 |
| parameters | `ActionStep.params`（map<string,string>）で受け渡し |
| lease_until | M4 は **DS 側が計算**（`created_at + min/max_duration`）。M5 で proto 拡張候補 |

### 3.5 二層ループの骨格（fast=毎tick / slow=LLM）と Decision Request 発行

- **fast-layer（毎tick）**: `AIActorSystem` が 20体を時間分割で回し、PersonalState 更新→Interrupt 監視→Runner の Step 前進。LLM を待たない。
- **slow-layer（LLM、本体 M5）**: 再判断トリガ発生時に `AIDecisionClient` が `DecisionRequest`（`actor_id, world_id, state_versions, reason`）を **NATS `ai.decision.request`** へ発行。
  - 再判断トリガ（7.4）: 状態変化イベント / Template 完了 / Lease 期限 / 失敗回数閾値 / 重大欲求発生。
  - **M4 は骨格**: request を発行し、result を購読する経路が疎通すればよい。result が来ない/モックでも fallback で進む。
- **NATS クライアント**: DS（Unity C#）は C# NATS client（`NATS.Net`）を NuGetForUnity で導入（M0 の導入順に倣う）。接続先は `NATS_URL`。subject は `ai.decision.request`（発行）と `ai.decision.result.{server_id}`（購読、`server_id` は自DSのID）。

### 3.6 Utility Fallback（基本 7.4 / MVP 9.2）

LLM 不在・タイムアウト・失敗時に DS が安価に評価する数値フォールバック:

```
urgency(food)    = clamp01((60 - hunger) / 60)
urgency(cleanup) = clamp01((used_slots - capacity) / capacity)
urgency(earn)    = clamp01((wealth_goal - net_worth) / max(wealth_goal, 1))
```

- 最大 urgency のタグ（food/cleanup/earn）に対応するテンプレへ切替。**同点は `food > cleanup > earn > sell`**。
- どの urgency も低い（安全・充足）場合は `safety.idle_at_camp`（安全地点へ移動→待機）を終端とする。
- タグ→テンプレの対応は 3.3 の表の主要Tag で解決（food→survival.eat_owned_food / cleanup→cleaning.clean_nearby / earn→mining.acquire_iron 等、cash・所持状況で分岐）。
- **段階的縮退（基本図7-1）**: 現行継続 → Utility AI → 安全待機。LLM の遅延・コスト・障害をゲーム進行のブロッカーにしない。

### 3.7 20体スポーンと同時実行

- `AIActorSystem` が **20体**を World 起動時にスポーン（Server owned、6.3）。個体ごとに Personality を初期化（seed 由来で決定的）。
- 毎tick 全数更新はせず、**時間分割**（ラウンドロビン / バジェット）で 6.1 手順4 に載せる。PersonalState 更新は固定周期、Runner の Step 前進は tick 毎。
- R3 の購読はイベント境界のみ、ホットループでのアロケーション/購読リークを避ける（5.5.3）。

### 3.8 actor_runtime_states の生成→API永続化（`ActorState.Save`）

- DS が `actor_runtime_states`（`actor_id, world_id, version, payload JSONB, updated_at`）を**生成**し、gRPC `ActorState.Save(actor_id, version, personal_state, inventory_summary)`（14.2）で **API に永続化**する。
- **DS は actor_runtime_states を直接 DB 書き込みしない**（フィールド権威=DS、永続 Writer=API、付録C）。payload は PersonalState と Inventory Summary の要約。
- 永続頻度は定期＋Graceful Shutdown 時（M0/M1 の PersistenceAgent と整合）。`version` は PersonalState の personal_state_version と単調整合。

---

## 4. 実装順序（Windows側）

| # | タスク | 完了確認 |
|---|---|---|
| W-1 | `Assets/Scripts/Server/AI/` に asmdef（DS参照）作成、VContainer 登録 | コンパイル通過 |
| W-2 | `AIPersonalState` + 9.2 urgency 式・need_score・pressure 実装 | EditMode Unit で式検証 |
| W-3 | `AIInventoryAdapter`（M2 InventoryService へ委譲、free/used/capacity/sellable） | 参照エラーなし |
| W-4 | `PrimitiveActionRegistry`（MoveTo/Interact/UseItem/Craft、Purchase はスタブ） | Primitive 呼び出し可 |
| W-5 | `ActionTemplateRunner`（Step/Retry/Timeout/Interrupt/Compensation、definition parser） | PlayMode で Template 実行 |
| W-6 | `UtilityFallback`（urgency 最大→テンプレ、同点順、idle 終端） | EditMode で切替順検証 |
| W-7 | `AIDecisionClient`：NATS client 導入・`ai.decision.request` 発行・`ai.decision.result.{server_id}` 購読 | 骨格疎通（07B と結合） |
| W-8 | Decision 適用規則（検証・冪等・Lease・Cancel Policy、B.1↔proto マップ） | 同一 decision_id 再受信で副作用なし |
| W-9 | `AIActorSystem`：20体スポーン・時間分割更新 | 20体が同時に自律行動 |
| W-10 | `actor_runtime_states` 生成→`ActorState.Save`（gRPC/API） | API に永続化される |
| W-11 | `scripts\unity_test.ps1`（EditMode/PlayMode）+ DS Linux ビルド | テスト緑・Headless 起動 |

`scripts\ai_soak_smoke.ps1`（任意）: DS を Headless 起動し 20体が一定時間フォールバックで自律行動することを確認するローカル smoke（PlayMode 代替）。

---

## 5. テスト・受入（Windows側）

MVP 18.1 の区分に沿う。

- **EditMode Unit**（18.1 Definitions / Template parser / Need score）:
  - `Need score`：9.2 の各式が境界（hunger=0/60/120、used_slots=capacity 等）で clamp01 通り。
  - `Template parser`：definition JSONB の tags/preconditions/interrupts/steps を正しく解釈、未知 Primitive を含む step を検出。
  - `UtilityFallback`：urgency 同点時に `food > cleanup > earn > sell` の順、全低時に idle。
  - `Decision 冪等`：同一 decision_id 再適用で副作用ゼロ。
- **PlayMode**（18.1 AI Template）:
  - `AI Template`：hunger 低下→`survival.*` 系テンプレ実行→Consume で hunger 回復。Interrupt（health_critical）で切替＆Cancel Policy 実行（予約/Lock 解放）。
  - Template 切替時に予約アイテム・Station Lock が確実に解放される。
- **同時実行**：20体スポーンで tick 予算内更新、Profiler で 20体時の tick/allocation を記録（未達は既知Issue に数値付き、DoD）。
- **結合（07B と）**：`ai.decision.request` 発行→worldstate 受信→（M4 はモック/未接続）→`ai.decision.result.{server_id}` 購読の疎通。result 不在でも 20体が自律行動継続。
- **受入**：LLM を一切繋がない状態で DS が Headless 起動し、20体が生存（food）・清掃（cleanup）・稼得（earn、採取/製作）・安全待機（idle）を Utility Fallback で継続。`actor_runtime_states` が API に永続化される。

---

## 6. 落とし穴（Windows側）

1. **LLM 待ちで AI が止まる**のは NG。fast-layer は毎tick、slow-layer 未応答時は現行継続→Utility Fallback（3.5/3.6）。M4 は result 不在が既定運用。
2. **毎tick R3 購読/アロケーション**でのリーク・GC スパイク。ホットループはプレーン C#、R3 はイベント境界のみ（5.5.3）。20体で顕在化しやすい。
3. **Template 切替時の解放漏れ**：予約アイテム・Station Lock を Cancel Policy で必ず解放（9.4）。漏れると M2/M3 の在庫/Station がデッドロック。
4. **Inventory の二重実装**：AI 専用 Inventory を作らない。必ず M2 の `InventoryService` を `AIInventoryAdapter` 経由で使う（単一 Writer、6.2）。
5. **actor_runtime_states を DS が直接 DB 書き込み**しない。永続は `ActorState.Save`（API）経由（付録C、フィールド権威=DS / 永続 Writer=API）。
6. **proto と B.1 のフィールド差**：wire は `ai.proto` の `ActionDecision` が正。`personal_state_version→state_version`、`parameters→ActionStep.params`、`lease_until` は DS 側計算、`template_version`/`world_version` は 07B の `ai_decisions` と突合（3.4）。勝手に proto を書き換えない（proto 変更は 07B が生成、両冊合意で）。
7. **Buyer/Event 依存テンプレ**（economy.*, worldevent.join）は M4 でスタブ。Precondition を満たさない前提で fallback が earn を採取・製作へ流すこと。
8. **決定性**：20体の Personality/初期配置は seed 由来で決定的に（Soak/再現の前提）。
9. **DS の NATS client**：`server_id` を購読 subject に正しく埋める（`ai.decision.result.{server_id}`）。取り違えると他DS宛の結果を拾う/取りこぼす。
10. **`-runTests` に `-quit` を付けない**（M0 03A 8章）。DS ビルドは Dedicated Server モジュール必須。

---

## 参考資料

- MVP 詳細設計 v0.2.2：9章（9.1 AI Actor構成 / 9.2 PersonalState / 9.3 Action Templates / 9.4 Decision適用規則）、6章（6.1 ゲームループ / 6.2 Server コンポーネント / 6.3 権限）、13章（actor_runtime_states 他）、14.2（ActorState.Save）、14.3（NATS）、付録B.1（ActionDecision）、付録C（データ所有権）、18.1（テスト区分）、19（DoD）。
- 基本設計 v0.2.1：7章（7.1 二層ループ / 7.2 PersonalState / 7.3 行動テンプレート / 7.4 urgency式・フォールバック）、図7-1、9.4（所有権）。
- `proto/survival/v1/ai.proto`（DecisionRequest / ActionStep / ActionDecision）。
- [R-R3] R3 / [R-VC] VContainer / [R-UT] UniTask（M0 03A 参考資料）。
- [R-NATSNET] [NATS .NET client](https://github.com/nats-io/nats.net)
