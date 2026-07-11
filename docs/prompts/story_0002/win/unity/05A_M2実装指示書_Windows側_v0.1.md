---
title: "M2 実装指示書（Windows ネイティブ側）"
subtitle: "Unity Runtime 共通Inventory / Item Definition / InventoryCommand / World Bootstrap クライアント / Cache復旧"
document_id: "IMPL-M2-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M2 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / R3 / VContainer / UniTask / gRPC(C#)"
related_document: "05B_M2実装指示書_WSL2側_v0.1.md, 04A_M1実装指示書_Windows側_v0.1.md, 04B_M1実装指示書_WSL2側_v0.1.md, 03A_M0実装指示書_Windows側_v0.1.md, 03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M2 実装指示書：Windows ネイティブ側 v0.1

本書は **M2「Inventory / Save」** の作業を **Windows ネイティブ側**（Unity Runtime の共通 Inventory、Item Definition、InventoryCommand 処理、Dedicated Server の World Bootstrap クライアント、ローカルキャッシュ復旧）に限定して指示する。API の gRPC 永続化・DB・Outbox は別冊 **05B（WSL2側）** を参照。第0章「分担と連携」は両冊で共通・同一内容（M0 03A/03B と同一方針）。

M2 の到達点（MVP 第19章）: **共通Inventory・Item Definition・World Load/Save・Cache削除復旧**。本書（A）はその**セッション（ランタイム）Writer 側**（DS メモリ正本）と Client 表示を実装する。永続 Writer（API）は 05B（B）が実装する。

---

## 0. 分担と連携（共通・両冊同一）

### 0.1 環境別 責務分担（M2 該当分を再掲）

| 領域 | 担当環境 | M2 の主なタスク |
|---|---|---|
| **Unity Runtime 共通Inventory** | **Windows(A)** | slot_capacity/weight_capacity/version, owner_type/owner_id, 予約 |
| **Item Definition（ScriptableObject）** | **Windows(A)** | MVP 7.2 の18定義を SO 化（正本 JSON は B 供給） |
| **InventoryCommand 処理** | **Windows(A)** | operation/expected_version/item_ref/quantity/target_ref, 楽観ロック |
| **RequestInventorySnapshot** | **Windows(A)** | version ズレ時の Full Snapshot 再同期 |
| **World Bootstrap クライアント（DS）** | **Windows(A)** | `LoadBootstrap` gRPC→payload復元→event tail 適用→Ready Heartbeat |
| **ローカルキャッシュ削除→復旧** | **Windows(A)** | 5.4、重要データを Cache に置かず Server 正本から再取得 |
| **単一Writer原則の遵守（DS 側）** | **Windows(A)** | 永続=API, セッション=DS。二重永続化しない |
| WorldDataService/ActorState gRPC 実装（API） | **WSL2(B)** | LoadBootstrap/AppendEvents/SaveSnapshot/ActorState.Save |
| DB 拡張 / Outbox relay / Item Def 正本 JSON | **WSL2(B)** | migrations 0002, ≤1秒 relay, `data/item_definitions.json` |
| proto → C# 生成 | **WSL2(B)**（生成） | `unity/SurvivalWorld/Assets/Generated/` へ出力 |

### 0.2 リポジトリ配置・Git/LFS・改行コード規約

- 配置・改行・LFS の規約は M0（03A/03B 第0.2〜0.3章）と同一。**変更しない**（sh=LF, ps1=CRLF, 資産=LFS）。
- A が触るのは **`unity/` と `scripts/*.ps1` のみ**。`services/`, `proto/`, `Makefile`, `*.sh` は触らない。
- **`unity/SurvivalWorld/Assets/Generated/` は B の生成物**（`buf generate` 出力）。A は**消費（コンパイル）するだけ**で手編集しない。生成物のコミット漏れは B の CI が検出する。

