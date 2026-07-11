---
title: "M3 実装指示書（Windows ネイティブ側）"
subtitle: "Survival Vertical Slice / Dedicated Server ゲームロジック・Client UI"
document_id: "IMPL-M3-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M3 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / R3 / VContainer / UniTask"
related_document: "06B_M3実装指示書_WSL2側_v0.1.md, 05A_M2実装指示書_Windows側_v0.1.md, 05B_M2実装指示書_WSL2側_v0.1.md, 04A_M1実装指示書_Windows側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M3 実装指示書：Windows ネイティブ側 v0.1

本書は M3（Survival Vertical Slice）の作業を **Windows ネイティブ側**（Unity で書く Dedicated Server 上のゲームロジックと Client UI/入力）に限定して指示する。永続化・NATS 配信・マスタデータ供給・DB マイグレーション・テストは別冊 **06B（WSL2側）** を参照。第0章「分担と連携」は両冊で要点を共有する（詳細な規約の正本は M0 の 03A/03B 第0章）。

M3 の成果（MVP 第19章）: **採掘・Development・製作・狩猟・料理・Hunger・Waste・清掃**の縦切り。Windows 側は、これらの**ゲームロジックを Dedicated Server（Unity ビルド）上で権威実行**し、成立した各行動を **Domain Event として Outbox 生成**して `WorldData.AppendEvents`（gRPC）へ送る。Client 側は UI・入力・フィードバックを担う。

---

## 0. 分担と連携（要点・両冊共有）

> 規約の正本（リポジトリ配置 / Git・LFS・改行 / 境界成果物）は M0 03A/03B 第0章。M3 で変わらない。本章は M3 固有の連携点に絞る。

### 0.1 M3 の環境別 責務分担

| 領域 | 担当環境 | M3 での主なタスク |
|---|---|---|
| DS 上のゲームロジック（Hunger/採掘/製作/狩猟/料理/清掃/Damage Matrix） | **Windows**（Unity Server ビルド） | 権威 Simulation、InteractCommand/PrimaryActionCommand 処理、Domain Event 生成 |
| Domain Event の Outbox 生成・`WorldData.AppendEvents` 呼び出し | **Windows**（DS = gRPC Client） | event_id=ULID / local_sequence を **DS 採番**、OK/DUPLICATE/CONFLICT 処理 |
| Client UI・入力・フィードバック | **Windows**（Unity Client ビルド） | Interact スキャン、Hunger/Station 進捗 UI、複製状態の R3 購読 |
| Domain Event の永続確定（inventory_entries/item_instances/currency_ledger） | **WSL2**（api = 永続 Writer） | `AppendEvents` サーバー実装、event_id 重複排除、sequence 採番 |
| NATS `world.{id}.event.resource` / `.actor` 発行・購読土台 | **WSL2** | Outbox Relay → NATS、worldstate 購読スケルトン |
| ResourceNode / Recipe / ItemDefinition マスタデータ供給 | **WSL2**（migration 0003 + Bootstrap 配信） | DS は Bootstrap payload で受領しキャッシュ |
| DB マイグレーション 0003、テスト（Go/Integration） | **WSL2** | 06B 参照 |

### 0.2 競合回避（厳守）

- **Windows 側が触るのは `unity/` 配下と `scripts/*.ps1` のみ。**
- `unity/SurvivalWorld/Assets/Generated/`（proto C# 生成物）は **WSL2 が生成する。Windows は参照しコンパイルするだけ**（編集しない）。
- proto 変更が必要な場合（本 M3 では原則不要。gameplay.proto の InteractCommand/PrimaryActionCommand と worlddata.proto の AppendEvents は M0 で確定済み）は **WSL2 側 06B に依頼**する。Windows から `.proto` を編集しない。

### 0.3 境界契約：Domain Event 型カタログ（A/B 共通・正本は本章と 06B 第0章で一致させる）

DS が生成し API が永続確定する M3 の Domain Event（`DomainEvent.type` と `payload` の JSON）。**A（生成側）と B（消費・永続側）で完全一致させること。**齟齬は AppendEvents の適用不能・ドリフトの原因になる。

