---
title: "M8A 実装指示書（Windows ネイティブ側）"
subtitle: "Playtest Interactions：入力・照準/近接検出・Network Command・対象登録・UI フィードバック・プレイテスト導線"
document_id: "IMPL-M8A-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-18"
status: "実装指示（M8A / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / Input System / R3 / VContainer / UniTask"
related_document: "10A_M7実装指示書_Windows側_v0.1.md, 10B_M7実装指示書_WSL2側_v0.1.md, 09A_M6実装指示書_Windows側_v0.1.md, 06A_M3実装指示書_Windows側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M8A Playtest Interactions 実装指示書：Windows ネイティブ側 v0.1

本書は、M3〜M7 で実装済みのサーバー側ゲームロジックとクライアント基盤を、**プレイテストで検証可能な一連の操作**として接続するための Windows/Unity 側実装指示である。

ここでの完了条件は「単体テストでロジックが通る」ではなく、実 Client から移動し、対象を見て、Interact/Attack/UI 操作を行い、Dedicated Server が権威判定した結果を画面とログで確認できる状態である。

---

## 0. 現状判定

### 0.1 実装済みと見なせるもの

- Player 移動入力と FishNet 経由の Server 権威移動は `NetworkPlayerController` / `ThirdPersonInputReader` で実装済み。
- M3 のサーバー側 Simulation は存在する。`InteractionCommandHandler` は `mine`, `station_craft:*`, `station_cancel`, `farm_plant`, `farm_harvest`, `clean`, `butcher` をルーティングできる。
- `PrimaryActionCommandHandler` と `HuntingSystem` は狩猟攻撃のサーバー側入口を持つ。
- Inventory は `NetworkInventoryCommandBridge` 経由で ADD/MOVE/DROP/USE 系 command を DS に送れる。
- Buyer 購入は `NetworkBuyerPurchaseCommandBridge` と `BuyerPurchaseHandler` が存在し、DS 側検証と API 購入プロトコルに接続できる。
- `InteractionScanner` は画面中心 Raycast で候補検出できる。
- M7 の Import Processor により `interaction_points` 付き Prefab 生成の土台はある。

### 0.2 未完了と見なすもの

次の接続が未完了のため、現時点では「各種インタラクションをプレイテストで検証可能」とは言えない。

- `Interact` / `Attack` InputAction が、実プレイ中の `InteractCommand` / `PrimaryActionCommand` 送信へ接続されていない。
- `InteractionCommandHandler` / `PrimaryActionCommandHandler` が `ServerBootstrap` の実接続経路に組み込まれていない。
- World_MVP に、採掘ノード、Station、FarmPlot、Animal/Carcass、Waste/Clean 対象、Buyer などのプレイテスト対象が一貫して登録されていない。
- Client 側に Interact 候補表示、実行中/成功/失敗フィードバック、最小 Inventory/Buyer/Station UI がそろっていない。
- 手動プレイテスト用のシナリオ、初期アイテム投入、受入チェックリスト、自動 smoke が未整備。

---

## 1. 対象とゴール

### 1.1 対象

Windows 側は `unity/` と必要な `scripts/*.ps1` のみを編集する。`proto/`, `services/`, `infra/`, `scripts/*.sh`, `Makefile` は触らない。`Assets/Generated/` の proto C# 生成物も編集しない。

主対象:

```text
unity/SurvivalWorld/Assets/Scripts/
├─ Player/
│  ├─ ThirdPersonInputReader.cs
│  ├─ NetworkPlayerController.cs
│  ├─ NetworkInteractionCommandBridge.cs      # 新規
│  └─ NetworkPrimaryActionCommandBridge.cs    # 新規
├─ Client/
│  ├─ Interaction/
│  │  ├─ InteractionScanner.cs                # 拡張
│  │  ├─ InteractableTargetView.cs            # 新規
│  │  └─ PlaytestInteractionController.cs     # 新規
│  └─ UI/
│     ├─ InteractionPromptView.cs             # 新規
│     ├─ InventoryPanelView.cs                # 新規または既存 VM の View 化
│     ├─ BuyerStockView.cs                    # 新規
│     └─ ActionFeedbackPresenter.cs           # 拡張
├─ Server/
│  ├─ ServerBootstrap.cs                      # Interaction/PrimaryAction 入口を追加
│  └─ Handlers/
│     ├─ InteractionCommandHandler.cs         # 必要に応じて Result 情報を拡張
│     └─ PrimaryActionCommandHandler.cs       # 対象解決と Result を拡張
└─ Tests/
   ├─ EditMode/
   └─ PlayMode/
```

