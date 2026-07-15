---
title: "Story 0002a 実装指示書（Windows ネイティブ側）"
subtitle: "Assets/Models 配下のキャラクターモデルを PlayerCharacter に設定する"
document_id: "IMPL-M2A-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-15"
status: "実装指示（Story 0002a / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / Input System / R3 / UniTask"
related_document: "04A_M1実装指示書_Windows側_v0.1.md, 04A-1_Story_0001a_DevLocalMode実装指示書_Windows側_v0.1.md, 05A_M2実装指示書_Windows側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# Story 0002a 実装指示書：Character Model 設定（Windows Unity） v0.1

本書は **Story 0002a** として、M1 完了後の `PlayerCharacter` に `Assets/Models` 配下の既存キャラクターモデルを設定する作業を指示する。対象は **Windows ネイティブ側 / Unity Runtime** のみ。バックエンド、proto、DB、WSL2 生成アセットパイプラインは対象外。

Story 0002 本体（M2: Inventory / Save）に入る前に、2 Client 接続済みのプレイヤーがプロトタイプ形状ではなく実キャラクターモデルで表示される状態にする。

---

## 0. 位置づけ

### 0.1 なぜ Story 0002a を追加するか

- Story 0002 以降の既存指示書には、`PlayerCharacter` の見た目を `Assets/Models` のキャラクターモデルへ差し替える専用ストーリーがない。
- Story 0007 には Manifest Import Processor / Client Prefab / Server Prefab 生成があるが、これは Hardening 段階の自動生成パイプラインであり、M1/M2 直後に既存 FBX を PlayerCharacter に割り当てる作業とは粒度が違う。
- M2/M3 以降の Inventory / Survival / Interaction 検証では、画面上で自 Character / 他 Character を識別しやすいことが必要になる。

### 0.2 担当範囲

| 領域 | 担当 | 備考 |
|---|---|---|
| `PlayerCharacter.prefab` の見た目設定 | Windows Unity | 本書対象 |
| `Assets/Models/passive_marker_man.fbx` の import / material 確認 | Windows Unity | 本書対象 |
| Animator / Avatar の最低限確認 | Windows Unity | 本書対象 |
| FishNet 接続 / Join Ticket / Server authority movement | 既存 M1 | 壊さない |
| Inventory / Save / WorldData | Story 0002 | 本書では実装しない |
| Manifest Import Processor | Story 0007 | 本書では実装しない |

---

## 1. 対象と前提

### 1.1 対象アセット

既存アセットを使用する。

```text
unity/SurvivalWorld/Assets/Models/passive_marker_man.fbx
unity/SurvivalWorld/Assets/Models/Materials/Body_MAT.mat
unity/SurvivalWorld/Assets/Models/Materials/Brows_MAT.mat
unity/SurvivalWorld/Assets/Models/Materials/Eyes_MAT.mat
unity/SurvivalWorld/Assets/Models/Materials/Reflectors.mat
unity/SurvivalWorld/Assets/Models/Materials/MocapSuit02_diffuse.png
```

`passive_marker_man.fbx.meta` の現状メモ:

- `animationType: 2`（Humanoid）
- `avatarSetup: 0`（Create From This Model）
- Material は `Assets/Models/Materials` の外部 Material に紐付け済み

### 1.2 対象 Prefab

```text
unity/SurvivalWorld/Assets/Prefabs/PlayerCharacter.prefab
```

この Prefab は M1 のネットワーク移動の中核なので、次を維持する。

- ルート GameObject 名: `PlayerCharacter`
- `FishNet.Object.NetworkObject`
- `CharacterController`
- `ThirdPersonInputReader`
- `NetworkPlayerController`
- NetworkManager の spawnable prefab 登録
- `ServerBootstrap.playerPrefab` 参照

---

## 2. DoD

Story 0002a は次を満たしたら完了とする。