### 0.3 境界成果物（M2 で増えるもの）

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| proto → C#（worlddata/gameplay/common の gRPC/message） | WSL2 | Windows | `unity/SurvivalWorld/Assets/Generated/` |
| **Item Definition 正本 JSON** | **WSL2(B)** | **Windows(A)（SO へ取込）** | `services/api/data/item_definitions.json` |
| gRPC 接続先（api gRPC ポート 8092 既定） | WSL2(B) が公開 | Windows(A)（DS が接続） | `.env`（B 管理）と DS Config の一致 |
| Linux Dedicated Server ビルド成果物 | Windows（クロスビルド） | WSL2/Docker（実行） | `unity/SurvivalWorld/Build/Server/` |

### 0.4 連携フロー（M2 代表例）

- **DS 起動時**: A の World Bootstrap クライアントが B の `WorldData.LoadBootstrap(world_id, server_build)` を **C# gRPC クライアント**で呼び、`snapshot_id / sequence / snapshot_payload / event_tail[]` を受ける。payload を World Runtime へ復元し、`sequence` 以降の `event_tail` を **昇順に適用**してから Ready Heartbeat を送る（MVP 12.1）。
- **プレイ中**: A（DS）が確定した Inventory 変更等を Domain Event 化し、**Outbox → `WorldData.AppendEvents` へ ≤1秒 間隔**で送る。event は `event_id`(ULID) と `local_sequence` を A が採番。**永続 `sequence` は API(B) が採番**（A は採番しない）。
- **30秒ごと**: A（DS）が World Snapshot を作り checksum を計算して `WorldData.SaveSnapshot` を呼ぶ。checksum の算出式は **B と一致**させる（落とし穴6.5）。
- **単一Writer原則**: 購入以外の通常 Inventory 変更は DS がランタイムを更新し、Outbox 経由で API へ送る。購入付与は API が確定した結果を DS が**映すだけ**（二重永続化しない・MVP 12.2.1）。

---

## 1. 対象と前提（Windows側）と M2 DoD

### 1.1 対象

M2 で A が実装するのは、**DS を「World と Inventory のセッション（ランタイム）Writer」として機能させる**部分と、Client の Inventory 表示・Cache 復旧である。

1. **共通 Inventory ランタイム**（Player/AI 共通。MVP 7.1 / 基本設計 6.1）。
2. **Item Definition**（ScriptableObject。B 供給の正本 JSON を取込・MVP 7.2）。
3. **InventoryCommand 処理**（サーバー権威・楽観ロック・MVP 7.3 / 14.1）。
4. **RequestInventorySnapshot**（version 不一致時の Full Snapshot 再同期・MVP 16章）。
5. **World Bootstrap クライアント**（DS 起動時の Load→適用→Ready・MVP 12.1）。
6. **Persistence Agent（DS 側）**: Event Outbox flush（≤1秒）と 30秒 Snapshot のタイマー、AppendEvents/SaveSnapshot 呼出。
7. **ローカルキャッシュ復旧**（Safe Delete・重要データ非保存・MVP 5.4）。
8. EditMode/PlayMode テスト。

### 1.2 M2 DoD（Windows側の満たすべき条件）

- **共通 Inventory** が Player/AI 双方で同一実装を使い、`slot_capacity=24` / `weight_capacity=40.0` / `version`（Mutation 成功ごとに +1）/ `reserved`（製作・購入中の確保）を持つ（MVP 7.1）。
- **Item Definition** 18種が SO 化され、B の `item_definitions.json` と一致する（定義ズレなし）。Client は定義を**参照**できるが quantity/quality/durability を自由指定できない（MVP 7.3 / MVP-SEC-006）。
- **InventoryCommand** が Server 権威で直列適用され、`expected_version` 不一致は **Conflict** として `RequestInventorySnapshot` で Full Snapshot を返す（MVP 7.3 / 16章）。同一 `command_id` の二重送信は1回分のみ反映（冪等・AT-003）。
- **World Bootstrap**: DS 起動で `LoadBootstrap` を呼び、Snapshot payload 復元 → `event_tail` を sequence 昇順で適用 → **Ready Heartbeat** を送るまでが成立する（MVP 12.1）。
- **Persistence**: プレイ中に Domain Event を Outbox へ積み **≤1秒**で `AppendEvents` 送信、**30秒ごと**に Snapshot を `SaveSnapshot` 送信。event の `event_id`(ULID)/`local_sequence` は DS 採番、**永続 sequence は採番しない**。
- **Cache 復旧（AT-002）**: Client の Cache フォルダを削除して再ログインしても、Character/Inventory/所持金/位置が Server 正本から復元され同一である。**重要データを Cache に保存していない**（MVP 5.4 / 付録C）。
- **単一Writer原則**: 購入付与を DS が二重に永続化しない。runtime version と API の `new_persisted_version` を突き合わせ、ズレ時 Full Snapshot 再同期（MVP 12.2.1・AT-021）。
- `scripts/unity_test.ps1` の EditMode/PlayMode テストが緑。`scripts/unity_build_server.ps1` で Linux DS がビルドできる。