Scene / Prefab:

```text
unity/SurvivalWorld/Assets/Scenes/World_MVP.unity
unity/SurvivalWorld/Assets/Prefabs/PlayerCharacter.prefab
unity/SurvivalWorld/Assets/Prefabs/Playtest/
```

### 1.2 M8A DoD

- 実 Client で `E` または Gamepad Interact を押すと、照準/近接候補から DS へ `InteractCommand` が送信され、DS が対象・距離・Version・ツール条件を検証して結果を返す。
- 左クリックまたは Gamepad Attack で `PrimaryActionCommand` が送信され、DS が Animal のみを対象に攻撃判定する。Player/AI への直接攻撃は拒否される。
- 採掘、Station 製作/Cancel、Farm Plant/Harvest、狩猟、Carcass 解体、料理/食事、Drop/清掃、Buyer 購入のうち MVP プレイテストに必要な最小縦切りが 1 シーンで検証できる。
- Client は Damage/Loot/Drop/Craft/Purchase Price を確定しない。Client は入力と候補表示だけを行い、結果は TargetRpc/ObserversRpc/Snapshot で受ける。
- World_MVP に `PlaytestArena` を用意し、DevLocalMode で 5〜10 分の手動検証が可能。
- `scripts\unity_test.ps1` が緑。少なくとも EditMode で command 構築/権威拒否/対象登録、PlayMode で Player prefab と基本 UI/Bridge の存在を検証する。
- Windows Client と Linux Dedicated Server がビルドできる。

---

## 2. 実装方針

### 2.1 入力の拡張

`ThirdPersonInputReader` は現在 Move/Look/Jump/Sprint のみを読む。M8A では既存の InputActionAsset にある `Attack` / `Interact` / `Previous` / `Next` を解決し、押下イベントを公開する。

追加する公開 API の例:

```csharp
public Observable<Unit> InteractPressed => interactPressed;
public Observable<Unit> PrimaryActionPressed => primaryActionPressed;
public Observable<int> HotbarSelectionChanged => hotbarSelectionChanged;
```

実装上の注意:

- StarterAssets 側の `PlayerInput.defaultActionMap` が `Player` であることを前提にし、既存の action map 名不一致を吸収する。
- Interact は Hold 設定でも、プレイテストでは `performed` を 1 回の確定入力として扱う。長押し進捗が必要なら UI 表示だけ追加し、確定は performed に寄せる。
- 入力イベントは owner client のみ発火する。非 owner は送信しない。

### 2.2 Client 候補検出

`InteractionScanner` を候補情報の取得に拡張する。Raycast 結果から `InteractableTargetView` を探し、表示用ラベル、既定 `interaction_type`, `expected_version`, `NetworkObject.ObjectId` を取れるようにする。

`InteractableTargetView` の責務:

- `InteractionKind` を保持する。例: `Mine`, `StationCraft`, `StationCancel`, `FarmPlant`, `FarmHarvest`, `Butcher`, `Clean`, `Buyer`, `Pickup`
- Client 表示用 `DisplayName` と `PromptText` を持つ。
- Server に送る `interaction_type` を返す。Station craft は暫定で `station_craft:stone_spear` のように recipe_id を suffix に入れる。
- `expected_version` は Client 表示上の直近値を入れる。ただし DS 側が必ず再検証する。

Client は候補を提示するだけで、距離・Line of Sight・ツール・残量・Stock は DS 側で再検証する。

### 2.3 Network Bridge

`NetworkInteractionCommandBridge` を PlayerCharacter root に追加する。

責務:

- owner client から `InteractCommand` を `ServerRpc` で送信する。
- command_id は `clientId + localSequence + unixMs` などで生成し、再送が必要な場合は同一論理操作で保持する。
- ServerRpc 内で `ServerBootstrap.TryApplyInteractCommand(Owner, command, out result)` を呼ぶ。
- 結果を `TargetRpc` で owner client に返し、`ActionFeedbackPresenter` へ出す。

`NetworkPrimaryActionCommandBridge` を PlayerCharacter root に追加する。

責務:

- owner client の camera/aim から `PrimaryActionCommand` を作る。
- `equipment_slot` はまず 0/1 の hotbar 選択だけでよい。
- ServerRpc では Client 送信の命中結果や Damage を信用しない。DS 側で actor position / aim direction / target filter を使って対象解決する。

### 2.4 ServerBootstrap への接続

`ServerBootstrap` に以下を追加する。

```csharp
public bool TryApplyInteractCommand(NetworkConnection connection, InteractCommand command, out M3CommandResult result)
public bool TryApplyPrimaryActionCommand(NetworkConnection connection, PrimaryActionCommand command, out HuntingAttackResult result)
```

実装要件:

- 既存の `CanAcceptInventoryCommand` / `CanAcceptBuyerPurchaseCommand` と同じ認証チェックを使う。
- player の actor id は `InventoryOwnerId(connection)` と同じ規則で一貫させる。
- actor position は `spawnedPlayers[connection.ClientId].transform.position` から取る。
- actor inventory は `inventoryRuntimeService` から owner snapshot/state を解決する。M3 の `InventoryOwner` と M2/M6 の runtime inventory が分裂している場合は、M8A で adapter を作り、プレイテスト対象の item 付与/消費が同じ inventory version に載るよう統一する。
- `InteractionCommandHandler` の target registry を Scene 起動時に構築する。
- `PrimaryActionCommandHandler` は server-side raycast / sphere cast で Animal target を解決する。Client から target id を受け取らない。

### 2.5 Interaction Target 登録

World_MVP に server-owned のプレイテスト対象を置き、`NetworkObject.ObjectId` と M3 state を対応付ける。

必要な最小 target:

| 対象 | interaction_type | 受入 |
|---|---|---|
| Stone Resource Node | `mine` | 採掘で stone が増え、満杯なら Node 非減算 |
| Iron Resource Node | `mine` | tool tag 条件を満たすと iron_ore が増える |
| Anvil/Workbench | `station_craft:stone_spear` / `station_cancel` | 材料予約、完了、Cancel |
| Cooking Station | `station_craft:cook_raw_meat` | raw_meat から cooked_meat + food_waste |
| Farm Plot | `farm_plant` / `farm_harvest` | Server clock で Ready 後に harvest |
| Carcass | `butcher` | 一度だけ raw_meat/leather/bone を付与 |
| Waste WorldItem | `clean` | clean 完了で対象消失/報酬 event |
| Buyer Stall | Buyer UI open | stock 選択から `BuyerPurchaseCommand` |

M8A では全 target を本格アセットにする必要はない。Primitive + `GeneratedAssetMetadata` + Collider + `InteractableTargetView` でよい。ただし、プレイテスターが何を触っているか分かる見た目と prompt は必須。

### 2.6 UI とフィードバック

最低限の UI:

- 画面中央付近の Interact prompt: `E Mine Stone`, `E Craft Stone Spear`, `E Butcher`, `E Open Buyer`
- Action feedback: success/rejected/pending と理由。例: `Inventory full`, `Too far`, `Need tool.mining`, `Purchased cooked_meat`
- Hunger/Health: 既存 `HungerHealthView` を Scene に配置し、Server 反映値を表示する。
- Inventory panel: `I` または `Tab` で開閉。slot, item_definition_id, quantity, reserved を表示する。
- Buyer stock panel: Buyer に Interact した時だけ開き、stock_entry_id / item / price / remaining を表示。購入ボタンは `NetworkBuyerPurchaseCommandBridge.SubmitPurchase` へ接続する。
- Station progress: craft 開始後、残り時間と Cancel を表示する。

UI は Dev/Playtest 用でよいが、結果確定は Server 応答を待つ。Client 側で先行して item 追加・stock 減算・damage 表示を確定しない。

### 2.7 PlaytestScenarioSeeder

