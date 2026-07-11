---
title: "M6 実装指示書（Windows ネイティブ側）"
subtitle: "Economy：DS の Buyer spawn/despawn・BuyerPurchaseCommand・購入プロトコル・AI購入行動・Buyer Kit配置"
document_id: "IMPL-M6-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M6 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / R3 / VContainer / UniTask / gRPC(C#)"
related_document: "09B_M6実装指示書_WSL2側_v0.1.md, 08A_M5実装指示書_Windows側_v0.1.md, 08B_M5実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M6 実装指示書：Windows ネイティブ側 v0.1

本書は M6（Economy）の作業を **Windows ネイティブ側**（Unity Dedicated Server 上の Buyer spawn/despawn、`BuyerPurchaseCommand` 処理、購入プロトコルの DS 反映、AI 購入行動、Buyer Kit の配置）に限定して指示する。バックエンド（`api` の EconomyService・在庫生成・ランキング・NATS・DB）は別冊 **09B（WSL2側）** を参照。

M6 の成果（MVP 第19章）: **Buyer、有限Stock、購入Transaction、AI購入、ランキング**。DS 側の中心は「**購入の確定は API が単一 Tx で行い、DS は結果を Runtime に映すだけ**」という単一 Writer プロトコル（MVP 12.2.1）を正しく実装することである。

---

## 0. 分担と連携（要点／詳細は 03A/03B 第0章）

環境別責務・リポジトリ配置・Git/LFS/改行コード規約・境界成果物の全文は **M0 の 03A/03B 第0章（両冊同一）** を正とする。M6 の要点のみ再掲する。

| 領域 | 担当 | M6 での具体 |
|---|---|---|
| Unity DS：Buyer spawn/despawn・購入コマンド・Runtime 反映・AI 購入 | **Windows** | 本書 |
| Buyer Kit（stall/sign/spawn marker）配置・Prefab | **Windows** | 本書 4章（Blender 生成物自体は WSL2） |
| proto C# 生成物の取り込み（EconomyService クライアント） | **Windows**（消費） | `unity/SurvivalWorld/Assets/Generated/` を参照 |
| `api` EconomyService（購入/売却/在庫生成/ランキング/NATS） | **WSL2** | 別冊 09B |
| DB マイグレーション（buyer_instances/buyer_stock/asset_rankings） | **WSL2** | 別冊 09B |

- **競合回避**: Windows 側は `unity/` と `scripts/*.ps1` のみを編集する。`services/`, `proto/`, `infra/`, `Makefile`, `scripts/*.sh` は触らない。`unity/SurvivalWorld/Assets/Generated/`（C# proto 生成物）は **WSL2 が生成**するので Windows は編集せず参照のみ（03A 0.4）。
- **単一 Writer 原則**（MVP 12.2.1・付録C・BSD 6.1/9.4）: 購入・売却・通貨・所有権・Buyer 在庫の**永続 Writer は常に API**。DS は Runtime（メモリ正本）のみを更新し、**購入付与を二重に永続化しない**。Client 入力の価格・数量・購入結果は採用しない（MVP-SEC-006）。
- **通貨・数量は整数**。DS 側でも金額を float で扱わない（表示専用の補間を除く）。API から受けた `Money.amount`（int64）をそのまま使う。

### 0.1 M6 の境界成果物（環境をまたぐファイル）

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| `economy.proto` / `gameplay.proto` の C# 生成コード | WSL2（`buf generate`） | **Windows（DS が消費）** | `unity/SurvivalWorld/Assets/Generated/` |
| Economy gRPC（`api` が公開、DS がクライアント） | WSL2（実装） | **Windows（DS が呼ぶ）** | 内部ネットワーク `API_GRPC_PORT`（既定 9092・TLS） |
| Buyer Kit（stall/sign/spawn marker）Blender 生成物 | WSL2（`make assets`） | **Windows（Import→Prefab）** | `build/assets/`→Unity Import |

- **proto を変更したとき**（09B が RegisterBuyer/DespawnBuyer を追加）: WSL2 で `make proto`→C# が `Assets/Generated/` に出力→Windows の Unity がコンパイル。Windows は生成物を編集しない（コミット漏れは WSL2 CI が検出）。

---

## 1. 対象と前提（Windows側）＋ M6 DoD

