---
title: "M0 実装指示書（Windows ネイティブ側）"
subtitle: "Unity Client / Dedicated Server ビルド・テスト・クライアント基盤"
document_id: "IMPL-M0-WIN-001"
document_type: "implementation_instruction"
version: "0.1"
issued_at: "2026-07-12"
status: "実装指示（M0 / Windows側）"
baseline: "Unity 6000.5.x / URP / FishNet / R3 / VContainer / UniTask"
related_document: "03B_M0実装指示書_WSL2側_v0.1.md, 02_MVP詳細設計書_v0.2.2.md, 01_基本設計書_v0.2.1.md"
language: "ja"
---

# M0 実装指示書：Windows ネイティブ側 v0.1

本書は M0（基盤）の作業を **Windows ネイティブ側**（Unity Editor と Unity ビルド、クライアント基盤ライブラリ）に限定して指示する。バックエンド（Go/Python/Docker/CI）は別冊 **03B（WSL2側）** を参照。第0章「分担と連携」は両冊で共通・同一内容。

---

## 0. 分担と連携（共通・両冊同一）

### 0.1 環境別 責務分担

| 領域 | 担当環境 | 主なタスク |
|---|---|---|
| Unity Editor / Client ビルド | **Windows** | プロジェクト設定、クライアント基盤ライブラリ導入、Windows Client ビルド |
| Unity Dedicated Server ビルド | **Windows** | Linux 向けクロスビルド（`-standaloneBuildSubtarget Server`） |
| Unity テスト（EditMode/PlayMode） | **Windows** | `-runTests` をローカル実行 |
| proto の C# 生成物の取り込み | **Windows**（消費） | `unity/SurvivalWorld/Assets/Generated/` を参照しコンパイル |
| Go サービス（auth/api） | **WSL2** | 実装・Lint・テスト・Dockerビルド |
| Python サービス（worldstate/llm-worker） | **WSL2** | 実装・Lint・テスト |
| proto 生成（buf） | **WSL2**（生成） | Go/Python/C# を生成し出力 |
| Docker Compose / DB / NATS | **WSL2** | インフラ起動、マイグレーション、smoke |
| ローカルCI（`make ci`） | **WSL2** | Go/Python/proto/assets を一括実行 |
| Blender アセット生成（headless） | **WSL2**（既定） | `make assets`。Windows Blender でも可（0.5） |
| Git / LFS | **両方** | 下記 0.3 の規約に従う |

### 0.2 リポジトリ配置（既定）

- **単一クローンを Windows ファイルシステムに置く**。例: `C:\dev\living-world-survival`
- **WSL2 からは `/mnt/c/dev/living-world-survival` で同じクローンにアクセス**する。
- 理由: Unity は I/O に最も敏感でネイティブ配置が必須。WSL2内配置＋Unity `\\wsl$` 参照は低速なため採らない。
- 代替（任意）: どうしてもバックエンドI/Oを優先する場合のみ WSL2内クローン＋Unityを別管理にする分割案があるが、M0では非推奨。

> 単一リポジトリを2環境から触るため、改行コードとLFSの規約（0.3）を厳守する。

### 0.3 Git / LFS / 改行コード規約

- Git と **Git LFS を両環境にインストール**し、各自 `git lfs install` を一度実行。
- Windows 側は `git config --global core.autocrlf false`（改行は .gitattributes で制御）。
- リポジトリ直下 `.gitattributes`（**shは必ずLF、ps1はCRLF**）:

```gitattributes
* text=auto eol=lf
*.sh   text eol=lf
*.ps1  text eol=crlf
*.cs   text
*.meta text eol=lf
# Unity/バイナリ資産は LFS
*.png filter=lfs diff=lfs merge=lfs -text
*.jpg filter=lfs diff=lfs merge=lfs -text
*.fbx filter=lfs diff=lfs merge=lfs -text
*.glb filter=lfs diff=lfs merge=lfs -text
*.wav filter=lfs diff=lfs merge=lfs -text
```

- `.sh` が CRLF になると WSL2 で実行不可になるため、上記を最初のコミットに含める。