| type | aggregate_id | payload 主要フィールド | NATS subject（B が発行） |
|---|---|---|---|
| `resource.mined` | node_id | `node_id, resource_type, mined_amount, grants[{item_definition_id, quantity, item_instance_id?}], remaining_amount, node_version` | `world.{id}.event.resource` |
| `resource.node_depleted` | node_id | `node_id, depleted_at_unix_ms` | `world.{id}.event.resource` |
| `resource.node_regenerated` | node_id | `node_id, remaining_amount, node_version` | `world.{id}.event.resource` |
| `station.job_started` | station_id | `station_id, recipe_id, actor_id, reserved[{item_definition_id, quantity}]` | `world.{id}.event.actor` |
| `station.job_completed` | station_id | `station_id, recipe_id, actor_id, consumed[], produced[{item_definition_id, quantity, item_instance_id?}]` | `world.{id}.event.actor` |
| `station.job_cancelled` | station_id | `station_id, recipe_id, actor_id, released[]` | `world.{id}.event.actor` |
| `development.blueprint_unlocked` | world_id | `blueprint_id, recipe_id` | `world.{id}.event.actor` |
| `farm.crop_planted` | plot_id | `plot_id, crop_id, ready_at_unix_ms` | `world.{id}.event.actor` |
| `farm.crop_harvested` | plot_id | `plot_id, crop_id, produced[]` | `world.{id}.event.actor` |
| `hunting.animal_killed` | animal_id | `animal_id, species, killer_id, carcass_id` | `world.{id}.event.actor` |
| `hunting.carcass_butchered` | carcass_id | `carcass_id, drops[], drop_seed` | `world.{id}.event.actor` |
| `cooking.completed` | station_id | `station_id, actor_id, consumed[], produced[]`（cooked_meat x1 + food_waste x1/x2） | `world.{id}.event.actor` |
| `inventory.item_consumed` | actor_id | `actor_id, item_definition_id, hunger_delta` | `world.{id}.event.actor` |
| `item.discarded` | actor_id | `actor_id, world_item_id, item_definition_id, item_instance_id?, quantity, position{x,y,z}, tags[]` | `world.{id}.event.actor` |
| `cleaning.completed` | world_item_id | `world_item_id, disposed_item_definition_id, reward_amount` | `world.{id}.event.actor` |
| `character.vitals_changed` | actor_id | `actor_id, hunger, health, cause`（**閾値越え時のみ**発行） | `world.{id}.event.actor` |

- `grants[]/produced[]` に `item_instance_id?` を含むのは、品質/耐久を持つ**個体**（武器・防具など rarity>0 相当の個体）を生成する場合のみ。ULID を DS が採番して埋める。stack 物（stone 等）は `item_definition_id + quantity` のみ。
- `resource.mined` の `grants[]` は必ず 1 種類以上。**Inventory に入り切らない場合は event を発行しない**（Node も減算しない。8.3-3）。
- `character.vitals_changed` は**毎 Tick 発行しない**。Warning(30)/Critical(10)/Starvation(0) の閾値越え、または Consume による回復時のみ発行し、DB 書き込み量を抑える。

### 0.4 連携フロー（M3 代表例）

1. **採掘成立**: DS が `ResourceNodeSystem` で remaining 減算 → `resource.mined` を Outbox へ → `OutboxAgent` が `WorldData.AppendEvents` 送信 → B(api) が `domain_events` + `inventory_entries`/`item_instances` を確定し `RESULT_STATUS_OK`。二重送信は同一 `event_id` で `DUPLICATE`（AT-003）。
2. **マスタデータ**: DS 起動時 `LoadBootstrap` の payload に ItemDefinition/Recipe/ResourceNodeDef が含まれる（B が migration 0003 で seed・Bootstrap で配信）。DS は `MasterDataStore` にキャッシュし、数量・重量・レシピ・ツール要件の権威値として使う。**Client にバンドルするのは表示用メタ（名前・アイコン）だけ**（5.4：重要データを Client Cache に置かない）。
3. **NATS**: DS は NATS に直接発行しない。B が Outbox Relay で `world.{id}.event.resource` / `.actor` を発行し、worldstate が購読土台で受ける（M4/M5 の下地）。

---

## 1. 対象と前提（Windows側）／ M3 DoD

### 1.1 対象

- **DS（Unity Dedicated Server ビルド）上の権威ゲームロジック**: Hunger/Health、ResourceNode 採掘、生産ステーション（forge/anvil/cooking station/farm plot）での製作・Development、狩猟→解体、料理→ゴミ→清掃、Damage Matrix。
- **Command 処理**: `InteractCommand` / `PrimaryActionCommand`（gameplay.proto, MVP 14.1）。
- **Domain Event 生成と送出**: 各行動結果を Domain Event 化し、`event_id`(ULID)・`local_sequence` を **DS が採番**して Outbox に積み、`WorldData.AppendEvents` へ送る。
- **Client**: 入力（Interact=E / PrimaryAction=左クリック 他, 5.2）、Interact 候補表示、Hunger/Health・Station 進捗・清掃結果のフィードバック UI。

### 1.2 M3 Windows側 DoD