DevLocalMode 限定で `PlaytestScenarioSeeder` を追加する。

責務:

- World_MVP に `PlaytestArena` root を作り、対象を決定的な位置に配置する。
- DevLocalMode かつ Editor/Development Build の時だけ、初期検証用アイテムを付与する。Production/Release では無効。
- 初期値例:
  - `stone_pickaxe` x1
  - `stone` x5
  - `wood` x3
  - `raw_meat` x1
  - 所持金/Buyer 購入に必要な通貨は API 側 seed がある場合のみ使用。Client/DS が通貨を偽装しない。

Seeder はテスト短縮用であり、通常ゲーム進行の正規ループを置き換えない。

---

## 3. プレイテスト縦切り

### 3.1 5〜10 分の手動検証ルート

1. Client 起動、DevLocalMode で DS に Join する。
2. WASD/マウスで `PlaytestArena` を移動する。
3. Stone node に照準を合わせ、Prompt が `Mine` になることを確認し、`E` で採掘する。
4. Inventory panel で stone が増えたことを確認する。
5. Anvil/Workbench に照準を合わせ、`stone_spear` を craft する。材料 reserved と progress 表示を確認する。
6. Deer を左クリックで攻撃する。DS が Animal のみを対象にし、死亡後 Carcass が出ることを確認する。
7. Carcass に `E` で butcher し、raw_meat/leather/bone が増えることを確認する。
8. Cooking Station で raw_meat を cook し、cooked_meat と food_waste を得る。
9. cooked_meat を Inventory UI から Use し、Hunger が回復することを確認する。
10. food_waste を Drop し、WorldItem として残ることを確認する。
11. waste に `E` で clean し、WorldItem が消えることを確認する。
12. Buyer stall に `E` で UI を開き、在庫を 1 件購入する。DS が API 結果後に Runtime Inventory を更新することを確認する。

### 3.2 2 Client 確認

- 2 Client で同じ World に入り、互いの移動が見えること。
- 非 owner の Player を操作できないこと。
- 同じ node / buyer stock に対して同時操作し、DS/API の version/conflict/out_of_stock が破綻しないこと。
- Player 同士や Player→AI の攻撃は Damage 0 / rejected になること。

---

## 4. 実装順序

| # | タスク | 完了確認 |
|---|---|---|
| W-1 | `ThirdPersonInputReader` に Interact/Attack/Hotbar input を追加 | EditMode で action 解決と押下イベントを検証 |
| W-2 | `InteractableTargetView` と `InteractionScanner` 拡張 | Raycast 候補から prompt と command 情報を取得 |
| W-3 | `NetworkInteractionCommandBridge` 追加、PlayerCharacter に付与 | owner client だけが Interact ServerRpc を送る |
| W-4 | `NetworkPrimaryActionCommandBridge` 追加、PlayerCharacter に付与 | left click で PrimaryAction ServerRpc |
| W-5 | `ServerBootstrap` に Interact/PrimaryAction の TryApply を追加 | 認証済み connection だけ処理される |
| W-6 | Scene target registry を構築 | NetworkObject id から M3 target state へ解決 |
| W-7 | World_MVP に `PlaytestArena` を配置 | 採掘/Station/Farm/Animal/Buyer/Clean 対象が見える |
| W-8 | Minimal UI: prompt, feedback, inventory, station, buyer | 手動検証で結果が画面に出る |
| W-9 | Inventory Use/Drop と Buyer Purchase UI を既存 bridge へ接続 | UI 操作が既存 ServerRpc を呼ぶ |
| W-10 | DevLocalMode 用 `PlaytestScenarioSeeder` | 5〜10 分の検証が初期準備なしで可能 |
| W-11 | EditMode/PlayMode テスト追加 | `scripts\unity_test.ps1` 緑 |
| W-12 | Client/Server ビルドと実 Client smoke | `unity_build_client.ps1` / `unity_build_server.ps1` 成功 |

---

## 5. テスト指示

### 5.1 EditMode