- `PlayerCharacter.prefab` の子に `passive_marker_man.fbx` 由来の見た目が配置され、PlayMode / build client の両方で表示される。
- `NetworkObject` / `CharacterController` / `NetworkPlayerController` はルートに残り、FishNet spawn / ownership / Server authority movement が維持される。
- モデルの足元が CharacterController の接地点と大きくズレない。原点・高さ・回転・スケールを調整し、カメラが自然に追従する。
- 2 Client 接続時に、自分と相手のモデルが表示され、少なくとも移動方向の回転または Transform 同期が破綻しない。
- Linux DS ビルドでは描画モデルがサーバーロジックを阻害しない。DS で不要な Camera/UI/Animator 更新を増やさない。
- EditMode / PlayMode テストが通る。少なくとも M1 の接続・移動テストを壊さない。

---

## 3. 実装方針

### 3.1 Prefab 構造

`PlayerCharacter.prefab` のルートはネットワーク・当たり判定・入力の責務を持つ。モデルは必ず子に置く。

推奨構造:

```text
PlayerCharacter
├─ VisualRoot
│  └─ passive_marker_man   # FBX prefab instance または展開済みモデル
└─ Runtime components      # NetworkObject / CharacterController / Input / Controller は root 側
```

禁止:

- FBX の root を `PlayerCharacter` ルートにして `NetworkObject` を移す。
- `CharacterController` をモデル子へ移す。
- `NetworkPlayerController` の owner 判定を見た目設定のために変更する。
- Server authority movement を Animator root motion に置き換える。

### 3.2 モデル配置調整

`VisualRoot` で調整する。

- Rotation: Unity の前方がモデル前方になるようにする。
- Scale: 現在の `CharacterController.height` / `radius` と整合するサイズにする。
- Position: 足元が CharacterController の底面付近に来るようにする。
- Pivot: ルート移動は `PlayerCharacter` の Transform が担当し、モデル側 Pivot ずれは `VisualRoot` の localPosition で吸収する。

受入目安:

- 地面に埋まらない。
- 足が大きく浮かない。
- カメラ追従時に頭部・胴体が画面中央から大きく外れない。
- 2 Client で互いのモデル位置がネットワーク Transform と一致する。

### 3.3 Animator / Animation

この Story ではフルアニメーション制御は必須にしない。最低限、次を確認する。

- FBX の Avatar が valid であること。
- Animator を付ける場合は、Idle など最小状態のみでよい。
- 移動ロジックは `NetworkPlayerController` の Transform / CharacterController 移動を正とし、root motion は無効にする。

将来の走行・攻撃・採取・被弾 Animation は別 Story で扱う。

### 3.4 Material / URP 表示

- `Assets/Models/Materials` の Material が欠落・ピンク表示にならないこと。
- URP で表示可能な Shader を使う。必要なら Material を URP Lit へ変換する。
- Texture `MocapSuit02_diffuse` が正しく割り当たること。

### 3.5 DS / Server build への配慮

Linux DS は描画を必要としない。M2a では大きな DS 最適化は不要だが、次は守る。

- DS の移動・認証・スポーンにモデル追加が影響しないこと。
- Animator を使う場合でも、サーバー権威の移動処理を Animator 依存にしないこと。
- DS build が通ること。

---

## 4. 実装順序

| # | タスク | 完了確認 |
|---|---|---|
| W2a-1 | `passive_marker_man.fbx` の Import 設定を確認 | Humanoid / Avatar valid / Material 欠落なし |
| W2a-2 | `PlayerCharacter.prefab` に `VisualRoot` を追加 | root components は維持 |
| W2a-3 | `VisualRoot` 配下にモデルを配置 | local position / rotation / scale が妥当 |
| W2a-4 | Material / Texture 表示を確認 | Scene / Game view でピンク表示なし |
| W2a-5 | PlayMode で単体スポーン確認 | モデル表示、地面埋まりなし、カメラ追従 OK |
| W2a-6 | 2 Client 接続確認 | 自分/相手がモデル表示、移動同期を維持 |
| W2a-7 | EditMode / PlayMode テスト | 既存テスト緑 |
| W2a-8 | Windows Client / Linux DS ビルド | Build 成功 |

