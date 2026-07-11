---
title: "M5 実装指示書（Windows ネイティブ側）"
subtitle: "Dedicated Server での ActionDecision 適用（AI slow-layer 統合）/ World Event 現地効果 / EventProposal 承認フロー / LLM 失敗時 Fallback"
document_id: "IMPL-M5-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M5 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / R3 / VContainer / UniTask"
related_document: "08B_M5実装指示書_WSL2側_v0.1.md, 07A_M4実装指示書_Windows側_v0.1.md, 07B_M4実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M5 実装指示書：Windows ネイティブ側 v0.1

本書は M5（WorldState/LLM）の作業を **Windows ネイティブ側**（Unity Dedicated Server での decision 適用・World Event 現地効果）に限定して指示する。バックエンド（Projection/Decision Worker/実 LLM/gRPC/DB）は別冊 **08B（WSL2側）** を参照。第0章「分担と連携」は両冊で共通・要点のみ再掲し、詳細は M0（03A/03B）第0章を正とする。

M5 の中心は WSL2 側（実 LLM 化）で、Windows 側は **DS が受け取った ActionDecision を実行し、承認された World Event の現地効果を適用する側**である。M4 の fast-layer（Utility）と合流させ、LLM 由来の slow-layer を統合する。

---

## 0. 分担と連携（要点・詳細は M0 第0章）

### 0.1 環境別 責務分担（M5 該当分）

| 領域 | 担当環境 | 主なタスク |
|---|---|---|
| DS での ActionDecision 適用（AI slow-layer） | **Windows** | `steps` 実行、M4 fast-layer と合流、Lease 適用 |
| World Event 現地効果適用 | **Windows** | `world_event_instances` state に従い spawn/効果/終了 |
| EventProposal→承認→適用 の DS 側フロー | **Windows** | 負荷/競合/Version 検査・Reject・状態進行 |
| LLM 由来 decision の遅延/失敗時 Fallback | **Windows** | M4 Utility Fallback へ退避 |
| Projection / Decision Worker / 実 LLM / gRPC / DB | **WSL2** | 08B が担当。Windows は `services/` を触らない |

### 0.2 競合回避（厳守）

- **Windows（本書）は `unity/` と `scripts/*.ps1` のみ**を編集する。
- `services/`, `proto/`, `infra/`, `Makefile`, `scripts/*.sh` は触らない（08B が担当）。
- `unity/SurvivalWorld/Assets/Generated/`（`ai.proto`/`worldevent.proto` の C# 生成物）は **WSL2 が生成**する。Windows は**参照してコンパイルするのみ**（コミット漏れ時は WSL2 の CI が検出・M0 0.4/0.5）。

### 0.3 境界成果物（M5 で参照する分）

| 成果物 | 生成側 | 消費側（本書） | 置き場所 |
|---|---|---|---|
| `ai.proto`（ActionDecision/ActionStep/DecisionRequest） → C# | WSL2 | Unity DS | `unity/SurvivalWorld/Assets/Generated/` |
| `worldevent.proto`（WorldEventService/WorldEventState） → C# | WSL2 | Unity DS | `unity/SurvivalWorld/Assets/Generated/` |
| NATS Subject / DB テーブル定義 | WSL2（08B） | Unity DS（消費仕様） | 08B 3 章 |

---

## 1. 対象と前提（Windows側）+ M5 DoD

- 本書の完了で、Dedicated Server が **`ai.decision.result.{server_id}` の ActionDecision を検証・適用**（AI slow-layer）し、M4 fast-layer（Utility）と合流して 20 AI が動作し、承認された **World Event 3 種**の現地効果を `world_event_instances` の state に従い適用・終了できる状態にする。
- LLM 由来 decision が遅延/失敗しても Tick を止めず、**M4 Utility Fallback へ退避**する。

### 1.1 Windows側 M5 DoD

- DS が ActionDecision を **9.4 の規則で検証**（actor/template 存在・Version・Precondition・Target 参照・Decision 鮮度・重複）し、`steps` を PrimitiveActionRegistry で実行する。
- LLM slow-layer（Template 切替）と M4 fast-layer（Template 内 Step 実行）が合流し、Template 切替時に Cancel Policy（予約/Station Lock 解放）が走る。
- 承認された World Event の現地効果（great_hunt / rare_resource / rare_buyer_rush）が **Spawn Cap/供給予算/duration を超えず**適用・終了し、終了集計（spawned/harvested/purchased/remaining/participant_count）を WorldEventService へ確定する。
- EventProposal を DS が負荷/競合/地域可否/Version で検査でき、Reject／Approved→適用のフローが動く。
- LLM decision の遅延/失敗時（60 秒超でも）Tick を継続し、Utility Fallback へ移行する（AT-014）。
- EditMode/PlayMode テストが `scripts\unity_test.ps1` で緑、`scripts\unity_build_server.ps1` で Linux Dedicated Server ビルドが生成される。