### 0.4 境界成果物（環境をまたぐファイル）

| 成果物 | 生成側 | 消費側 | 置き場所 |
|---|---|---|---|
| proto → C# 生成コード | WSL2（`buf generate`） | Windows（Unity） | `unity/SurvivalWorld/Assets/Generated/` |
| proto → Go/Python 生成コード | WSL2 | WSL2 | 各サービス配下 |
| Linux Dedicated Server ビルド成果物 | Windows（クロスビルド） | WSL2/Docker（実行） | `unity/SurvivalWorld/Build/Server/` |
| DBスキーマ（migrations） | WSL2 | 参照: 両方 | `services/*/migrations/` |

### 0.5 連携フロー（代表例）

- **proto を変更したとき**: WSL2で `make proto`（`buf generate`）→ `unity/SurvivalWorld/Assets/Generated/` にC#が出力 → Windowsの Unity がコンパイル。生成物のコミット漏れは WSL2 の CI が検出。
- **サーバー起動確認**: Windowsで `unity-build-server` → 生成した Linux バイナリを WSL2/Docker 上で実行（M1以降）。
- **日次**: Windows=Unityの作業、WSL2=`make ci`。両者は同一リポジトリを push/pull で同期。

---

## 1. 対象と前提（Windows側）

- OS: Windows。Unity Editor はネイティブ動作。
- 既存状態: **MCP 導入済みの空 URP プロジェクト**。プロジェクトルートは **`living-world-survival/unity/SurvivalWorld/`**（`Assets/` `Packages/` `ProjectSettings/` はこの直下）。
- 本書の完了で、クライアント基盤ライブラリが入りコンパイルが通り、Client/Server のローカルビルドと Unity テストが実行できる状態にする。

### 1.1 Windows側 DoD

- Unity プロジェクトがエラーなく開き、コンパイルが通る（R3/VContainer/UniTask/FishNet/Input System 導入済み）。
- `scripts\unity_test.ps1` で EditMode/PlayMode テストが実行できる。
- `scripts\unity_build_client.ps1`（Windows Client）と `scripts\unity_build_server.ps1`（Linux Dedicated Server）でビルド成果物が生成される。
- `Packages/manifest.json` / `packages-lock.json` / `ProjectVersion.txt` がコミットされ版が固定されている。
- `unity/SurvivalWorld/Assets/Generated/` の C# proto 生成物を参照してコンパイルできる（WSL2が生成、空でも可）。

---

## 2. 必要ツール（Windows）

| ツール | 目安 | 用途 |
|---|---|---|
| Unity Hub / Editor | **6000.5.x** | Client/Server |
| Git for Windows + Git LFS | 最新 | バージョン管理・LFS |
| PowerShell | 5.1+ / 7+ | ビルド・テストスクリプト |
| （任意）Blender | 4.x | Windowsでアセット生成する場合（既定はWSL2） |

### 2.1 Unity Editor 必須モジュール

Unity Hub で 6000.5.x に次を追加する。

- **Windows Build Support (IL2CPP)** … Windows Client 用（通常同梱）
- **Linux Dedicated Server Build Support** … Linux Dedicated Server クロスビルド用（**必須**）
- （任意）Linux Build Support (IL2CPP/Mono)

> これで Windows の Unity から Linux 向け Dedicated Server をビルドできる。別途 Linux マシンは不要（実行は WSL2/Docker）。

---

## 3. Unity プロジェクト整備

### 3.1 .gitignore（Unity 部分）

`unity/SurvivalWorld/.gitignore`（要点）:

```gitignore
Library/
Temp/
Obj/
Build/
Logs/
UserSettings/
*.csproj
*.sln
```

- リポジトリ直下の `.gitattributes`（0.3）で `.meta` をテキスト追跡、バイナリ資産をLFS化する。
- `Packages/` と `ProjectSettings/` は**コミットする**（版固定のため）。

### 3.2 バージョン固定

- `ProjectSettings/ProjectVersion.txt` の Unity パッチ版をコミット。
- `Packages/packages-lock.json` をコミットし、UPM 依存の版を固定。

---