- Hunger が Server Tick で減少し、Warning/Critical/Starvation の閾値挙動（Starvation 時 Health 減少）が Server 権威で成立する（8.1）。
- `stone_pickaxe` 装備で ResourceNode を採掘し、remaining 減算・枯渇・再生成が Server で成立、`resource.mined`/`node_depleted`/`node_regenerated` を Outbox 生成する（8.3）。
- 生産ステーションで **stone_pickaxe / stone_spear / iron_hunting_spear / rare_weapon_craft** の製作と **iron_spear_research（Development→Blueprint 解放）**が、材料 reserved→消費、Cancel で予約解除、を伴い成立する（8.4）。
- `stone_spear`（石+木, leather 不要）を初期から製作でき、Deer 狩猟→解体で leather 取得→Iron Hunting Spear へ到達できる（デッドロック回避, 8.4 / AT-022）。
- Deer/Rare Deer を Server 検証の狩猟で倒し、Carcass を Interact 解体して Seed 付きドロップ確定、`hunting.*` を生成する（8.5 / AT-008）。
- Cooking Station で raw_meat→cooked_meat + food_waste（luxury は waste x2）を生成、Consume で Hunger 回復、Discard→WorldItem、Clean→Disposal を成立させる（8.6 / AT-009, AT-010）。
- **Damage Matrix（6.4）**が Target Filter と DamageService の二重防御で成立：Player/AI→Animal 許可、Animal→Player/AI 許可、Player↔AI・Player↔Player・AI↔AI 拒否（AT-006, AT-007）。
- 各 Domain Event が **event_id(ULID)/local_sequence を DS 採番**して Outbox に入り、`WorldData.AppendEvents` で送出、OK/DUPLICATE/CONFLICT を処理する。二重送信で 1 回分のみ反映（AT-003）。
- Server 権威を迂回する **Client 側確定処理がない**（19.1）。数量/品質/耐久を Client が自由指定できない（7.3）。
- EditMode（Damage Matrix・Recipe・Drop Table・Hunger 閾値）と PlayMode（採掘・Station Job・狩猟・料理の一連）テストが `scripts\unity_test.ps1` で緑。
- Linux Dedicated Server ビルドが `scripts\unity_build_server.ps1` で生成でき Headless 起動する（19.1）。

---

## 2. 前提成果物（M0–M2 / Windows側から見た依存）

| 由来 | 前提 | 参照 |
|---|---|---|
| M0 | Unity プロジェクト（URP+R3/VContainer/UniTask/FishNet/Input System）、BuildScript、`scripts\unity_*.ps1`、`Assets/Generated/` 受け皿 | 03A |
| M0 | proto C# 生成物（gameplay/worlddata/common/worldevent）が `Assets/Generated/` に出力済み。`InteractCommand`/`PrimaryActionCommand`/`DomainEvent`/`AppendEventsRequest`/`WorldDataService` が利用可能 | 03B 5.4 / proto |
| M1 | Auth/Join Ticket/FishNet 接続、2 Client 移動（TPS/WASD）、NetworkPlayerController、ServerBootstrap | 04A（M1） |
| M2 | 共通 Inventory（24 slot / 40.0 重量 / version+1 / reserved）、Item Definition 取り込み、World Load/Save、`InventoryService`（Mutation 直列化）、`PersistenceAgent`/Outbox 基盤、Cache 削除復旧 | 05A（M2）, MVP 7 |
| M2/B | api の `WorldData.LoadBootstrap`/`AppendEvents`/`SaveSnapshot`、DB(domain_events/inventories/inventory_entries/item_instances/currency_ledger/outbox_messages) | 03B 7章, 05B, MVP 13 |

> M2 で `InventoryService`（全 Mutation を owner 単位に直列化し Delta/Event 生成）と `PersistenceAgent`（Outbox→AppendEvents）が入っている前提。M3 はこの 2 つに **reserved（予約）**と **M3 の Event 種別**を積み増す形で拡張する。存在しない場合は M2 の完了を先に確認すること。

---

## 3. 実装対象（DS ゲームロジック / Client UI）

### 3.0 Unity ファイル配置（asmdef 分割・Windows が新規作成）

```text
unity/SurvivalWorld/Assets/Scripts/
├─ Shared/            (asmdef: Survival.Shared)   … Client/Server 共有の定数・DTO・enum
│  ├─ SurvivalTuning.cs          # 暫定定数（Hunger/採掘/Recipe 時間 等）
│  ├─ MasterData/                # ItemDefinition/Recipe/ResourceNodeDef の POCO と MasterDataStore
│  └─ Events/DomainEventTypes.cs # type 文字列定数と payload DTO（0.3 と一致）
├─ Server/            (asmdef: Survival.Server, Define: UNITY_SERVER 前提のロジック)
│  ├─ Simulation/CharacterSimulation.cs      # Hunger/Health（M2 から拡張）
│  ├─ Simulation/ResourceNodeSystem.cs       # 採掘・枯渇・再生成
│  ├─ Simulation/StationJobSystem.cs         # forge/anvil/cooking/farm の Job 実行
│  ├─ Simulation/FarmPlotSystem.cs           # potato 栽培
│  ├─ Simulation/HuntingSystem.cs            # Animal AI・攻撃判定・Carcass・解体
│  ├─ Simulation/CleaningSystem.cs           # Waste→Disposal
│  ├─ Combat/DamageService.cs                # Damage Matrix（権威）
│  ├─ Combat/TargetFilter.cs                 # 二重防御の1段目
│  ├─ Handlers/InteractionCommandHandler.cs  # InteractCommand ルーティング
│  ├─ Handlers/PrimaryActionCommandHandler.cs# PrimaryActionCommand（狩猟攻撃）
│  ├─ Inventory/InventoryService.cs          # M2 拡張（Reserve/Consume/Grant）
│  ├─ Persistence/OutboxAgent.cs             # M2 拡張（ULID/local_sequence 採番・AppendEvents）
│  └─ Persistence/UlidFactory.cs             # ULID 生成（下記注記）
├─ Client/            (asmdef: Survival.Client)
│  ├─ Interaction/InteractionScanner.cs      # 候補表示（最終判定は Server, 5.3）
│  ├─ UI/HungerHealthView.cs                 # R3 購読の HUD
│  ├─ UI/StationJobView.cs                   # Job 進捗・Cancel
│  ├─ UI/InventoryViewModel.cs               # M2 拡張（reserved 表示）
│  └─ UI/ActionFeedbackPresenter.cs          # 採掘/料理/清掃の成否フィードバック
└─ Tests/
   ├─ EditMode/  (Survival.Server 参照)      # Damage Matrix / Recipe / DropTable / Hunger 閾値
   └─ PlayMode/                              # 採掘/Station/狩猟/料理の一連
```