---

## 2. 前提成果物（M0〜M4）

| Milestone | 参照する成果物（M5 で前提とする） |
|---|---|
| M0 基盤 | Unity プロジェクト（URP/FishNet/R3/VContainer/UniTask）, `Assets/Generated/`（proto C#）, ビルド/テスト PowerShell（03A） |
| M1 接続 | FishNet Dedicated Server 接続, 2 Client 移動 |
| M2 Inventory/Save | 共通 Inventory, World Load/Save |
| M3 Survival | 採掘/製作/狩猟/料理/Hunger/Waste/清掃（PrimitiveAction の実体） |
| M4 AI | **AIActorController / AIPersonalState / ActionTemplateRunner / PrimitiveActionRegistry / AIDecisionClient / UtilityFallback（9.1）**, fast-layer, 20 AI |

- M5 は M4 の `AIDecisionClient`（Decision Request 発行・Result 検証・Lease 適用）と `UtilityFallback` を**拡張**して、実 LLM 由来の ActionDecision を扱う。

---

## 3. 実装対象（Unity Dedicated Server）

> ファイルは `unity/SurvivalWorld/Assets/**`（DS 用アセンブリ）と `scripts/*.ps1` のみ。`Assets/Generated/` は WSL2 生成物を参照。

### 3.1 ActionDecision の steps 実行（AI slow-layer 統合・MVP 9.1 / 9.4 / 付録B.1）

- **受信**: `AIDecisionClient` が `ai.decision.result.{server_id}` の **ActionDecision**（`ai.proto`）を受け取る（DS↔NATS 経路は M4 の DecisionRequest 発行経路と対称。Client→API/WorldState は禁止だが DS は可・基本設計 3.1/5.2）。
- **検証（9.4・DS が最終検証・10.2 手順5）**:
  - `actor_id` の actor が存在し稼働中。
  - `template_id` が `action_templates`（9.3）に存在し `template_version` 一致。
  - **Precondition**（タグ/前提条件）成立、**Target 参照**が有効。
  - **Decision 鮮度**: `lease_until`（付録B.1）が未来、`state_version`/`personal_state_version` が現在と整合。
  - **重複（冪等）**: 同一 `decision_id` の再受信は**副作用を起こさず既処理結果を返す**（9.4）。
  - いずれか不成立なら **その Decision を破棄**し、現行 Template 継続→完了後 Utility Fallback（3.4）。
- **適用**: 検証通過した ActionDecision の `template_id` へ切替し、`steps[]{action_template_id, params}` を **`ActionTemplateRunner` + `PrimitiveActionRegistry`**（`MoveTo`/`Interact`/`UseItem`/`Craft`/`Purchase` 等・9.1）で実行。Step の Retry/Timeout/Interrupt/Compensation は Runner に委譲。
- **Lease 適用**: `lease_until` の期間は当該 Template を継続（9.4）。期間内に新 Decision が来なければ現行継続、期限後は Utility Fallback。
- **二層合流（基本設計 7.1）**: LLM は **slow-layer（どの Template にするか＝Template 切替）**を担い、M4 の **fast-layer（Template 内の Step 進行・生命維持）**と合流する。Template 切替時は **現在 Step の Cancel Policy を実行**し、予約アイテム/Station Lock を解放（9.4）。
- **Allowed ID の再確認**: `steps[].action_template_id` は PrimitiveActionRegistry の登録 ID のみ許可（WSL2 側でも検証済みだが DS が最終防衛・17章 MVP-SEC-008）。

対象クラス（M4 を拡張）:

```text
Assets/Scripts/AI/
├─ AIDecisionClient.cs      # ai.decision.result.{server_id} 受信・9.4 検証・Lease 適用（拡張）
├─ ActionTemplateRunner.cs  # steps 実行・Cancel Policy（M4 既存を利用）
├─ PrimitiveActionRegistry.cs # MoveTo/Interact/UseItem/Craft/Purchase（M4 既存を利用）
└─ UtilityFallback.cs       # LLM 不在/失敗時の退避（3.4・M4 拡張）
```

### 3.2 承認された World Event の現地効果適用（MVP 8.2 / 10.3 / 10.4）

- DS は `world_event_instances`（WSL2 が API 経由で登録・08B 3.5）の **state に従い spawn/効果/終了**を現地適用する。状態は基本設計 8.2 の `Proposed→Approved→Scheduled→Preparing→Active→Completing→Completed`。DS は進行を **Event 化し `WorldEventService.UpdateState`** で API へ確定（08B 3.5）。API へ確定する主要 state は `WorldEventState`（PROPOSED/ACTIVE/COMPLETED/REJECTED）。
- **3 種の現地効果（10.3・暫定制約を DS が強制）**:

| template_id | 現地効果 | DS が強制する暫定制約 |
|---|---|---|
| `world_event.great_hunt` | Rare Deer を**段階 Spawn** | duration **15分**、**alive cap +40**、**total cap 100** を超えない |
| `world_event.rare_resource` | Rare Ore Node を追加 | duration **15分**、**node cap 20**、**total yield budget** を超えない |
| `world_event.rare_buyer_rush` | Rare Buyer を **3体** Spawn | duration **10分**、各在庫独立・**Rare 保証なし** |
- **Spawn Budget/供給予算/座標/同時生存数は DS（+ルール）が決定**（基本設計 8.2）。LLM 提案（強度/地域タグ）を実値に翻訳するのは DS 側。
- **終了時集計**（10.4）: `spawned`, `harvested`, `purchased`, `remaining`, `participant_count` を集計し、`UpdateState(new_state=COMPLETED, stats={...})` で確定。
- Client → AI 干渉なし（6.4 / 17章）を維持。イベント Spawn 物にも非干渉ポリシーを適用。

対象クラス:

```text
Assets/Scripts/WorldEvent/
├─ WorldEventDirectorClient.cs # worldevent.proposal.{server_id}/worldevent.result 連携・状態進行（3.3）
├─ WorldEventInstanceRunner.cs # state に従い spawn/効果/終了・Cap 強制・集計（3.2）
├─ GreatHuntEffect.cs          # Rare Deer 段階 Spawn（alive+40/total100/15分）
├─ RareResourceEffect.cs       # Rare Ore Node（node20/yield budget/15分）
└─ RareBuyerRushEffect.cs      # Rare Buyer 3体（独立在庫/10分/Rare保証なし）
```

### 3.3 EventProposal→承認→適用 の DS 側フロー（MVP 8.2 / 10.4）

- DS は `worldevent.proposal.{server_id}` の **EventProposal**（`ai.proto`・付録B.2）を受け取り、**負荷・競合イベント・地域利用可否・Version を検査**して**拒否できる**（基本設計 8.2）。
- **承認**時: `WorldEventService.Register(proposal_id, template_id, world_id, region_id, params)` を呼び `event_instance_id` を得て、Preparing→Active へ進める（08B 3.5/3.6）。
- **拒否**時: `reason_code` を付けて `worldevent.result`（Rejected）へ返す。**LLM に自由な代替を再生成させず次回評価まで待つ**（10.4）。
- 進行に応じ `UpdateState` で PROPOSED→ACTIVE→COMPLETED を条件付き遷移（`expected_state` 一致・08B 3.5）。二重遷移は冪等に無視。
- `worldevent.result`（Approved/Rejected/Completed）は WSL2 側 Director/承認と協調（08B 3.6）。

### 3.4 LLM 由来 decision の遅延/失敗時 Fallback（MVP 9.4 / 16章・AT-014）

- **LLM 結果がない間は現行 Template を継続**し、完了後は **Utility Fallback を使用**（9.4）。
- Fallback は **最大 urgency のタグに対応するテンプレへ切替**、同点は **`food > cleanup > earn > sell`**（9.2）。最終退避は `safety.idle_at_camp`（fallback タグ・9.3）。
- **トリガ**: `ai.decision.result` が `lease_until` までに来ない／`decision_id` 検証失敗（3.1）／`ai.decision.request` の指数 Backoff リトライ中（16章 LLM timeout・08B 3.7）。
- **要件**: LLM が 60 秒超停止しても **Server Tick を継続**（20Hz・基本設計 5.2）し、AI が Fallback へ移行（AT-014）。生命維持・安全待機・現在行動継続を UtilityFallback が担保（9.1）。
- fast-layer（M4）は LLM 停止中も独立に動く（二層ループ・基本設計 7.1）。slow-layer（Template 切替）だけが Fallback 判断へ切替わる。

---

## 4. 実装順序（Windows側）

| # | タスク | 完了確認 |
|---|---|---|
| W-1 | `Assets/Generated/`（`ai.proto`/`worldevent.proto` C#）を参照しコンパイル | 生成物受領・コンパイル通過 |
| W-2 | `AIDecisionClient` 拡張（3.1: ActionDecision 受信・9.4 検証・重複冪等・Lease） | 不正/重複 Decision を破棄 |
| W-3 | steps 実行の slow/fast 合流（3.1: Template 切替 + Cancel Policy） | Template 切替で予約/Lock 解放 |
| W-4 | UtilityFallback 拡張（3.4: LLM 失敗/遅延で退避・food>cleanup>earn>sell） | LLM 停止 60 秒超で Tick 継続・Fallback |
| W-5 | WorldEventInstanceRunner（3.2: state 追従・Cap 強制・集計） | Cap/duration を超えない |
| W-6 | 3 種 Effect（3.2: great_hunt/rare_resource/rare_buyer_rush） | 各暫定制約を満たす |
| W-7 | 承認フロー（3.3: EventProposal 検査・Register/UpdateState・reason_code） | Reject/Approved/Completed が遷移 |
| W-8 | `scripts\unity_test.ps1`（EditMode/PlayMode） | AI Template/イベント現地効果テスト緑 |
| W-9 | `scripts\unity_build_server.ps1`（Linux Dedicated Server） | ビルド成果物生成 |