## 4. クライアント基盤ライブラリ導入

**前提**: R3 コアは NuGet 配布のため **NuGetForUnity を先に入れる**。導入順を守る（順番を誤ると R3 がコンパイルエラーになる）。

```text
1) NuGetForUnity  (Package Manager > Add package from git URL)
   https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity

2) R3 コア     … NuGetForUnity ウィンドウで "R3"（publisher: Cysharp）を検索し Install
   R3.Unity    … UPM git URL
   https://github.com/Cysharp/R3.git?path=src/R3.Unity/Assets/R3.Unity#<version>

3) VContainer  … UPM git URL
   https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#<version>

4) UniTask     … UPM git URL
   https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#<version>

5) FishNet     … Asset Store / UPM（版固定）
6) Input System… Package Manager（有効化時にエディタ再起動）
```

注意:

- 各パッケージは `#<version>` でピン留めし、`Packages/manifest.json` と `packages-lock.json`、R3コアの `packages.config` をコミットする。
- **バージョン競合が出たら** Player Settings → Configuration の **Assembly Version Validation を無効化**（R3導入時の既知回避策）。
- MCP はそのまま併用可（エディタ拡張であり上記ランタイムライブラリと競合しない）。
- 役割と使い分け（DI=VContainer / async=UniTask / reactive=R3）は MVP詳細設計 第5.5章に準拠。

---

## 5. ビルド・テストスクリプト

### 5.1 BuildScript.cs（static・Editorフォルダ必須）

`unity/SurvivalWorld/Assets/Editor/BuildScript.cs`:

```csharp
using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BuildScript
{
    // Linux Dedicated Server（クロスビルド）
    public static void BuildLinuxServer()
    {
        var opts = new BuildPlayerOptions {
            scenes = new[] { "Assets/Scenes/World_MVP.unity" },
            locationPathName = "Build/Server/survival-server.x86_64",
            target = BuildTarget.StandaloneLinux64,
            subtarget = (int)StandaloneBuildSubtarget.Server,   // ← Dedicated Server
            options = BuildOptions.None
        };
        Report(BuildPipeline.BuildPlayer(opts));
    }

    // Windows Client
    public static void BuildWindowsClient()
    {
        var opts = new BuildPlayerOptions {
            scenes = new[] { "Assets/Scenes/Bootstrap.unity", "Assets/Scenes/World_MVP.unity" },
            locationPathName = "Build/Client/survival.exe",
            target = BuildTarget.StandaloneWindows64,
            subtarget = (int)StandaloneBuildSubtarget.Player,
            options = BuildOptions.None
        };
        Report(BuildPipeline.BuildPlayer(opts));
    }

    static void Report(BuildReport r)
    {
        if (r.summary.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);   // batchmodeで失敗を伝える
    }
}
```

### 5.2 PowerShell スクリプト

`scripts/unity_build_server.ps1`:

```powershell
$Unity = "C:\Program Files\Unity\Hub\Editor\6000.5.xfx\Editor\Unity.exe"
& $Unity -batchmode -nographics -quit -logFile - `
  -projectPath "$PSScriptRoot\..\unity\SurvivalWorld" `
  -executeMethod BuildScript.BuildLinuxServer `
  -buildTarget Linux64 -standaloneBuildSubtarget Server
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

`scripts/unity_build_client.ps1`:

```powershell
$Unity = "C:\Program Files\Unity\Hub\Editor\6000.5.xfx\Editor\Unity.exe"
& $Unity -batchmode -nographics -quit -logFile - `
  -projectPath "$PSScriptRoot\..\unity\SurvivalWorld" `
  -executeMethod BuildScript.BuildWindowsClient `
  -buildTarget Win64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

`scripts/unity_test.ps1`:

```powershell
$Unity = "C:\Program Files\Unity\Hub\Editor\6000.5.xfx\Editor\Unity.exe"
& $Unity -batchmode -runTests -projectPath "$PSScriptRoot\..\unity\SurvivalWorld" `
  -testPlatform EditMode -testResults "$PSScriptRoot\..\unity\SurvivalWorld\results-editmode.xml" -logFile -