- **ULID 生成**: C# 実装は外部ライブラリ `Cysharp/Ulid`（UPM/NuGet）を推奨。導入手順は M0 03A 第4章のライブラリ導入方式（NuGetForUnity）に準ずる。導入できない場合は `UlidFactory` に RFC ドラフト準拠の 128bit（48bit time + 80bit random, Crockford Base32）実装を置く。**同一 aggregate 内で単調増加する `local_sequence` は ULID とは別に DS がメモリ保持するカウンタで採番**する（下記 3.8）。
- Server ロジックは毎 Tick 走る。R3/VContainer はイベント境界に限定し、ホットループはプレーン C#（5.5.3）。

### 3.1 Hunger / Health（8.1・CharacterSimulation 拡張）

`SurvivalTuning.cs` に暫定定数を集約（すべて **暫定**・Config 化可能に）:

| 定数 | 値 | 根拠 |
|---|---|---|
| `HungerInitial` | 100 | 8.1 |
| `HungerDecayPerSeconds` | 1 / 60s | 8.1 |
| `HungerWarning` | 30 未満 | 8.1 |
| `HungerCritical` | 10 未満 | 8.1 |
| `StarvationHealthDrain` | Hunger==0 の間 1 HP / 5s（暫定） | 8.1「Starvation：0 の間 Health を定期減少」 |
| `CookedMeatHungerGain` | +30 | 8.1 / 7.2 |
| `LuxuryFoodHungerGain` | +30（優遇なし） | 8.1 / 7.2 |

- Hunger の減少判定は **Server UTC / World Clock**（`WorldRuntime` の Tick 時刻）で行い、**Client 時刻を使わない**（8.2 と同原則）。
- Player と AI の両方が同じ生存状態を持つ（6.4：Animal→AI 許可の前提）。Hunger/Health は `actor_runtime_states` に含めて Snapshot/Save で永続（権威は DS, 永続 Writer は API・付録C）。
- `character.vitals_changed` は閾値越え・Consume 回復時のみ Outbox（0.3 注記）。毎 Tick は Replication のみで Client に反映（R3 で HUD 更新, 3.9）。

### 3.2 採掘 ResourceNode（8.3・ResourceNodeSystem）

`ResourceNode` ランタイム構造（Server 権威, `Server owned` 6.3）:

```csharp
struct ResourceNode {
  string node_id; string resource_type; string region_id; Vector3 position;
  int remaining_amount; int maximum_amount; int quality; int hardness;
  string[] required_tool_tags; long version; RegenerationPolicy regeneration_policy;
  string event_instance_id; // レア素材放出イベント紐付け時のみ（M5 で活用、M3 では空可）
}
```

採掘フロー（8.3 の 1〜4 を厳密実装）:

1. **Interact 開始時**（`InteractCommand{interaction_type="mine"}`）に **距離・Line of Sight・Tool Tag・Node Version** を検証。`expected_version != node.version` は Conflict として拒否し、Client に再同期を促す。Tool Tag は装備中アイテムの Tag（`tool.mining` 等）が `required_tool_tags` を満たすか。
2. **所要時間完了後に再検証**（採掘中に Node が枯渇/他者が採掘した可能性）。`hardness` と Tool Quality から所要時間・採掘量を決定（暫定: base 採掘量 = 1〜3、所要 = `hardness` 秒程度）。remaining から採掘量を減算。
3. **Inventory 容量（slot/重量）を確認**し、入らない場合は **付与せず Node も減算しない**（AT-004）。容量チェックは `InventoryService` に委譲。
4. `resource.mined` Event を Outbox 生成（`grants[]`, `remaining_amount`, `node_version` を含む）。remaining==0 なら続けて `resource.node_depleted`。
5. **再生成**: `regeneration_policy`（例: `{type:"linear", amount_per_min:N, cooldown_sec:M}`）に従い WorldRuntime Tick で remaining を回復し `resource.node_regenerated` を発行。再生成も Server Clock 判定。