---

## 2. 前提成果物（M0 / M1 で構築済み）

M2 は次を**前提**とする（再実装しない）。

**M0（03A / 既存）**:
- Unity 6000.5.x URP プロジェクト（`unity/SurvivalWorld/`）。R3 / VContainer / UniTask / FishNet / Input System 導入済み・コンパイル通過。
- `Assets/Editor/BuildScript.cs`（`BuildLinuxServer` / `BuildWindowsClient`）、`scripts/unity_{test,build_client,build_server}.ps1`。
- `Assets/Generated/`（proto C# 生成物の受け皿。M2 では worlddata/gameplay/common が生成済み）。

**M1（04A / 前提）**:
- Auth ログイン、Matchmaking、Join Ticket、FishNet 接続、2 Client 三人称移動（WASD/カメラ）。
- `NetworkSessionClient` / `JoinTicketAuthenticator` / `NetworkPlayerController` 等（MVP 5.3 / 6.2）。
- DS が Auth と内部 gRPC（Join Ticket 消費・Heartbeat）で通信できる基盤。M2 の WorldData gRPC 呼出はこの gRPC クライアント基盤を流用する。

> M0/M1 の DoD が全緑であることを前提とする。

---

## 3. 実装対象（ドメイン／コンポーネントごと）

### 3.1 共通 Inventory ランタイム（MVP 7.1 / 基本設計 6.1）

Player/AI が**同一実装**を使う（AI は `AIInventoryAdapter` 経由・MVP 9.1）。`Assets/Scripts/Runtime/Inventory/` に配置。

データ（基本設計 6.1 の具体化）:
```csharp
public sealed class InventoryOwner {
    public string OwnerType;      // "player" | "ai"
    public string OwnerId;
    public int    SlotCapacity;   // 既定 24
    public float  WeightCapacity; // 既定 40.0（表示・判定はここでは float 可。永続は B が milli 整数）
    public long   Version;        // Mutation 成功ごとに +1
    public readonly List<InventoryEntry> Entries = new();
}
public struct InventoryEntry {
    public int    SlotIndex;
    public string ItemDefinitionId;
    public string ItemInstanceId;   // 個体（品質/耐久）を持つ物のみ
    public int    Quantity;
    public int    Reserved;         // 製作・購入中の確保分
}
```

規則:
- **Stack** は `ItemDefinition.StackLimit` まで。超過は新スロット、満杯（`Entries` が `SlotCapacity` 超・重量超）は失敗。
- **満杯時**: Player は操作失敗、AI は `inventory_pressure` を加算（MVP 7.1 / 9.2）。
- **予約**: 製作・購入開始時に `Reserved` へ確保、完了で消費、Cancel で解放（MVP 8.4）。
- **World Drop**: `DROP` は WorldItem Entity を生成し位置・Owner・Item を永続化対象にする（MVP 7.1・実体は M3 と接続）。
- **`version` は Mutation 成功ごとに +1**。これが楽観ロックの基準。
- **権威は DS**（メモリ正本・付録C）。Client の R3 ReactiveProperty は**表示投影のみ**で正本にしない（MVP 5.5.2）。

### 3.2 Item Definition（ScriptableObject・MVP 7.2）