- OS: Windows。Unity Editor 6000.5.x（ネイティブ）。プロジェクトルートは `unity/SurvivalWorld/`。
- 前提: M1（FishNet 接続・Client 移動）、M2（共通 Inventory・Item Definition・Runtime version）、M3（採掘/製作/狩猟/料理）、M4（AI PersonalState・Template Runner・`economy.visit_buyer`/`economy.sell_surplus` テンプレの骨格）、M5（WorldState/LLM Decision）が完了していること。
- 本書完了で、DS 上に Buyer が spawn/despawn し、Player/AI が `BuyerPurchaseCommand` で購入でき、購入プロトコル（DS→API→Runtime 反映）と version 突き合わせ・snapshot 再同期が動作する状態にする。

### 1.1 Windows側 M6 DoD

- DS 上で Buyer が **spawn_at で出現・despawn_at で消滅**し、`status`（spawning/active/preparing/despawned）を持つ。**despawn 準備（preparing）後は新規購入を DS で拒否**し、**開始済みトランザクションのみ完了**させる。
- Buyer 出現時に **DS が seed を採番して `EconomyService.RegisterBuyer` を呼び**、API が生成した在庫（stall に表示する）を受け取って Runtime に反映する。
- Client/AI の `BuyerPurchaseCommand{ command_id, buyer_instance_id, stock_entry_id, inventory_version }` を DS が受け、**距離・Buyer存在・要求・version を検証**してから `Economy.CommitPurchase` を呼ぶ（MVP 8.7 図8-1）。
- **購入プロトコル（MVP 12.2.1）**: `CommitPurchase` の `PurchaseResult{ status, granted_items[], item_instance_ids[], new_persisted_inventory_version }` を受けて **Runtime Inventory に granted_items を反映し runtime version をインクリメント**。API の `new_persisted_inventory_version` と DS runtime version の**ズレ検知時は `RequestInventorySnapshot` で Full Snapshot 再同期**。
- **冪等**: 同一 `command_id` の再送で二重購入しない。timeout 時は同じ `idempotency_key` で照会/再試行し、判明まで Client に反映しない（MVP 16）。
- **AI 購入**: M4 テンプレ（`economy.visit_buyer`）から AI が Buyer へ移動→購入を実行。Player と同じ Purchase API を DS 経由で使う（Player との直接取引ではない・MVP 8.7）。
- **Buyer Kit 配置**: stall / sign / spawn marker の Prefab を配置し、spawn marker 位置に Buyer が出現、stall に在庫 UI を表示。
- EditMode/PlayMode テストが緑（`scripts\unity_test.ps1`）、Client/Server ビルドが通る（`scripts\unity_build_*.ps1`）。

---

## 2. 前提成果物（M0〜M5・Windows 側で参照）