- 対象 resource_type と ItemDefinition の対応・要求ツールはマスタデータ（`ResourceNodeDef`, B が供給）から引く。stone node→`stone`、iron node→`iron_ore`、rare node→`rare_ore`（レア放出イベント時, M5）。
- ドロップ物は基本 stack 物（`item_instance_id` 不要）。

### 3.3 生産ステーション・製作・Development（8.2 農園以外は 8.4・StationJobSystem）

ステーション種別（`Station owned`・World_MVP に配置, 5.1 / 15章 Production Kit: forge/anvil/cooking station/farm plot）。各ステーションは 1 Job を直列実行。

**Recipe マスタ（8.4 の実値・B が seed し Bootstrap 配信。DS は権威値として使用）:**

| recipe_id | 種別 | 材料 | 出力 | 時間 | 前提 |
|---|---|---|---|---|---|
| `stone_pickaxe` | 既知製作 | stone x5 + wood x2 | stone_pickaxe x1 | 30s | なし |
| `stone_spear` | 既知製作（初期狩猟武器） | stone x3 + wood x2（**leather 不要**） | stone_spear x1 | 20s | なし |
| `iron_hunting_spear` | 開発後製作 | iron_ingot x3 + wood x1 + **leather x1** | iron_hunting_spear x1 | 60s | Blueprint: iron_spear |
| `iron_spear_research` | Development | iron_ore x5 + rare_ore x1 | （Blueprint 解放） | 120s | なし |
| `rare_weapon_craft` | 開発後製作 | rare_ingot x3 + iron_ingot x5 | rare_weapon x1 | 90s | Blueprint: rare_weapon（任意） |

> 精錬（iron_ore→iron_ingot 等）は 8.4 表に明示のレシピが無いが、iron_hunting_spear が iron_ingot を要するため、**精錬レシピ（forge）**を最小で追加してよい（暫定: `iron_ingot`= iron_ore x2 / 40s、`rare_ingot`= rare_ore x2 / 60s）。値は暫定として `SurvivalTuning`/マスタに置き、B の seed と一致させる（**A/B で必ず同値**にする。齟齬時は 06B を正とする）。

Job ライフサイクル:

1. `InteractCommand{interaction_type="station_craft", 付随データで recipe_id}` 受信 → 距離/対象/ツール・ステーション種別を検証。
2. **材料を reserved に確保**（`InventoryService.Reserve`, 7.1 予約）。`station.job_started`（`reserved[]`）を Outbox。不足時は失敗を Client へ。
3. 所要時間は **Server Clock** でカウント（UniTask + CancellationToken で待機, 5.5.2）。
4. 完了時に **予約分を消費**し出力を付与（`InventoryService.Consume`+`Grant`）。武器など個体は `item_instance_id`(ULID) を採番。`station.job_completed`（`consumed[]`/`produced[]`）を Outbox。
5. **Development(`iron_spear_research`)** は出力アイテム無しで **Blueprint を解放**：`development.blueprint_unlocked{blueprint_id:"iron_spear"}` を Outbox。Blueprint は **World 共通解放を推奨**（8.4）。解放状態は `world_blueprints`（B, 06B）と Snapshot に反映。
6. **Cancel**（`InteractCommand{interaction_type="station_cancel"}`）で **予約解除**（消費しない）、`station.job_cancelled`（`released[]`）を Outbox。
7. Inventory に空きが無く出力が入らない場合は **Station Output / WorldItem として残し消失させない**（8.6-3 と同原則）。

### 3.4 農園（8.2・FarmPlotSystem）

- 作物 **potato**、1 種の FarmPlot、状態機械 **Plant→Growing→Ready→Harvested**。
- 成長は **Server UTC / World Clock** で判定（Client 時刻不使用）。成長時間は Config 化（暫定: 120s）。水分/肥料/季節/枯死は省略（8.2）。
- `InteractCommand{interaction_type="farm_plant"/"farm_harvest"}`。Plant で `farm.crop_planted{ready_at_unix_ms}`、Ready 後 Harvest で `farm.crop_harvested{produced:[{potato, N}]}` を Outbox。
- AI Action 用に PlantCrop/HarvestCrop の口を用意するが、主検証ループは採掘・狩猟を優先（8.2, M4 で AI 接続）。

### 3.5 狩猟・解体（8.5・HuntingSystem, `Animal` Server owned 6.3）

**攻撃（PrimaryActionCommand, 左クリック）:**

- `PrimaryActionCommandHandler` が **装備・Cooldown・距離・方向・Hitbox を Server 検証**。**Client 送信の Damage 値は使用しない**（8.5 / 17章）。判定結果は `DamageService` 経由（3.6）。
- 対象は Animal のみ（Player→Animal / AI→Animal 許可, 6.4）。Animal→Player/AI も許可（Animal AI の攻撃 Template）。

**ドロップ表（8.5・EditMode でテスト）:**

| 種 | 分類 | Drop |
|---|---|---|
| Deer | 通常獣 | raw_meat, leather, bone |
| Rare Deer | レア変種 | raw_meat, rare_meat, leather, rare_material（=rare_ore 等の rare 素材） |
| Event Beast | イベント | Template 準拠。MVP では Rare Deer 増加で代替可 |