- `Assets/Scripts/Runtime/Items/ItemDefinition.cs`（`ScriptableObject`）と `Assets/Data/Items/*.asset`（18種）。
- **正本は B の `services/api/data/item_definitions.json`**。A はこれを取り込む（Editor スクリプトで JSON→SO 生成、または実行時ロード）。**手打ちで値をズラさない**（落とし穴6.2）。
- フィールド（基本設計 6.1 / MVP 7.2）:
```csharp
[CreateAssetMenu(menuName="Survival/ItemDefinition")]
public sealed class ItemDefinition : ScriptableObject {
    public string   ItemDefinitionId;
    public string[] Tags;         // "resource.stone" 等
    public int      StackLimit;
    public float    Weight;       // 表示用。永続は B が weight_milli
    public int      Rarity;       // 0=common..3=epic
    public long     BaseValue;    // 通貨は long（BIGINT 対応）
    public ItemUseEffect UseEffect; // cooked_meat: Hunger+30, luxury_food: Hunger+30/waste x2
}
```
- 18定義（MVP 7.2, `id | 主要Tag | Stack | 重量 | rarity`）: stone(50/1.0/0), iron_ore(30/1.5/0), rare_ore(10/1.5/2), wood(30/0.8/0), iron_ingot(20/1.2/0), rare_ingot(10/1.2/2), leather(20/0.5/0), bone(20/0.5/0), stone_spear(1/3.0/0), raw_meat(10/1.0/0), rare_meat(5/1.0/2), cooked_meat(10/0.8/0), food_waste(20/0.3/0), stone_pickaxe(1/4.0/0), iron_hunting_spear(1/5.0/0), luxury_food(5/0.8/2), decorative_weapon(1/6.0/2), rare_weapon(1/5.0/3)。
- **Client は定義参照のみ**。quantity/quality/durability を自由指定させない（MVP 7.3 / MVP-SEC-006）。

### 3.3 InventoryCommand 処理（サーバー権威・楽観ロック・MVP 7.3 / 14.1）

生成 proto（`unity/SurvivalWorld/Assets/Generated/`、既存 `gameplay.proto`）を消費する:
```protobuf
// InventoryCommand { command_id, expected_version, operation, item_ref, quantity, target_ref }
// enum InventoryOperation { UNSPECIFIED, MOVE, SPLIT, MERGE, DROP, USE }
// ItemRef { item_definition_id, item_instance_id }
```

処理（DS 側 `InventoryService`・MVP 6.2）:
1. **同一 owner_id の Command を一つずつ直列適用**（並行 Mutation を作らない）。
2. `expected_version` と現在 `Version` を照合。**不一致は Conflict** → `RequestInventorySnapshot` 相当の Full Snapshot を返し反映しない（MVP 7.3 / 16章）。
3. 一致時、`operation` を適用（MOVE/SPLIT/MERGE/DROP/USE）。成功で `Version += 1`。
4. **冪等性**: 同一 `command_id` の再受信は副作用なく前回結果を返す（AT-003）。処理済み command_id を owner 単位で保持。
5. 適用結果を Domain Event 化し Outbox へ（3.6）。Replication Delta を Owner/Interest 対象へ複製（MVP 6.1）。
6. **AI も InventoryService を経由**（`AIInventoryAdapter`）。内部フィールドを直接変更しない（MVP 7.3 / 9.1）。

> proto の `InventoryOperation`（MOVE/SPLIT/MERGE/DROP/USE）は **wire の正**。MVP 7.3 本文の概念操作（ADD/REMOVE/RESERVE 等）はサーバー内部の遷移として表現し、外部コマンドは proto enum に従う（落とし穴6.6）。ADD/REMOVE 相当（採掘付与・消費）は Command ではなく Server 内部の確定処理として行い Event 化する。

### 3.4 RequestInventorySnapshot（MVP 14.1 / 16章）