---

## 5. テスト・受入（Windows側）

- **AT-013（AI 自律動作）**: LLM Decision で **Template 切替後、次の Decision まで継続**。Inventory も共通規則。`decision_id` 重複は副作用なし。
- **AT-014（LLM 停止）**: LLM が **60 秒超停止しても Server Tick 継続**、AI が Fallback（`food>cleanup>earn>sell`／`safety.idle_at_camp`）へ移行。
- **AT-015（Great Hunt）**: Proposal 承認後、**Spawn Cap（alive+40/total100）を超えず** Rare Deer が段階増加し 15 分で終了。
- **AT-016（Rare Resource）**: **供給予算（node20/yield budget）を超えず** Rare Ore Node が出現・15 分で終了。
- **AT-017（Rare Buyer Rush）**: **3 Buyer** が出現し、各在庫が有限・独立、**Rare 保証なし**、10 分で終了。
- **AT-006/007（非干渉維持）**: イベント Spawn 物にも Player↔AI 干渉禁止を維持。
- 自動テスト区分（18.1）: Unity EditMode（Template parser/Damage Matrix）・PlayMode（AI Template/Station jobs）を CI/ローカルで実行。
- DoD（19.1）: Server 権威を迂回する Client 側確定処理がないこと（LLM 由来 decision も DS が最終検証）。Headless 起動・Graceful Shutdown・Snapshot を確認。

---

## 6. 落とし穴（Windows側・LLM 由来 decision の扱い）

- **未検証の Decision 適用（重大）**: `ai.decision.result` を鵜呑みにしない。**9.4 の全検証（actor/template/Version/Precondition/Target/鮮度/重複）を DS が最終防衛**（17章 MVP-SEC-008 / DoD）。WSL2 の Allowed ID 検証を通っていても DS で再確認。
- **decision の重複適用**: 再配信で同一 `decision_id` が二度来る。**冪等（既処理結果を返す・副作用なし）**を守る。
- **Lease 切れの放置**: `lease_until` を過ぎても新 Decision を待ち続けると硬直する。期限後は現行完了→Utility Fallback へ確実に移行。
- **Template 切替の解放漏れ**: 切替時に Cancel Policy を実行しないと予約アイテム/Station Lock がリークする（9.4）。
- **fast/slow の二重制御**: fast-layer（Step 進行）と slow-layer（Template 切替）が競合しないよう、slow は Template 切替のみを担い、Step 実行は Runner に一本化（基本設計 7.1）。
- **Spawn Cap 超過**: イベント効果で Cap/供給予算/duration を超えると AT-015/016/017 不合格。DS が **上限を強制**し、上限到達で Spawn 停止。実値は LLM でなく DS/ルールが決定。
- **LLM 停止で Tick を止める**: LLM 応答待ちで Tick をブロックしない（AT-014）。Decision Request は非同期・待たない。応答が無ければ Fallback。
- **Generated の参照ミス**: `Assets/Generated/`（WSL2 生成の C#）のコミット漏れ/未更新で `ActionDecision`/`WorldEventState` がコンパイルできない。WSL2 の `make proto` 出力を pull してから作業（M0 0.5）。
- **`.ps1` の改行**: PowerShell スクリプトは CRLF（`.gitattributes` で固定・M0 0.3）。`.sh` は触らない（08B 管轄）。
- **Unity.exe パスのパッチ版差**: ビルド/テスト PowerShell の版指定を実環境に合わせる（M0 5.2）。

---

## 参考資料

[R1] [Unity 6000.5.x Release Notes](https://unity.com/releases/editor/whats-new/6000.5.3f1)
[R2] [Unity Manual: Dedicated Server build](https://docs.unity3d.com/6000.2/Documentation/Manual/dedicated-server-build.html)
[R3] [Unity Input System](https://docs.unity3d.com/Packages/com.unity.inputsystem@latest)
[R-FISH] [FishNet Documentation](https://fish-networking.gitbook.io/docs/)
[R-R3] [Cysharp/R3](https://github.com/Cysharp/R3)
[R-UT] [UniTask](https://github.com/Cysharp/UniTask)
[R-VC] [VContainer](https://github.com/hadashiA/VContainer)