---

## 5. テスト / 受入

### 5.1 Unity Editor 手動確認

1. `Bootstrap.unity` を開く。
2. PlayMode で接続フローを実行する。
3. `World_MVP` 遷移後、自 Character が `passive_marker_man` の見た目で表示される。
4. WASD 移動でモデルがルート Transform と一緒に動く。
5. カメラ追従が破綻しない。

### 5.2 2 Client 確認

M1 / Story 0001a と同じ実バックエンド + DS 構成で確認する。

- Client 1 / Client 2 が同一 DS に接続する。
- 2体とも `passive_marker_man` の見た目で表示される。
- Client 1 の移動が Client 2 側に見える。
- Client 2 の移動が Client 1 側に見える。
- 所有外 Character を操作できない。

### 5.3 自動テスト

- `scripts/unity_test.ps1` の EditMode / PlayMode を実行し、既存 M1 テストを壊していないこと。
- 可能なら PlayMode に `PlayerCharacter.prefab` の構造テストを追加する。
  - root に `NetworkObject` がある。
  - root に `CharacterController` がある。
  - root に `NetworkPlayerController` がある。
  - 子に `VisualRoot` がある。
  - `VisualRoot` 配下に Renderer または SkinnedMeshRenderer が存在する。

### 5.4 ビルド確認

- Windows Client build が成功する。
- Linux Dedicated Server build が成功する。
- DS 起動時にモデル追加起因の例外が出ない。

---

## 6. 落とし穴

- **Prefab root を差し替えない**。FishNet の NetworkObject / spawnable prefab 参照が壊れる。
- **CharacterController とモデルの位置を混同しない**。当たり判定は root、見た目は `VisualRoot`。
- **root motion を有効化しない**。M1 の Server authority movement と競合する。
- **Animator をサーバーロジックの前提にしない**。DS は描画・演出に依存しない。
- **Material のピンク表示を見逃さない**。URP Shader / texture 参照を確認する。
- **M7 の Import Processor と混同しない**。本 Story は既存 `Assets/Models` の手動/半自動設定。将来の大量アセット決定的生成は M7。
- **`Assets/Generated/` へ置かない**。このモデルは既存 `Assets/Models` と `Assets/Prefabs` の範囲で扱う。

---

## 7. 変更対象ファイル候補

主対象:

```text
unity/SurvivalWorld/Assets/Prefabs/PlayerCharacter.prefab
unity/SurvivalWorld/Assets/Models/passive_marker_man.fbx.meta
unity/SurvivalWorld/Assets/Models/Materials/*.mat
```

必要なら追加:

```text
unity/SurvivalWorld/Assets/Tests/PlayMode/PlayerCharacterVisualPrefabTests.cs
unity/SurvivalWorld/Assets/Animations/Player/
```

ただし、Animation Controller の本格実装は本 Story では必須ではない。

---

## 8. 完了報告に含めること

- 使用したモデル: `Assets/Models/passive_marker_man.fbx`
- Prefab 構造の要約: `PlayerCharacter/VisualRoot/...`
- 調整した local position / rotation / scale
- Material / Texture 欠落がないこと
- 2 Client 接続で両者のモデル表示・移動同期が維持されたこと
- EditMode / PlayMode / Client build / Server build の結果

---

## 参考資料

- `docs/prompts/story_0001/win/unity/04A_M1実装指示書_Windows側_v0.1.md`
- `docs/prompts/story_0001a/win/unity/04A-1_Story_0001a_DevLocalMode実装指示書_Windows側_v0.1.md`
- `docs/prompts/story_0002/win/unity/05A_M2実装指示書_Windows側_v0.1.md`
- `docs/02_MVP詳細設計書_v0.2.2.md` 5.3 / 6.3
- `unity/SurvivalWorld/Assets/Prefabs/PlayerCharacter.prefab`
- `unity/SurvivalWorld/Assets/Models/passive_marker_man.fbx`