- Client が `RequestInventorySnapshot { last_known_version }` を送る。
- DS は現在の Inventory 全量（entries + version）を **Full Snapshot** として返す。
- 用途: (a) `expected_version` Conflict の回復、(b) Cache 削除後の初期同期、(c) 購入後の runtime/persisted version ズレ検知時の再同期（MVP 12.2.1）。
- Client 側 `InventoryViewModel` は受領 Snapshot で R3 の複製状態を丸ごと差し替える（MVP 5.3 / 5.5.2）。

### 3.5 World Bootstrap クライアント（DS 起動・MVP 12.1）

`ServerBootstrap` / `PersistenceAgent`（MVP 6.2）に実装。**C# gRPC クライアント**で B の `WorldDataService` を呼ぶ。

手順（MVP 12.1）:
1. DS 起動時、`WorldData.LoadBootstrap(world_id, server_build)` を呼ぶ。
2. 応答 `snapshot_payload`（bytes JSONB）を **World Runtime へ復元**（Entity Registry / Node 残量 / Inventory 等）。`snapshot_id==""`（新規 world）なら空初期化。
3. `event_tail[]` を **`sequence` 昇順**に順次適用（Snapshot 以降の差分）。適用は冪等（`event_id` 単位）に。
4. World Runtime 初期化完了後に **Ready Heartbeat** を Auth/Matchmaking へ送る（MVP 6.2 / 11.2 Heartbeat の `ready=true`）。Ready になるまで Join を受け付けない。
5. 復元は **UniTask + CancellationToken** で書き、失敗時はリトライ／Graceful に中断（MVP 5.5.2）。

### 3.6 Persistence Agent（DS 側 Outbox / Snapshot タイマー・MVP 12.1）

- **Event Outbox flush（≤1秒）**: DS が確定した Domain Event（Inventory 変更・採取・消費・廃棄）を DS ローカル Outbox に積み、**最大1秒程度**でまとめて `WorldData.AppendEvents(server_id, events[])` を呼ぶ。
  - 各 `DomainEvent` の `event_id` は **ULID を DS 生成**、`local_sequence` は aggregate 内順序を DS 生成、`occurred_at_unix_ms` を設定。**`sequence`（world 永続順序）は設定しない（API が採番）**。
  - 応答 `results[]`（OK/DUPLICATE/CONFLICT）を見て、DUPLICATE は成功扱い、CONFLICT は再送または Snapshot 再同期。
- **30秒ごと Snapshot**: World Runtime から Snapshot payload（bytes）を作り、**checksum を計算**して `WorldData.SaveSnapshot(world_id, sequence, checksum, payload)` を呼ぶ。
  - `sequence` は「その Snapshot が反映済みの最新 event sequence」を渡す（B が active pointer を更新）。
  - **checksum 算出式は B と一致**（例: payload バイト列の SHA-256 hex。B の検証と同一定義。落とし穴6.5）。
- **Graceful Shutdown**: 停止時に Outbox を flush し最終 Snapshot を保存（MVP 6.2 / 9.2）。
- タイミングは Config 化（`OutboxFlushIntervalMs=1000` / `SnapshotIntervalSec=30`。MVP 3章の暫定値）。

### 3.7 単一Writer原則の遵守（DS 側・MVP 12.2.1 / 付録C）

- **永続 Writer=API、セッション Writer=DS**。DS は通常 Inventory 変更をランタイムに適用し、Event を Outbox 経由で API へ送る（API が永続確定）。
- **購入（M6 で本格化・M2 は土台）**: API が `inventory_entries` を Tx 確定 → `PurchaseResult{ granted_items[], new_persisted_inventory_version }` を DS が受け、**ランタイムへ映すだけ**（同じ付与を Outbox で再送・二重永続化しない）。
- DS の runtime version と API の `new_persisted_inventory_version` を突き合わせ、**ズレ検知時は `RequestInventorySnapshot` で Full Snapshot 再同期**（AT-021）。
- M2 ではこの**プロトコルの DS 側フック**（runtime version 管理・再同期経路）を用意する。実購入 Tx は M6。

### 3.8 ローカルキャッシュ削除→復旧（MVP 5.4 / AT-002）