- 死亡時 `hunting.animal_killed{carcass_id}` を Outbox し **Carcass Entity を生成**。
- **解体**は `InteractCommand{interaction_type="butcher"}`。**Drop は解体完了時に Server が Seed 付きで確定**（`hunting.carcass_butchered{drops[], drop_seed}`）。Carcass は **一度だけ消費**（二重解体不可, AT-008）。
- **Rare Meat 確率**は Animal Variant・Event Modifier・解体 Tool Quality を入力にし、**Client へ Seed を公開しない**（8.5）。Seed は Server 生成し payload に記録（再現・監査用）。

### 3.6 Damage Matrix（6.4・DamageService + TargetFilter, 二重防御）

**必ず二重防御**で実装（6.4「Target Filter と DamageService の二重防御」）:

| 攻撃元 | 対象 | MVP | 実装 |
|---|---|---|---|
| Player | Animal | 許可 | 装備/Cooldown/距離/Hit を Server 検証 |
| AI | Animal | 許可 | Action Template 経由のみ |
| Animal | Player | 許可 | 危険性を成立 |
| Animal | AI | 許可 | AI も同じ生存状態 |
| Player | AI | **拒否** | Target Filter 除外 + DamageService 拒否 |
| AI | Player | **拒否** | AI Template 候補にも含めない |
| Player | Player | **拒否** | PvP 対象外 |
| AI | AI | **拒否** | 直接攻撃対象外 |

- `TargetFilter`（1 段目）: `PrimaryActionCommand`/攻撃判定の候補集合から拒否対象を除外（レイキャスト/近傍列挙の段階で Player/AI を対象にしない）。
- `DamageService`（2 段目）: 実ダメージ適用直前に (attackerType, targetType) を Matrix 照合し拒否ペアは **Damage 0 + Server 警告ログ**（AT-006）。強制 Command でも 2 段目で必ず遮断（AT-007）。
- Matrix は `DamageService` 内の純データ表（EditMode で全 8 ペアを網羅テスト, 18.1 Unity EditMode）。

### 3.7 料理・ゴミ・清掃（8.6・CleaningSystem / StationJobSystem[cooking]）

1. **Cooking Station** で raw_meat を 1 個 **予約**（`station.job_started`）。luxury_food は入力を変えず出力 waste 量が変わる。
2. 所要時間完了後 **cooked_meat x1 と food_waste x1** を生成。**luxury_food は food_waste x2**（7.2 / 8.6-2）。`cooking.completed{consumed, produced}` を Outbox。
3. **Inventory に空きが無い出力は Station Output / WorldItem として残す（消失させない）**（8.6-3）。
4. **Consume** 成功時 cooked_meat/luxury_food を減算し Hunger 回復（+30, 3.1）。`inventory.item_consumed{hunger_delta}` を Outbox。
5. **Discard** した Item は **WorldItem として残る**。waste Tag（`waste.food`）を持つ物は Clean 対象。`item.discarded{world_item_id, tags}` を Outbox（WorldItem は永続対象・B が `world_items` に確定 → 再接続後も残る AT-010）。
6. **Clean**（`InteractCommand{interaction_type="clean"}`）は対象を **Disposal へ移動/削除**し、`cleaning.completed{reward_amount}` を Outbox（簡易報酬。currency_ledger 反映は B。M3 の報酬額は暫定小額でよい）。

### 3.8 Domain Event 生成・Outbox・AppendEvents（DS 採番・OutboxAgent 拡張）

- 各 System は成立変更を `DomainEvent` に変換して `OutboxAgent.Enqueue` する。`OutboxAgent` が M2 の Outbox 基盤を拡張して以下を担う:
  - **event_id = ULID を DS 生成**（`UlidFactory`）。128bit・辞書順=時刻順。
  - **local_sequence = aggregate_id ごとの単調増加カウンタ**を DS メモリで採番（`Dictionary<aggregateId, long>`）。Snapshot に含めて再起動後も継続（採番リセットで sequence 逆行しないこと）。
  - `world_id` / `type` / `payload`(JSON, 0.3 の DTO を `System.Text.Json` 等で直列化) / `occurred_at_unix_ms` を設定。
  - 最大 1 秒程度で Flush（12.1-4）。`WorldData.AppendEvents(server_id, events[])` を gRPC 送信（生成 C# stub `WorldDataService.WorldDataServiceClient`）。
- **結果処理**（`AppendEventsResponse.results[]`）:
  - `OK`: Outbox から確定除去。
  - `DUPLICATE`: 既に永続済み（再送/クラッシュ後の再試行）。**成功扱いで除去**（冪等・AT-003）。
  - `CONFLICT`: sequence/version 競合。**当該 aggregate の Runtime を Snapshot 再取得で再同期**し、以降を再構築（DS 権威を壊さない）。
- **単一 Writer 原則の厳守**（付録C / 12.2.1）: DS は `inventory_entries`/`currency_ledger` を **自分で永続書き込みしない**。DS は Runtime を更新し、**変更を Domain Event として送るだけ**。永続確定は API（B）。二重永続を作らない。

