---
title: "Story_0008 MainMenu UI 実装指示書（Windows ネイティブ側）"
subtitle: "簡易 Account/Login/Character/Matchmaking UI と手動・PlayMode 検証導線"
document_id: "IMPL-STORY-0008-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-18"
status: "実装指示（Story_0008 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / Input System / R3 / VContainer / UniTask"
related_document: "02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md, 04A_M1実装指示書_Windows側_v0.1.md, 04A-1_Story_0001a_DevLocalMode実装指示書_Windows側_v0.1.md, 11A_M8A_PlaytestInteractions実装指示書_Windows側_v0.1.md"
language: "ja"
---

# Story_0008 MainMenu UI 実装指示書：Windows ネイティブ側 v0.1

本書は、MVP 詳細設計書 v0.2.2 の `MainMenu` 要件を、まず **簡易に手動テストできる UI** として実装するための Windows/Unity 側指示である。

対象は「アカウント作成・ログイン・単一キャラクター選択・マッチメイク Join・World_MVP 接続」の最小縦切りに限定する。フル機能のキャラクター管理、Refresh Token の OS Credential Store 永続化、見た目の最終 polish、VContainer 全面移行は本 Story の範囲外とする。

---

## 0. 現状判定

### 0.1 実装済みと見なせるもの

- `Bootstrapper` は `AuthClient` / `DevLocalAuthClient` を生成し、`LoginJoinAndConnectAsync` で Login -> Matchmaking Join -> FishNet 接続を実行できる。
- `AuthClient` は `CreateAccountAsync` / `LoginAsync` / `RefreshAsync` / `JoinMatchmakingAsync` を持つ。
- `MatchmakingJoinFlow` は Join 401 時に Refresh を 1 回試行できる。
- `NetworkSessionClient` は Join Ticket を使って `World_MVP` をロードし、FishNet 接続と認証完了を待てる。
- `EditorBuildSettings` には `Bootstrap.unity` と `World_MVP.unity` が登録済み。

### 0.2 未完了と見なすもの

- 設計書で要求される `MainMenu` シーンが存在しない。
- Account/Login/Character 選択/Matchmaking を操作する UI がない。
- 自動接続以外の起動導線がなく、プレイテスターが画面からログイン・接続を検証できない。
- UI 状態、入力検証、エラー表示、接続中のボタン無効化を検証するテストがない。

---

## 1. 対象とゴール

### 1.1 対象

Windows 側は `unity/` と必要な `scripts/*.ps1` のみを編集する。`services/`, `proto/`, `infra/`, `scripts/*.sh`, `Makefile`, `Assets/Generated/` の proto C# は触らない。

主対象:

```text
unity/SurvivalWorld/Assets/Scenes/
├─ Bootstrap.unity
├─ MainMenu.unity                  # 新規
└─ World_MVP.unity

unity/SurvivalWorld/Assets/Scripts/
├─ Bootstrap/
│  └─ Bootstrapper.cs              # MainMenu 起動導線と public flow を追加
├─ Client/
│  └─ UI/
│     ├─ MainMenuController.cs     # 新規
│     ├─ MainMenuSessionFlow.cs    # 新規、テスト用に flow を薄く抽象化
│     └─ MainMenuValidation.cs     # 新規または Controller 内 private helper
└─ Tests/
   ├─ EditMode/
   │  └─ MainMenuValidationTests.cs
   └─ PlayMode/
      └─ MainMenuScenePlayModeTests.cs
```

### 1.2 Story_0008 DoD

- [ ] `Assets/Scenes/MainMenu.unity` が存在し、Build Settings で `Bootstrap -> MainMenu -> World_MVP` の順に登録されている。
- [ ] interactive client 起動時、auto connect が無効なら `Bootstrap` から `MainMenu` へ遷移する。batchmode DS と `--sw-auto-connect` は既存挙動を維持する。
- [ ] MainMenu から Login -> Matchmaking Join -> `World_MVP` 接続を実行できる。
- [ ] DevLocalMode では「Quick Connect」ボタンで既存 dev account / dev character を使って最短接続できる。
- [ ] Account 作成 UI は email/password/display name を受け取り、作成成功後に login/join/connect へ進む。409 は「既に存在」と表示し、Login を促す。
- [ ] MVP の Character 選択は単一 character id 入力でよい。既定値は `SurvivalRuntimeConfig.DevCharacterId` または Bootstrapper の default character id を表示する。
- [ ] 接続中は主要ボタンを無効化し、`Idle / Creating / LoggingIn / Joining / Connecting / Connected / Error` を UI に表示する。
- [ ] password / access token / refresh token / join ticket を UI status、Debug.Log、例外 message に出さない。
- [ ] `scripts\unity_test.ps1` が緑。少なくとも EditMode で入力検証、PlayMode で MainMenu scene と主要 UI 要素の存在を検証する。