- `ThirdPersonInputReader` が `Player` action map から Move/Look/Jump/Sprint/Attack/Interact を解決できる。
- `InteractionScanner` が `InteractableTargetView` から `target_network_id`, `interaction_type`, `expected_version` を作る。
- `NetworkInteractionCommandBridge` は owner 以外で送信しない。
- `ServerBootstrap.TryApplyInteractCommand` は unauthenticated connection を拒否する。
- `InteractionCommandHandler` は unsupported type を rejected にし、各 type を正しい system へルーティングする。
- `PrimaryActionCommandHandler` は Player/AI target を拒否し、Animal のみ許可する。
- PlayerCharacter prefab に必要 bridge と UI 参照がある。

### 5.2 PlayMode

- World_MVP を開き、`PlaytestArena` の target が存在する。
- owner player が Interact すると feedback が出る。
- 採掘成功、満杯拒否、station start/cancel、carcass butcher 一回のみ、clean 完了を PlayMode で確認する。
- Buyer UI は stock がない場合も空表示で落ちず、購入失敗時は rejected feedback を出す。

### 5.3 Manual / E2E

- 実 DS + 実 Client で 3.1 のルートを実施する。
- backend が未起動の場合でも、DevLocalMode の非経済 interaction は検証できること。Buyer 購入は backend 起動時のみ合格対象にする。
- WSL2 側の `scripts/load_test.sh` / `scripts/recovery_test.sh` とは別に、Windows 側では画面操作と Unity log を evidence として保存する。

---

## 6. 受入基準

| 項目 | 合格条件 |
|---|---|
| 入力 | Interact/Attack/UI 操作が owner client のみから送信される |
| 候補表示 | 対象に照準を合わせると prompt が出て、離れると消える |
| DS 権威 | 距離外、version mismatch、tool 不足、stock 無し、unauthenticated は DS で rejected |
| 採掘 | 成功時のみ item 付与と node version 更新。満杯時は node 非減算 |
| 製作 | reserved 表示、完了付与、cancel 解除 |
| 狩猟 | Animal のみ攻撃可能。Carcass は一度だけ butcher |
| 料理/食事 | cooked_meat 生成、Use で Hunger 回復 |
| Drop/Clean | dropped waste が WorldItem として残り、clean で消える |
| Buyer | stock UI から purchase command が送られ、API 結果後に Runtime 反映 |
| UI | 成功/失敗理由がプレイテスターに見える |
| Test | EditMode/PlayMode 緑、Client/Server build 成功 |

---

## 7. 落とし穴

- `InteractionScanner` の Raycast 命中を Client 確定にしない。Client は target id と希望 type を送るだけ。
- `PrimaryActionCommand` に Damage 値を追加しない。Damage は DS が equipment/cooldown/range/hitbox から算出する。
- `NetworkObject.ObjectId` は spawn 後に有効になる。Scene 登録時に未 spawn id を固定値として使わない。
- Station craft の recipe 指定を Client 任せにしない。M8A の簡易 UI では候補 recipe を表示してよいが、DS が station type / blueprint / materials を再検証する。
- DevLocalMode の初期アイテム付与を Release に入れない。`UNITY_EDITOR || DEVELOPMENT_BUILD` と config flag の両方で gated にする。
- Buyer の価格、残高、購入成立を DS/Client で確定しない。API 応答が正。
- `Assets/Generated/` の proto C# を手で修正しない。

---

## 参考資料

- `docs/02_MVP詳細設計書_v0.2.2.md`: 5.2 入力、5.3 Client、6.3 Network Object、7 Inventory、8 Survival Loop、14.1 Gameplay Commands、18 受入、19 DoD。
- `docs/prompts/story_0003/win/unity/06A_M3実装指示書_Windows側_v0.1.md`: M3 サーバー側 Interaction/PrimaryAction/Simulation。
- `docs/prompts/story_0006/win/unity/09A_M6実装指示書_Windows側_v0.1.md`: BuyerPurchaseCommand と購入プロトコル。
- `docs/prompts/story_0007/win/unity/10A_M7実装指示書_Windows側_v0.1.md`: Import Processor、Interaction Point、RC/負荷/復帰。
- `proto/survival/v1/gameplay.proto`: `InteractCommand`, `PrimaryActionCommand`, `InventoryCommand`, `BuyerPurchaseCommand`, `RequestInventorySnapshot`。