& $Unity -batchmode -runTests -projectPath "$PSScriptRoot\..\unity\SurvivalWorld" `
  -testPlatform PlayMode -testResults "$PSScriptRoot\..\unity\SurvivalWorld\results-playmode.xml" -logFile -
```

注意点:

- `-runTests` は自動終了するため `-quit` を付けない。ビルド（`-executeMethod`）側は `-quit` を付ける。
- `-executeMethod` の対象は **static** かつ **Editor フォルダ**内であること。
- `Unity.exe` のパスは実際のパッチ版に合わせる（`6000.5.xfx` を置換）。

---

## 6. proto の C# 生成物の取り込み

- WSL2 側 `make proto`（`buf generate`）が `unity/SurvivalWorld/Assets/Generated/` に C# を出力する（0.4/0.5）。
- Windows 側は当該フォルダを Unity がコンパイルするだけ（asmdef を切って参照制御してよい）。
- 生成物が未コミットだと WSL2 の CI（`git diff --exit-code`）が検出する。**生成物のコミット漏れに注意**。
- M0 時点では proto が空でもよい（フォルダと asmdef の受け皿だけ用意）。

---

## 7. Windows側 実装順序

| # | タスク | 完了確認 |
|---|---|---|
| W-1 | リポジトリを `C:\dev\living-world-survival` にクローン、`git lfs install` | LFS有効、クローン成功 |
| W-2 | `.gitattributes`/`unity/.gitignore` を確認（0.3/3.1） | shがLF, 資産がLFS |
| W-3 | Unity Editor 6000.5.x + Linux Dedicated Server モジュール導入 | Hubでモジュール表示 |
| W-4 | 空URPプロジェクトを開く（MCP併用） | エラーなく開く |
| W-5 | 基盤ライブラリ導入（NuGetForUnity→R3→VContainer→UniTask→FishNet→Input System） | コンパイル通過 |
| W-6 | 版固定（manifest/lock/packages.config/ProjectVersion）をコミット | 差分がコミット済み |
| W-7 | `Assets/Editor/BuildScript.cs` 追加 | 参照エラーなし |
| W-8 | `scripts/unity_test.ps1` 実行 | EditMode/PlayModeが走る |
| W-9 | `scripts/unity_build_client.ps1` 実行 | `Build/Client/survival.exe` 生成 |
| W-10 | `scripts/unity_build_server.ps1` 実行 | `Build/Server/…x86_64` 生成 |
| W-11 | `unity/SurvivalWorld/Assets/Generated/` の受け皿（asmdef）を用意 | 空でもコンパイル通過 |

---

## 8. 落とし穴（Windows側）

- **Dedicated Server モジュール未導入**だと `-standaloneBuildSubtarget Server` が失敗（2.1）。
- **NuGetForUnity を飛ばして R3.Unity だけ**入れるとコンパイルエラー。導入順を守る（第4章）。
- **`-runTests` に `-quit` を付けない**。ビルドの `-executeMethod` には付ける。
- `Library/` は巨大。必ず gitignore。共有は Packages固定＋LFS。
- `.sh` を Windows のエディタで CRLF 保存しないこと（.gitattributes で LF 固定済み）。
- `Unity.exe` のパスはパッチ版で変わる。スクリプトの版指定を実環境に合わせる。

---

## 参考資料

[R-DSBUILD] [Unity Manual: Build your application for Dedicated Server](https://docs.unity3d.com/6000.2/Documentation/Manual/dedicated-server-build.html)
[R-BATCH] [Unity Support: Batchmode build locally](https://support.unity.com/hc/en-us/articles/9466056266004-How-do-I-build-my-Unity-Project-in-Batchmode-Locally)
[R-CLI] [Unity Manual: Command line arguments](https://docs.unity3d.com/Manual/EditorCommandLineArguments.html)
[R-R3] [Cysharp/R3](https://github.com/Cysharp/R3)
[R-NGU] [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity)
[R-VC] [VContainer](https://github.com/hadashiA/VContainer)
[R-UT] [UniTask](https://github.com/Cysharp/UniTask)