---

## 2. 実装方針

### 2.1 Bootstrap 起動フロー

`Bootstrapper` に `mainMenuSceneName = "MainMenu"` を追加する。

既存の自動接続は残す:

- `Application.isBatchMode` は DS 起動のため MainMenu を開かない。
- `AutoConnectLocalClientInEditor` または `--sw-auto-connect` が有効なら、既存の `StartAutomaticConnectFlowAsync` をそのまま実行する。
- 上記以外の interactive client では `MainMenu` をロードする。

`Bootstrapper` / `NetworkSessionClient` / `NetworkManager` が scene load で消えないよう、Bootstrap scene 側の runtime root を `DontDestroyOnLoad` 対象にする。重複起動を避けるため static instance guard を入れる。

実装例の責務:

```csharp
private static Bootstrapper instance;

private void Awake()
{
    if (instance != null && instance != this)
    {
        Destroy(gameObject);
        return;
    }

    instance = this;
    DontDestroyOnLoad(transform.root.gameObject);
    ...
}
```

注意:

- Server batchmode でも既存 server bootstrap を壊さない。`ShouldRunServerBootstrap()` 側の挙動には手を入れない。
- `MainMenu` 直起動テストもできるよう、`MainMenuController` は `FindFirstObjectByType<Bootstrapper>()` が null の場合に分かりやすい error state を出す。
- `NetworkSessionClient.ConnectWithJoinTicketAsync` は最終的に `World_MVP` を `LoadSceneMode.Single` でロードする。MainMenu の UI は接続成功時に破棄されてよい。

### 2.2 Bootstrapper の public flow

MainMenu から使うため、`Bootstrapper` に次の public API を用意する。

```csharp
public UniTask LoginJoinAndConnectAsync(string email, string password, string characterId, CancellationToken cancellationToken)
public UniTask CreateAccountLoginJoinAndConnectAsync(string email, string password, string displayName, string characterId, CancellationToken cancellationToken)
public UniTask DevQuickConnectAsync(CancellationToken cancellationToken)
```

既存の `LoginJoinAndConnectAsync(string email, string password, CancellationToken)` は互換維持のため残し、character id を省略した overload として扱う。

実装上の注意:

- character id が空なら既存の解決規則を使う。
- DevLocalMode では `DevLocalAuthClient` を使う既存設計を維持する。
- account 作成時の 409 は UI へ返す。自動 smoke 用の既存 private flow のように黙って握りつぶさない。ただし `DevQuickConnectAsync` では既存 dev account を前提にしてよい。

### 2.3 MainMenu UI

`MainMenu.unity` は uGUI で構成する。TextMeshPro への移行は不要。既存 UI と同じ `UnityEngine.UI.Text / InputField / Button` を使う。

最小レイアウト:

- Title: `Living World Survival`
- Connection mode:
  - `Dev Quick Connect`
  - `Login`
  - `Create Account`
- Fields:
  - Email
  - Password
  - Display Name（Create Account 時のみ有効）
  - Character Id
- Buttons:
  - `Quick Connect`
  - `Login`
  - `Create + Connect`
  - `Quit`（Editor では play mode 停止不要。Application.Quit のみでよい）
- Status text:
  - 現在状態と短い error summary のみ
  - password/token/ticket は絶対に表示しない

入力初期値:

- DevLocalMode では `dev@example.local` / `dev-password` / `Dev Player` / `config.DevCharacterId` を Editor/Development build のみで入れてよい。
- 非 DevLocalMode では password は空にする。

### 2.4 MainMenuController

`MainMenuController` は MonoBehaviour とし、UI event を session flow に委譲する。

推奨構造:

```csharp
public sealed class MainMenuController : MonoBehaviour
{
    [SerializeField] private InputField emailInput;
    [SerializeField] private InputField passwordInput;
    [SerializeField] private InputField displayNameInput;
    [SerializeField] private InputField characterIdInput;
    [SerializeField] private Button quickConnectButton;
    [SerializeField] private Button loginButton;
    [SerializeField] private Button createAccountButton;
    [SerializeField] private Text statusText;

    private IMainMenuSessionFlow sessionFlow;
    private CancellationTokenSource actionCts;
}
```