- `ClientCacheService`（MVP 5.3）: **保存可**＝画質/音量/キー設定・Addressables Cache・規約 Version・最後の画面・ログ。**保存禁止**＝所持金/Inventory/World Save/Buyer Stock/AI State/ランキング/購入成功状態。
- Refresh Token は機密。OS Credential Store 相当を優先し**平文ファイル禁止**（M1 実装を踏襲）。
- **Safe Delete**: Cache フォルダ削除後もクラッシュせず、再ログイン→接続→`RequestInventorySnapshot` / Bootstrap で Server 正本から Inventory/位置を復元。
- 受入（AT-002）: Cache 削除→再ログインで Server 上の Character/Inventory が同一。

---

## 4. 実装順序表（Windows側）

| # | タスク | 完了確認 |
|---|---|---|
| W2-1 | `Assets/Generated/` に worlddata/gameplay/common の C# 生成物が入りコンパイル通過 | 生成物参照でビルド緑 |
| W2-2 | gRPC C# ランタイム導入（Grpc.Net.Client 等・NuGetForUnity） | gRPC クライアントが生成 stub を使える |
| W2-3 | 共通 Inventory ランタイム（3.1・Player/AI 共通） | slot/weight/version/reserved の単体テスト緑 |
| W2-4 | Item Definition SO＋B の JSON 取込（3.2） | 18定義が JSON と一致 |
| W2-5 | InventoryService（Command 直列適用・楽観ロック・冪等）（3.3） | version 不一致 Conflict・二重 command 冪等 |
| W2-6 | RequestInventorySnapshot（3.4） | Full Snapshot 返却で Client が再同期 |
| W2-7 | World Bootstrap クライアント（LoadBootstrap→復元→tail適用→Ready）（3.5） | DS が Ready になるまで Join 拒否 |
| W2-8 | Persistence Agent（Outbox ≤1秒 / Snapshot 30秒 / checksum）（3.6） | AppendEvents/SaveSnapshot 疎通・checksum 一致 |
| W2-9 | 単一Writer フック（runtime version 管理・再同期経路）（3.7） | version ズレで再同期が走る |
| W2-10 | ClientCacheService Safe Delete（3.8） | Cache 削除後も復旧・重要データ非保存 |
| W2-11 | EditMode/PlayMode テスト（5章）＋ `scripts/unity_test.ps1` | 全緑 |
| W2-12 | `scripts/unity_build_server.ps1`（Linux DS ビルド） | Build/Server 生成 |

---

## 5. テスト・受入（Windows側）

### 5.1 EditMode（MVP 18.1）

- **Inventory version**: 各 Mutation で +1、失敗時は据え置き。
- **Stack/Weight**: StackLimit 超で新スロット、`SlotCapacity`/`WeightCapacity` 超で失敗、満杯 AI は inventory_pressure 加算。
- **Item Definition**: 18定義が B の JSON と一致（値ズレ検出）。
- **InventoryCommand 楽観ロック**: `expected_version` 不一致で Conflict、一致で適用＆+1。
- **冪等性（AT-003）**: 同一 `command_id` 二重適用で1回分のみ変化。
- **checksum**: 同一 payload で A の算出値が B の期待と一致（フィクスチャで固定・落とし穴6.5）。

### 5.2 PlayMode / 統合

- **World Bootstrap**: モック（またはローカル B）で `LoadBootstrap`→復元→tail 適用→Ready の順が成立。event_tail は sequence 昇順で適用。
- **Cache 復旧（AT-002）**: Cache 削除後の再ログインで Inventory/位置が Server 正本と一致。
- **単一Writer（AT-021）**: 購入相当のモック応答で runtime version と persisted version を突き合わせ、ズレ時 `RequestInventorySnapshot` が走り二重付与しない。
- **Server 再起動（AT-018 の DS 側）**: DS 再起動で `LoadBootstrap`（Snapshot+tail）から World/Inventory が復元。

### 5.3 受入トレーサビリティ（MVP 18章）