### 3.9 Client UI・入力・フィードバック（5.2 / 5.3 / 5.5）

- **入力**（5.2）: Interact=E（採掘/Pickup/Station/Buyer/清掃）、PrimaryAction=左クリック（狩猟攻撃）、Inventory=Tab/I。`ThirdPersonInputReader` が Action → `InteractCommand`/`PrimaryActionCommand` へ変換（M1 の InputCommand 経路に追加）。
- `InteractionScanner`（5.3）: 画面中心/近傍の Interactable 候補を表示。**最終判定は Server**（Client は候補提示のみ）。
- `HungerHealthView`: 複製された Hunger/Health を **R3 の Observable/ReactiveProperty** で購読し HUD 更新（5.5.2）。`AddTo(destroyCancellationToken)` で購読破棄。
- `StationJobView`: Job 進捗（残時間は Server 複製値を表示）と Cancel ボタン。待機は UniTask。
- `InventoryViewModel`（M2 拡張）: reserved（予約中数量）を表示に反映。
- `ActionFeedbackPresenter`: 採掘失敗（満杯 AT-004）・料理完了・清掃完了などのフィードバック。**Client 側で結果を確定表示しない**（Server 反映を待つ）。
- **重要データを Client Cache に保存しない**（5.4 / 19.1）: Inventory・所持金・World Save 等はメモリ複製のみ。

---

## 4. 実装順序（Windows側）

| # | タスク | 完了確認 |
|---|---|---|
| W-1 | asmdef 分割（Shared/Server/Client/Tests）と `SurvivalTuning`/`DomainEventTypes` 骨組み（3.0） | コンパイル通過 |
| W-2 | `MasterDataStore`：Bootstrap payload から ItemDefinition/Recipe/ResourceNodeDef を読み込みキャッシュ（0.4-2） | 起動時ログにマスタ件数 |
| W-3 | `OutboxAgent` 拡張：ULID/local_sequence 採番、`AppendEvents` 送信、OK/DUPLICATE/CONFLICT 処理（3.8） | ダミー Event 送信で OK 受領 |
| W-4 | `CharacterSimulation` に Hunger/Health Tick（3.1）＋`character.vitals_changed` 閾値発行 | Hunger 減少・Starvation で Health 減 |
| W-5 | `ResourceNodeSystem`：距離/LoS/Tool/Version 検証→採掘→容量チェック→`resource.mined`/枯渇/再生成（3.2） | 採掘で Item 付与・満杯で不付与 |
| W-6 | `InventoryService` に Reserve/Consume/Grant（M2 拡張, 3.3-2/4） | 予約→消費が version+1 |
| W-7 | `StationJobSystem`＋Recipe：stone_pickaxe/stone_spear/精錬/iron_hunting_spear/rare_weapon_craft/Development（3.3） | 製作完了・Blueprint 解放・Cancel で予約解除 |
| W-8 | `FarmPlotSystem`：potato Plant→Ready→Harvest（3.4） | 成長後に収穫 Event |
| W-9 | `DamageService`+`TargetFilter`：Damage Matrix 二重防御（3.6） | 拒否ペアで Damage 0 |
| W-10 | `PrimaryActionCommandHandler`+`HuntingSystem`：攻撃検証→Deer/Rare Deer→Carcass→解体 Seed ドロップ（3.5） | 狩猟→解体で素材付与、一度だけ消費 |
| W-11 | Cooking/Consume/Discard/Clean（3.7・CleaningSystem） | cooked_meat+waste 生成、Clean で消失/Disposal |
| W-12 | `InteractionCommandHandler`：全 interaction_type ルーティング（mine/station_craft/station_cancel/farm_*/butcher/clean） | 各 Command が正しい System へ |
| W-13 | Client UI/入力（3.9）：InteractionScanner/HungerHealthView/StationJobView/InventoryViewModel/ActionFeedbackPresenter | HUD/進捗/フィードバック表示 |
| W-14 | EditMode/PlayMode テスト（第5章）＋`scripts\unity_test.ps1` | テスト緑 |
| W-15 | `scripts\unity_build_server.ps1` で Linux DS ビルド→Headless 起動確認 | Readiness/Graceful Shutdown |

> デッドロック回避の縦切り（AT-022）は W-7（stone_spear）→W-10（Deer 解体で leather）→W-7（iron_hunting_spear 到達）の順で一気通貫を確認する。

---

## 5. テスト・受入（Windows側）

### 5.1 EditMode（18.1: Definitions/Damage Matrix/Recipe/Drop Table）

- **Damage Matrix**: 8 ペア全網羅。許可4/拒否4。強制入力でも DamageService が拒否ペアを Damage 0（AT-006, AT-007）。
- **Recipe**: 各 recipe の材料/出力/時間/前提（Blueprint）を検証。循環依存が起きない（stone_spear が leather 不要, AT-022 の静的確認）。
- **Drop Table**: Deer=raw_meat/leather/bone、Rare Deer=+rare_meat/rare_material。Seed 固定で決定的。
- **Hunger 閾値**: 100→減少、Warning30/Critical10/Starvation0 の遷移、Cooked/Luxury で +30。
- **Event payload**: 0.3 の各 type の JSON 形状（B が期待するスキーマと一致）。