状態管理:

```text
Idle -> Creating -> LoggingIn -> Joining -> Connecting -> Connected
Idle -> LoggingIn -> Joining -> Connecting -> Connected
any -> Error -> Idle
```

実装ルール:

- ボタン押下中は全 submit ボタンを無効化する。
- `OnDestroy` で `actionCts.Cancel()` する。
- `AuthClientException` は status code と一般化された内容だけ出す。response body に token/password が混ざる可能性があるため全文表示しない。
- `OperationCanceledException` は error 扱いにせず Idle に戻す。
- 成功時は `Connected` を表示する。直後に `World_MVP` へ遷移するため、表示が短くてもよい。

### 2.5 MainMenuSessionFlow

テスト容易性のため、UI から `Bootstrapper` を直接叩く部分を薄く抽象化する。

```csharp
public interface IMainMenuSessionFlow
{
    UniTask QuickConnectAsync(CancellationToken cancellationToken);
    UniTask LoginConnectAsync(string email, string password, string characterId, CancellationToken cancellationToken);
    UniTask CreateAccountConnectAsync(string email, string password, string displayName, string characterId, CancellationToken cancellationToken);
}
```

本番実装 `BootstrapMainMenuSessionFlow` は `Bootstrapper` を受け取り、上記 public flow を呼ぶだけにする。

テストでは fake flow を差し替え、成功・失敗・遅延中ボタン無効化を検証する。

### 2.6 入力検証

`MainMenuValidation` は static helper でもよい。

最小ルール:

- email は空不可、`@` を含むこと。
- password は空不可。最小長は backend 側に合わせる。現時点で不明なら 8 文字以上にする。
- display name は Create Account 時のみ空不可。
- character id は空なら default を使う。入力された場合は GUID 形式を要求する。

エラー文言は UI 用に短くする:

- `Email is required.`
- `Password is required.`
- `Character id must be a GUID.`
- `Login failed. Check credentials or backend status.`
- `Connection failed. Check server endpoint and DS logs.`

---

## 3. シーン作成指示

### 3.1 MainMenu.unity

作成:

```text
unity/SurvivalWorld/Assets/Scenes/MainMenu.unity
```

必須 GameObject:

- `MainMenuCanvas`
  - `Canvas`
  - `CanvasScaler`
  - `GraphicRaycaster`
- `MainMenuPanel`
  - `MainMenuController`
  - 入力欄、ボタン、status text
- `EventSystem`

表示確認のため、背景は単色または World_MVP と同系の低彩度色でよい。初回実装では画像素材不要。

### 3.2 EditorBuildSettings

`Assets/Scenes/MainMenu.unity` を追加し、順序を次にする。

```text
0: Assets/Scenes/Bootstrap.unity
1: Assets/Scenes/MainMenu.unity
2: Assets/Scenes/World_MVP.unity
```

PlayMode テストで Build Settings を検査する。

### 3.3 Bootstrap.unity

Bootstrap runtime root に以下が同居していることを確認する。

- `Bootstrapper`
- `NetworkSessionClient`
- `NetworkManager`
- `JoinTicketAuthenticator`
- `SurvivalRuntimeConfig` 参照

MainMenuController は MainMenu scene 側に置く。Bootstrap scene に UI は置かない。

---

## 4. QA Test Cases

### 4.1 EditMode

`Assets/Tests/EditMode/MainMenuValidationTests.cs`

- **AC-1: email/password の空欄を拒否する**
  - Given: empty email または empty password
  - When: validation を実行する
  - Then: false と UI 表示用 error code/message が返る

- **AC-2: character id は空なら default 許可、入力ありなら GUID のみ許可**
  - Given: empty / valid GUID / invalid text
  - When: validation を実行する
  - Then: empty と valid GUID は通り、invalid text は拒否される

- **AC-3: secret を表示文言へ含めない**
  - Given: password/token/ticket らしい文字列を含む例外
  - When: UI error message へ変換する
  - Then: 出力文字列に元 password/token/ticket が含まれない

### 4.2 PlayMode

`Assets/Tests/PlayMode/MainMenuScenePlayModeTests.cs`

- **AC-4: MainMenu scene がロードできる**
  - Given: `Assets/Scenes/MainMenu.unity`
  - When: PlayMode で scene をロードする
  - Then: `MainMenuController`, email/password/character input, Quick/Login/Create buttons, status text が存在する