| AT | A の関与 | 確認 |
|---|---|---|
| AT-002 | Cache 削除復旧 | Server 正本から Inventory/位置復元、重要データ非保存 |
| AT-003 | 二重送信で1回分 | InventoryCommand の command_id 冪等 |
| AT-018 | Server 再起動復元 | World Bootstrap（Snapshot+tail）＋Ready |
| AT-021 | 購入時 Inventory 二重 Write 防止 | DS は runtime のみ、version 突合で再同期 |

### 5.4 ビルド/テストスクリプト

- `scripts/unity_test.ps1`（EditMode/PlayMode。M0 03A の作法。`-runTests` に `-quit` を付けない）。
- `scripts/unity_build_server.ps1`（Linux DS。`-standaloneBuildSubtarget Server`）。

---

## 6. 落とし穴（Windows側）

1. **sequence を DS 側で採番しない**。DS が採番するのは `event_id`(ULID) と `local_sequence` まで。**永続 `sequence` は API(B) が採番**（MVP 13.1）。DS が sequence を勝手に決めると CONFLICT や重複破綻を招く。
2. **Item Definition を手打ちしない**。正本は B の `item_definitions.json`。SO は取込で生成し、値ズレ（stack/weight/rarity）を作らない。
3. **R3 ReactiveProperty を Inventory の正本にしない**。正本は DS メモリ。Client の複製は表示投影のみ（MVP 5.5.2）。購読は `AddTo(destroyCancellationToken)` で破棄。
4. **重要データを Cache に保存しない**。所持金/Inventory/World Save/購入成功状態は Cache 禁止（MVP 5.4 / 付録C）。Refresh Token は平文禁止。
5. **checksum 算出式を B と一致**させる。Snapshot の payload バイト列と算出関数（例 SHA-256 hex）を両側で厳密一致させないと SaveSnapshot が常に拒否される。フィクスチャで固定してテスト。
6. **InventoryOperation は proto enum が正**。wire は MOVE/SPLIT/MERGE/DROP/USE。ADD/REMOVE 相当（採掘付与・消費）は外部 Command ではなく DS 内部確定＋Event 化で表現。
7. **event_tail の適用順**。必ず `sequence` 昇順で適用。順序を崩すと復元が壊れる。
8. **gRPC C# ランタイム未導入**。buf の grpc/csharp 生成物は stub のみ。実行には Grpc.Net.Client（または Grpc.Core）を NuGetForUnity で導入する（W2-2）。未導入だと接続時に型解決/実行で失敗。
9. **`services/`・proto・`Makefile`・`*.sh` を触らない**。A は `unity/` と `*.ps1` のみ。生成物 `Assets/Generated/` は手編集しない（B が再生成で上書き）。
10. **Ready 前に Join を受けない**。Bootstrap 完了（復元＋tail 適用）まで Heartbeat の `ready=false` を維持（MVP 6.2 / 12.1）。
11. **購入付与を Outbox で再送しない**。購入の永続は API が確定済み。DS は runtime に映すだけ（MVP 12.2.1・二重永続化防止）。

---

## 参考資料

- MVP詳細設計 v0.2.2: 5.3/5.4（Client クラス・ローカルキャッシュ）, 5.5（R3/VContainer/UniTask）, 6.1〜6.3（サーバーループ・権限）, 7章（Inventory/Item Definition/Command）, 12.1（World Bootstrap/Save）, 12.2.1（単一Writer原則）, 14.1（InventoryCommand/RequestInventorySnapshot）, 16章（復旧）, 18章（AT-002/003/018/021）, 付録C（データ所有権）。
- 基本設計 v0.2.1: 6.1（共通Inventory・単一Writer）, 9.2（保存単位と復旧）, 9.4（データ所有権マトリクス）。
- proto: `unity/SurvivalWorld/Assets/Generated/`（`gameplay`/`worlddata`/`common` の C# 生成物。B が生成）。
- M0: `03A_M0実装指示書_Windows側_v0.1.md`（BuildScript / ps1 / 基盤ライブラリ / gRPC 生成物受け皿）。
- [R4] FishNet overview / [R6] FishNet Authenticator。