| 由来 | 前提 | M6 での扱い |
|---|---|---|
| M0 | `Assets/Generated/` の C# proto（`economy_grpc.pb.cs`, `gameplay.pb.cs`：`BuyerPurchaseCommand`, `RequestInventorySnapshot`, `PurchaseStatus`, `Money`, `ItemRef`）、BuildScript、gRPC/FishNet 基盤 | DS が Economy gRPC クライアントとして利用 |
| M1 | FishNet Server/Client 接続、Connection ownership / Rate Limit / Sequence 検証（MVP-SEC-005） | 購入コマンドの検証に流用 |
| M2 | 共通 Inventory（Runtime, セッション Writer=DS）、Item Definition、Runtime version、Full Snapshot 再同期の枠組み | 購入付与の反映先・version 突き合わせ |
| M3 | 採掘/製作/狩猟/料理と Item 個体（品質/耐久） | 在庫アイテムの Runtime 表現 |
| M4 | AIActorController / ActionTemplateRunner / PrimitiveActionRegistry（MoveTo/Interact/**Purchase**）/ `economy.visit_buyer` / `economy.sell_surplus` | 本書 3.6 で Purchase primitive を Economy gRPC に接続 |
| M5 | WorldState Decision（AI が Buyer を選ぶ判断）、`economy_state` Projection | AI の購入判断の入力（DS は最終検証） |

> `RegisterBuyer` / `DespawnBuyer` は 09B で `economy.proto` に追加される。C# 生成物が `Assets/Generated/` に出るまで DS 側の該当コードはコンパイルできないため、**09B の L-1/L-2（proto 追加・生成）完了を待って**取り込む（0.1 の連携フロー）。

---

## 3. 実装対象（Windows / DS側）

配置方針（`unity/SurvivalWorld/Assets/`）:

```text
Assets/
├─ Scripts/Economy/
│  ├─ BuyerSpawnController.cs     # spawn_at/despawn_at スケジューリング・status 管理（3.1）
│  ├─ BuyerInstance.cs            # Runtime Buyer（stall/在庫/status/network id）
│  ├─ BuyerPurchaseHandler.cs     # BuyerPurchaseCommand 受信・検証（3.3）
│  ├─ PurchaseProtocol.cs         # DS→API CommitPurchase→Runtime 反映（3.4）
│  ├─ EconomyGrpcClient.cs        # EconomyService クライアント（RegisterBuyer/CommitPurchase/CommitSale/DespawnBuyer）
│  └─ InventoryReconciler.cs      # version ズレ検知→RequestInventorySnapshot（3.5）
├─ Scripts/AI/Economy/
│  └─ PurchasePrimitive.cs        # AI 購入 primitive（economy.visit_buyer から呼ぶ・3.6）
├─ Prefabs/Buyer/                 # stall / sign / spawn marker Prefab（4章）
└─ Editor/Buyer/                  # spawn marker 配置エディタ補助（任意）
```

`scripts/` 側は既存の `*.ps1`（ビルド/テスト）を流用。M6 で新規 ps1 は必須ではない。

### 3.1 Buyer spawn / despawn（`BuyerSpawnController.cs`・MVP 8.7 / 12.2）

DS は世界権威として Buyer の**出現タイミングと seed**を決める。永続（`buyer_instances`/`buyer_stock`）は API が持つ（付録C）。

- **出現周期**: 30分 ±10分、滞在 10分（暫定・MVP 8.7）。`spawn_at` / `despawn_at` を算出。
- **spawn 手順**:
  1. `seed`（int64）と `idempotency_key`（`(world_id, buyer 連番)` から決定的に生成）を採番。
  2. `EconomyService.RegisterBuyer(world_id, region_id, seed, inventory_table_id, price_modifier_bp, spawn_at, despawn_at)` を呼ぶ。
  3. 返却 `RegisterBuyerResponse{ buyer_instance_id, stock[] }` を受け、spawn marker 位置に `BuyerInstance` を生成（stall/sign Prefab）。在庫 `stock[]` を Runtime に保持し stall UI に表示。
  4. `status = active` にする。RegisterBuyer は idempotency_key で冪等なので、DS 再起動時の再登録は同一 Buyer を返す（AT-018 復元と整合）。
- **status 遷移**: `spawning → active → preparing → despawned`。
  - `despawn_at` の少し前（暫定 30秒前）に **`preparing`** へ遷移し、`DespawnBuyer(target_status="PREPARING")` を呼ぶ。**preparing 以降は DS で新規購入を拒否**（3.3）し、**開始済みトランザクション（CommitPurchase 送信済み・応答待ち）のみ完了**させる。
  - すべての開始済み Tx が解決したら `DespawnBuyer(target_status="DESPAWNED")` を呼び、Runtime の Buyer を despawn（stall/sign を除去）。
- **価格 modifier**: `price_modifier_bp`（basis point 整数、10000=×1.0）を DS 設定から渡す。DS では価格を再計算しない（unit_price は API が確定して stock に含める・09B 3.4）。
- **Rare Buyer Rush**（MVP 10.3 / AT-017）: WorldEvent 経由で 3 体をそれぞれ独立 seed で `RegisterBuyer`。各在庫は独立・Rare 保証なし（生成は API 側）。

### 3.2 BuyerInstance / stall UI（`BuyerInstance.cs`）

- FishNet の NetworkObject として stall/sign を同期。`buyer_instance_id`, `region_id`, `status`, 在庫リスト（`stock_entry_id`, `item_definition_id`, `unit_price`, `remaining_quantity`）を保持。
- 在庫の `remaining_quantity` は購入確定（API 応答）を受けて更新する（DS が勝手に減らさない。表示は Runtime 反映）。
- `unit_price` は表示のみ。Client からの購入要求では `stock_entry_id` を指すだけで価格は送らせない（MVP-SEC-006）。

### 3.3 BuyerPurchaseCommand の受信・検証（`BuyerPurchaseHandler.cs`・MVP 8.7 図8-1）

Client/AI → DS の `BuyerPurchaseCommand{ command_id, buyer_instance_id, stock_entry_id, inventory_version }`（`gameplay.proto`）を受ける。

DS 側検証（API を呼ぶ前・MVP-SEC-005/006）:

1. **Connection ownership / Rate Limit / Sequence**（M1 の枠組み）。
2. **command_id 冪等**: 同一 `command_id` を処理済みなら副作用なしで前回結果を返す（重複送信で二重購入しない・AT-003 の購入版）。
3. **距離**: 購入者アクターと Buyer stall の距離が閾値内か。
4. **Buyer 存在・status**: `buyer_instance_id` が Runtime に存在し `status == active`。**preparing/despawned なら拒否**（売切れ/購入失敗を Client へ）。
5. **在庫存在**: `stock_entry_id` が当該 Buyer にあり `remaining_quantity > 0`（表示上）。
6. **version**: `inventory_version` が購入者の Runtime inventory version と整合。
7. 検証通過後、`command_id`（または `(command_id, purchaser)`）から **決定的な `idempotency_key`** を生成し、購入プロトコル（3.4）へ。

- **最終確定は API**（DS の在庫表示はあくまで表示。実際の売切れ/残高は API が判定）。DS 検証は無駄な RPC を減らすためのフィルタであり、権威ではない。

### 3.4 購入プロトコル：DS→API→Runtime 反映（`PurchaseProtocol.cs`・MVP 12.2.1）

**単一 Writer プロトコル**の DS 側。

```text
1. DS  → API : Economy.CommitPurchase(idempotency_key, buyer_instance_id, stock_entry_id, purchaser_id, inventory_version)
2. API       : buyer_stock/currency_ledger/item_instances/inventory_entries を単一Txで確定（09B 3.6）
3. API → DS  : PurchaseResult{ status, granted_items[], item_instance_ids[], new_persisted_inventory_version, charged }
4. DS        : status==COMMITTED/DUPLICATE のとき Runtime Inventory へ granted_items を反映し runtime version をインクリメント
5. DS        : 購入付与は Outbox へ再送しない（二重永続化しない）。以降の通常 Inventory 変更のみ Outbox 経由で API へ
```

- **status 分岐**（`PurchaseStatus`）:
  - `COMMITTED`: Runtime Inventory に `granted_items` + `item_instance_ids` を追加、runtime version を `new_persisted_inventory_version` に整合させる。stall の `remaining_quantity` を減算。Client/Buyer UI 更新。
  - `DUPLICATE`: 既確定。同じ `granted_items` を返すので **Runtime に未反映なら反映**（再送で結果が揃う）。二重反映しない（command_id/idempotency 単位で1回）。
  - `OUT_OF_STOCK`: 売切れを Client へ。stall の在庫表示を実残量に補正。
  - `INSUFFICIENT_FUNDS` / `REJECTED`: 購入失敗を Client へ。Runtime を変更しない。
- **timeout / crash**（MVP 16 / AT-019）: API 応答が来ない場合、**Client に Pending を出さず**、同一 `idempotency_key` で照会/再試行する。判明するまで Runtime に反映しない。DS 再起動後も同じ idempotency_key で結果を回収できる（API 側が冪等保存・09B 3.6）。
- **granted_items の反映は API 結果のみを正**とする。DS がローカルに先行付与してから確定を待つ方式は取らない（version ズレの温床）。

### 3.5 version 突き合わせと再同期（`InventoryReconciler.cs`・MVP 12.2.1 / 16）

- 反映後、DS runtime version と API の `new_persisted_inventory_version` を突き合わせる。
- **ズレ検知時**は `RequestInventorySnapshot{ last_known_version }`（`gameplay.proto`）で **Full Snapshot 再同期**（MVP 16 Inventory version conflict）。
- 通常の採掘/料理/廃棄では DS が Runtime を更新→変更 Domain Event を Outbox 経由で API へ送って永続化（この経路でも永続 Writer=API）。**購入付与だけは Outbox に載せない**（12.2.1 ステップ5）。

### 3.6 AI 購入行動（`PurchasePrimitive.cs`・M4 テンプレ経由・MVP 8.7 / 9.3）

- M4 の `economy.visit_buyer`（tags: `wanted_item, buyer_available, cash_available`）テンプレから、`PrimitiveActionRegistry` の **Purchase primitive** を呼ぶ。
- Step: `MoveTo(buyer stall)` → 距離・Buyer active・要求充足・残高（Runtime 表示）確認 → **`BuyerPurchaseCommand` を DS 内部で発行**（AI も Client と同じ購入経路。Player との直接取引ではない・MVP 8.7）→ 3.3/3.4 の同一処理。
- AI は Player と同じ `Economy.CommitPurchase` を DS 経由で使う（AT-013：Inventory も共通規則）。AI 用に別 API を作らない。
- `economy.sell_surplus`（inventory_overflow）からは `CommitSale`（09B 3.7）を DS 経由で呼ぶ売却 primitive を用意（在庫過多時の売却）。
- LLM Decision（M5）が Buyer を選ぶが、**DS が最終検証**（存在/前提/鮮度/権限）してから購入を実行（MVP 9.4）。Decision 未達時は現行行動継続→Utility Fallback（M4）。

### 3.7 Buyer Kit 配置（`Prefabs/Buyer/`・MVP 15章）

- Blender 生成物（stall / sign / spawn marker）は **WSL2 の `make assets`** で生成される（Buyer Kit・MVP 15）。Windows は Import Processor で Client/Server Prefab を生成し配置する（09B は生成のみ、配置は Windows）。
- **spawn marker**: Buyer が出現する位置の Empty/マーカー。`BuyerSpawnController` が spawn marker 位置に `BuyerInstance` を生成。
- **stall / sign**: Buyer 本体の見た目と在庫 UI のアンカー。Server Prefab は Collider/Interaction Point、Client Prefab は表示。
- MVP 受入（15章）: 生成コマンド1回で Buyer Kit が再生成され、Unity Batchmode Import が成功すること（Import 検査は共通・WSL2 の CI が manifest/socket/collider 等を検査）。

---

## 4. 実装順序（Windows側）

| # | タスク | 完了確認 |
|---|---|---|
| W-1 | 09B の proto 追加（RegisterBuyer/DespawnBuyer）反映後、`Assets/Generated/` を取り込みコンパイル | エラーなくコンパイル |
| W-2 | `EconomyGrpcClient.cs`（RegisterBuyer/CommitPurchase/CommitSale/DespawnBuyer のクライアント、TLS/内部ネットワーク） | api gRPC(9092) へ接続確認 |
| W-3 | Buyer Kit Import→`Prefabs/Buyer/`（stall/sign/spawn marker）配置 | spawn marker が世界に存在 |
| W-4 | `BuyerSpawnController.cs`（spawn_at/despawn_at・seed 採番・RegisterBuyer・status 管理） | Buyer が出現し在庫表示 |
| W-5 | `BuyerInstance.cs` + stall 在庫 UI（NetworkObject 同期） | Client に Buyer/在庫が見える |
| W-6 | `BuyerPurchaseHandler.cs`（コマンド検証・距離/status/version・command_id 冪等） | 不正/遠距離/preparing で拒否 |
| W-7 | `PurchaseProtocol.cs`（CommitPurchase→PurchaseResult→Runtime 反映・runtime version++） | 購入で Inventory に付与、在庫減 |
| W-8 | `InventoryReconciler.cs`（version 突き合わせ→RequestInventorySnapshot） | ズレ時に Full Snapshot 再同期 |
| W-9 | despawn（preparing で新規拒否・開始済Tx完了→DESPAWNED） | 消滅、開始済み購入は完了する |
| W-10 | `PurchasePrimitive.cs`（AI 購入・economy.visit_buyer / sell_surplus 接続） | AI が Buyer から購入/売却する |
| W-11 | EditMode/PlayMode テスト・Client/Server ビルド | `unity_test.ps1` / `unity_build_*.ps1` 緑 |

---

## 5. テスト・受入（Windows側）

`scripts\unity_test.ps1`（EditMode/PlayMode）と Network E2E（2 Client + DS + Backend・MVP 18.1）。

| 種別 | ケース | 対応 AT / 合格条件 |
|---|---|---|
| EditMode | 購入コマンド検証：距離外/status!=active/version 不一致/残量0 で拒否 | MVP-SEC-005/006 |
| EditMode | command_id 冪等：同一 command_id 2回で1回分のみ反映 | AT-003（購入版） |
| PlayMode | Buyer 出現：spawn marker に出現、在庫が有限・Rare 無しの回もある | AT-011 |
| PlayMode | 購入成功→Runtime 反映：granted_items 付与、runtime version が new_persisted_version と一致 | AT-021（二重Write防止） |
| PlayMode | version ズレ→Full Snapshot 再同期 | MVP 16 |
| E2E | 同一在庫を2 Client が同時購入 → 1者のみ成功、他は売切れ。DS 表示に不整合なし | AT-012 |
| E2E | 購入応答直後に DS crash → 再起動後、同 idempotency_key で照会し購入 Item/通貨が保持 | AT-019 |
| E2E | despawn 準備後の購入要求は拒否、開始済み Tx は完了 | MVP 12.2 |
| PlayMode | AI 購入：economy.visit_buyer で Buyer へ移動→購入、Inventory 共通規則 | AT-013 |
| E2E | Rare Buyer Rush：3 Buyer 出現・各在庫独立・Rare 保証なし | AT-017 |

- **AT-021（購入時 Inventory 二重Write防止）**が Windows 側の最重要受入: 購入後、`inventory_entries` の付与は1回のみ（API 確定）、DS runtime version と API の `new_persisted_inventory_version` が一致し、二重付与・欠落が起きないこと。
- DS は購入付与を Outbox に載せない（12.2.1）。E2E で domain_events / inventory_entries を確認（DB 側検証は 09B と連携）。

---

## 6. 落とし穴（Windows側）

- **二重 Write / 二重付与**（最重要・12.2.1）: DS が購入付与を「先行反映＋Outbox 送信」してはいけない。**API 確定結果のみを Runtime に映す**。購入付与は Outbox に載せない。
- **DS が権威になってしまう**: 在庫の売切れ/残高は API が判定。DS 表示を根拠に確定しない。DS 検証はフィルタであって権威ではない（MVP-SEC-006）。
- **冪等の穴**: `command_id` と `idempotency_key` を分けて扱う。timeout 時は Pending を出さず同一 idempotency_key で照会（MVP 16）。DUPLICATE 応答でも二重反映しない。
- **despawn の取りこぼし**: preparing で新規購入を止めても、**開始済み Tx は必ず完了**させてから despawn。応答待ちを打ち切って Item/通貨を宙に浮かせない（AT-019）。
- **version 不整合の放置**: 反映後は必ず `new_persisted_inventory_version` と突き合わせ、ズレたら `RequestInventorySnapshot`（放置すると以降の Inventory 操作が壊れる）。
- **AI 専用購入 API を作る**: 禁止。AI も Player と同じ `CommitPurchase` を DS 経由で使う（AT-013）。
- **価格を Client/DS で再計算**: しない。`unit_price` は API 確定値。DS は表示のみ。金額を float で扱わない。
- **proto 生成物の取り込み漏れ**: 09B の RegisterBuyer/DespawnBuyer 追加後、`Assets/Generated/` の C# がコミット済みかを確認（生成は WSL2、Windows は編集せず参照）。未生成だと DS がコンパイルできない。
- **gRPC 到達性**: DS→api gRPC(9092) は内部ネットワーク・TLS のみ（MVP-SEC-001/007）。Client から API/WorldState へ直接到達させない。
- **`.ps1` の CRLF / `.sh` を触る**: Windows 側は `*.ps1` と `unity/` のみ編集。`scripts/*.sh` や `services/` を触らない（03A 0.3 / 競合回避）。

---

## 参考資料

- MVP 詳細設計書 v0.2.2：8.7（Buyer・図8-1 購入シーケンス）、9.2/9.3/9.4（AI PersonalState/Template/Decision 適用）、12.2 / 12.2.1（Purchase Transaction・単一Writer プロトコル）、13/13.1（DB・所有権）、14.1（Gameplay Commands：BuyerPurchaseCommand / RequestInventorySnapshot）、15（Buyer Kit）、16（エラー・再試行）、18（AT-011/012/013/017/019/021）、19（M6 DoD）、付録B.3、付録C。
- 基本設計書 v0.2.1：6.4（Buyerと経済・図6-1 権威フロー）、6.1/9.4（単一 Writer）。
- proto：`proto/survival/v1/gameplay.proto`（BuyerPurchaseCommand）, `economy.proto`（EconomyService）, `common.proto`（Money/ItemRef/PurchaseStatus）。
- [R-DSBUILD] [Unity Manual: Dedicated Server build](https://docs.unity3d.com/6000.2/Documentation/Manual/dedicated-server-build.html)
- [R-CLI] [Unity Manual: Command line arguments](https://docs.unity3d.com/Manual/EditorCommandLineArguments.html)
- [R-GRPC-CS] [gRPC C#](https://grpc.io/docs/languages/csharp/)
- [R-FISHNET] [FishNet Documentation](https://fish-networking.gitbook.io/docs/)