- **AC-5: submit 中はボタンが無効化される**
  - Given: fake session flow が完了しない状態
  - When: Login button を押す
  - Then: Quick/Login/Create buttons が disabled になり、status が `LoggingIn` または `Connecting` 系になる

- **AC-6: fake flow 成功で Connected を表示する**
  - Given: fake session flow が成功する
  - When: Quick Connect を押す
  - Then: status に `Connected` が表示される

- **AC-7: fake flow 失敗で安全な Error を表示する**
  - Given: fake session flow が例外を返す
  - When: Login button を押す
  - Then: status に一般化された error が表示され、password/token/ticket は含まれず、ボタンが再度 enabled になる

---

## 5. 手動スモーク

### 5.1 DevLocalMode 最小確認

1. Unity Editor で `Bootstrap.unity` を開く。
2. `SurvivalRuntimeConfig.DevLocalMode = true`、`AutoConnectLocalClientInEditor = false` にする。
3. Play を押す。
4. `MainMenu` が表示されることを確認する。
5. `Quick Connect` を押す。
6. `World_MVP` がロードされ、FishNet client authentication が成功することを Console で確認する。

期待結果:

- MainMenu 上に password/token/ticket が表示されない。
- 接続中はボタンが連打できない。
- 接続成功後、World_MVP で player 操作が可能。

### 5.2 Real backend 最小確認

前提: Auth/API/WorldData/Economy/NATS/PostgreSQL と DS が起動済み。

1. `DevLocalMode = false`、`AutoConnectLocalClientInEditor = false` にする。
2. `Bootstrap.unity` から Play。
3. email/password/character id を入力して `Login`。
4. 未作成アカウントの場合は display name を入れて `Create + Connect`。
5. `World_MVP` へ接続できることを確認する。

期待結果:

- Login 失敗時、HTTP status は分かるが response body 全文や secret は出ない。
- Join 401 時は既存 `MatchmakingJoinFlow` により Refresh retry が 1 回行われる。
- 接続失敗時は DS endpoint / DS log 確認を促す短いエラーを表示する。

---

## 6. 受入条件

- [ ] 設計書の `MainMenu | Account/Login、Character選択、Matchmaking` を満たす最小 UI がある。
- [ ] `Bootstrap` 起動から MainMenu 表示、MainMenu から `World_MVP` 接続まで手動確認できる。
- [ ] 既存の `--sw-auto-connect` と batchmode DS 起動が壊れていない。
- [ ] UI で secret を表示・ログ出力しない。
- [ ] `scripts\unity_test.ps1` が成功する。
- [ ] 証跡を `docs/prompts/story_0008/win/unity/story_0008-mainmenu-smoke-result.txt` に残す。

---

## 7. Out of Scope

- Refresh Token の OS Credential Store 永続化。
- 複数キャラクター一覧、作成、削除、外見選択。
- 本格的な設定画面、音量/解像度/キーコンフィグ。
- VContainer の全面導入。新規コードは後で DI 化しやすい constructor/interface 境界にするが、既存 Bootstrap 全体の移行は別 Story とする。
- UI の最終 art polish。今回はプレイテスト可能性を優先する。

---

## 8. 実装順序

1. `MainMenuValidation` と EditMode test を先に作る。
2. `Bootstrapper` に MainMenu 起動導線と public flow を追加する。
3. `MainMenuSessionFlow` / `MainMenuController` を実装する。
4. `MainMenu.unity` を作成し、Controller に UI 参照を接続する。
5. `EditorBuildSettings` に MainMenu を追加する。
6. PlayMode test を追加する。
7. DevLocalMode 手動スモークを実行し、結果ファイルへ記録する。
8. Real backend が起動できる環境なら Real backend smoke も実行する。未実行の場合は結果ファイルに理由を書く。

---

## 9. 完了時の報告フォーマット

実装完了時は次を報告する。

```text
Story_0008 MainMenu UI

変更:
- MainMenu scene / UI
- Bootstrapper 起動導線
- MainMenuController / SessionFlow / Validation
- EditMode / PlayMode tests

検証:
- scripts\unity_test.ps1: PASS/FAIL
- DevLocalMode manual smoke: PASS/FAIL
- Real backend smoke: PASS/FAIL/未実行（理由）

既知の残:
- OS Credential Store refresh token persistence は未実装
- 複数キャラクター選択は未実装
- VContainer 全面移行は未実装
```