### 5.2 PlayMode（18.1: Interaction/Station jobs）

- **採掘**（AT-003/AT-004）: 二重送信（同一 command_id/event_id）で 1 回分のみ反映。満杯 Inventory で Node 非減算・Item 非生成。
- **Station Job**（AT-005）: 材料予約→完了→Blueprint 解放→Weapon 生成、Cancel で予約解除。
- **狩猟・解体**（AT-008）: Server 確定で Meat/素材が Inventory へ、Carcass 一度だけ消費。
- **料理・食事**（AT-009）: raw_meat 減算、cooked_meat/waste 生成、Hunger 回復。
- **Drop/清掃**（AT-010）: WorldItem が（再接続後も）残り、Clean 後に消失/Disposal 記録（永続確認は B と結合）。
- **デッドロック回避**（AT-022）: leather/iron 非所持から stone_spear→Deer 狩猟→leather→iron_hunting_spear 到達を人手なしで（W-7/W-10 の縦切り）。

### 5.3 結合（B と）

- `AppendEvents` に対する OK/DUPLICATE/CONFLICT の実挙動（B の api・DB と結合）。二重送信の冪等（AT-003）と Server 再起動後の復元（AT-018）を B の Integration と合わせて確認。
- DS ビルドを Headless 起動し `LoadBootstrap`→マスタ受領→行動→`AppendEvents`→（B が永続）を E2E（Network E2E, 18.1）。

---

## 6. 落とし穴（Windows側）

- **二重永続の作り込み禁止**（12.2.1 / 付録C）: DS が `inventory_entries`/`currency_ledger` を自前で書かない。Runtime 更新＋Domain Event 送出のみ。永続は API。
- **local_sequence の逆行**: aggregate ごとのカウンタを Snapshot に含めず再起動でリセットすると sequence 競合/CONFLICT を招く。Snapshot に含め継続採番する（3.8）。
- **event_id の非冪等**: 再送のたびに ULID を振り直すと DUPLICATE 排除が効かず二重反映（AT-003 失敗）。**同一論理イベントは同一 event_id を保持**し再送する（Outbox に確定まで残す）。
- **Client 時刻依存**（8.2/3.1/3.4）: Hunger 減少・作物成長・再生成・Job 時間は必ず Server/World Clock。Client 送信時刻を使わない。
- **満杯時の Node 減算**（AT-004）: 容量チェックを採掘量減算より前に。入らないなら Node も減らさず Event も出さない（3.2-3, 0.3 注記）。
- **Damage の片側防御**: Target Filter だけ/DamageService だけでは強制 Command を防げない。必ず二重防御（6.4 / AT-007）。
- **Client 送信 Damage の信用**: PrimaryActionCommand の Damage/命中を信用しない。装備・Cooldown・距離・Hitbox を Server 再計算（8.5 / 17章）。
- **Rare Seed 漏洩**: 解体 Seed を Client へ複製しない（8.5）。payload には記録してよいが Client Replication に載せない。
- **Recipe/精錬値の A/B 齟齬**: マスタ数量・時間は B の seed（migration 0003）と**必ず同値**。齟齬は製作不能/予約不整合の原因。齟齬時は 06B を正とする（0.3 / 3.3 注）。
- **R3 購読リーク / ホットループのアロケーション**（5.5.3）: 毎 Tick 処理はプレーン C#。購読は `AddTo` で破棄。
- **`Assets/Generated/` を Windows で編集**しない（0.2）。proto 変更は 06B へ。
- **`-runTests` に `-quit` を付けない**、ビルドの `-executeMethod` には付ける（03A 5章の再掲）。

---

## 参考資料

[R-MVP] docs/02_MVP詳細設計書_v0.2.2.md（6.4 Damage Matrix, 7.2 Item Definition, 8.1〜8.6 生産ループ, 12.2.1 単一Writer, 13.1 採番責任, 14.1 Commands, 18 テスト, 19 DoD, 付録C）
[R-BSD] docs/01_基本設計書_v0.2.1.md（5.3 / 6.2 / 6.3 非干渉, 9.4 Writer 原則）
[R-06B] docs/prompts/06B_M3実装指示書_WSL2側_v0.1.md（AppendEvents 永続・NATS・マスタ供給・migration 0003）
[R-03A] docs/prompts/story_0000/win/unity/03A_M0実装指示書_Windows側_v0.1.md（Unity/ビルド/テスト基盤）
[R-ULID] [Cysharp/Ulid](https://github.com/Cysharp/Ulid)
[R-FN] [FishNet docs](https://fish-networking.gitbook.io/docs)
[R-R3] [Cysharp/R3](https://github.com/Cysharp/R3)
[R-UT] [Cysharp/UniTask](https://github.com/Cysharp/UniTask)
