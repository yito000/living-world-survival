## Session Extract - /dev-story 2026-07-15
- Story: docs/prompts/story_0002a/win/unity/05A-1_Story_0002a_CharacterModel設定実装指示書_Windows側_v0.1.md - Character Model setup
- Story: docs/prompts/story_0002/win/unity/05A_M2実装指示書_Windows側_v0.1.md - M2 Windows Inventory/Save runtime foundation
- Files changed: unity/SurvivalWorld/Assets/Prefabs/PlayerCharacter.prefab, unity/SurvivalWorld/Assets/Scripts/Items, unity/SurvivalWorld/Assets/Scripts/Inventory, unity/SurvivalWorld/Assets/Scripts/World, unity/SurvivalWorld/Assets/Scripts/Config/SurvivalRuntimeConfig.cs, unity/SurvivalWorld/Assets/Scripts/Server/ServerBootstrap.cs, unity/SurvivalWorld/Assets/Data/Items, unity/SurvivalWorld/Assets/Editor/M2ItemDefinitionAssetGenerator.cs, unity/SurvivalWorld/Assets/Tests/EditMode, unity/SurvivalWorld/Assets/Tests/PlayMode
- Test written: unity/SurvivalWorld/Assets/Tests/EditMode/M2InventoryServiceTests.cs, unity/SurvivalWorld/Assets/Tests/EditMode/M2ItemDefinitionTests.cs, unity/SurvivalWorld/Assets/Tests/EditMode/M2WorldPersistenceTests.cs, unity/SurvivalWorld/Assets/Tests/PlayMode/PlayerCharacterVisualPrefabTests.cs
- Verification: Unity EditMode 36 passed / 1 ignored (WSL2 source JSON absent); Unity PlayMode 4 passed; Windows Client build success; Linux Dedicated Server build success
- Blockers: None
- Next: /code-review unity/SurvivalWorld/Assets/Prefabs/PlayerCharacter.prefab unity/SurvivalWorld/Assets/Scripts/Items unity/SurvivalWorld/Assets/Scripts/Inventory unity/SurvivalWorld/Assets/Scripts/World unity/SurvivalWorld/Assets/Scripts/Server/ServerBootstrap.cs then /story-done docs/prompts/story_0002a/win/unity/05A-1_Story_0002a_CharacterModel設定実装指示書_Windows側_v0.1.md

## Session Extract - /dev-story 2026-07-15 inventory command bridge
- Story: docs/prompts/story_0002/win/unity/05A_M2実装指示書_Windows側_v0.1.md - M2 inventory command DS wiring
- Files changed: unity/SurvivalWorld/Assets/Scripts/Inventory/InventoryService.cs, unity/SurvivalWorld/Assets/Scripts/Inventory/InventoryRuntimeService.cs, unity/SurvivalWorld/Assets/Scripts/Server/ServerBootstrap.cs, unity/SurvivalWorld/Assets/Scripts/Player/NetworkInventoryCommandBridge.cs, unity/SurvivalWorld/Assets/Prefabs/PlayerCharacter.prefab, unity/SurvivalWorld/Assets/Tests/EditMode/M2InventoryServiceTests.cs, unity/SurvivalWorld/Assets/Tests/PlayMode/PlayerCharacterVisualPrefabTests.cs, scripts/unity_m2_inventory_smoke_client.ps1
- Test written: M2InventoryService AddItemCommand/idempotency/outbox event tests; InventoryRuntimeService Add->Move->Drop smoke sequence test; PlayerCharacter prefab bridge assertion
- Verification: Unity EditMode 40 total / 39 passed / 1 ignored; Unity PlayMode 4/4 passed; Windows Client build success; Linux Dedicated Server build success; live client smoke submitted run_id=m2-real-ds-1784115977897 to DS 127.0.0.1:7770
- Blockers: None
- Next: Restart/copy latest DS build only if TargetRpc-backed client smoke result logging is required; current restarted DS already received inventory smoke commands for AppendEvents observation.

## Session Extract - /story-done 2026-07-15 Story_0002
- Verdict: COMPLETE
- Story: docs/prompts/story_0002/win/unity/05A_M2実装指示書_Windows側_v0.1.md - M2 Inventory / Save
- Acceptance evidence: Real Unity DS + real Windows Client + real apid/PostgreSQL/NATS E2E completed. Client->DS inventory ADD/MOVE/DROP produced matching event_ids and API-assigned sequences 1,2,3; outbox published_at confirmed; NATS delivery confirmed; LoadBootstrap and RuntimePersistenceAgent startup confirmed; SaveSnapshot checksum was previously verified with real DS.
- Client smoke run: run_id=m2-inventory-1784117273289; event_ids=06FPBB9SKNEJZDM6RGPTP4NVEC, 06FPBB9TF8BPEQ9PMPGJFRSDSC, 06FPBB9VGDX44Z8W71DS3VEPPR.
- Tests: Unity EditMode 40 total / 39 passed / 1 ignored; Unity PlayMode 4/4 passed; Windows Client build success; Linux Dedicated Server build success. Backend side reported go test -race green and golangci-lint 0 issues after aggregate_id/actor_id TEXT migration fix.
- Deviations: None blocking. Backend schema bug found by real DS E2E and fixed on B side: domain_events.aggregate_id and actor id columns changed from UUID to TEXT to match proto string contract.
- Tech debt logged: None.
- Next recommended: Sprint/M2 close-out QA sequence if no additional Story_0002 follow-ups remain.

## Session Extract - /dev-story 2026-07-15 M3 Windows
- Story: docs/prompts/story_0003/win/unity/06A_M3実装指示書_Windows側_v0.1.md - M3 Windows DS gameplay logic and client UI foundation
- Files changed: unity/SurvivalWorld/Assets/Scripts/Shared, unity/SurvivalWorld/Assets/Scripts/Server/Inventory, unity/SurvivalWorld/Assets/Scripts/Server/Persistence, unity/SurvivalWorld/Assets/Scripts/Server/Combat, unity/SurvivalWorld/Assets/Scripts/Server/Simulation, unity/SurvivalWorld/Assets/Scripts/Server/Handlers, unity/SurvivalWorld/Assets/Scripts/Client, unity/SurvivalWorld/Assets/Tests/EditMode/M3SurvivalSystemsTests.cs
- Test written: unity/SurvivalWorld/Assets/Tests/EditMode/M3SurvivalSystemsTests.cs covering Damage Matrix, recipe/deadlock constraints, Hunger thresholds, mining full-inventory rejection, station reserve/cancel, and hunting/carcass single-use drops
- Verification: Unity MCP EditMode 46 total / 45 passed / 1 ignored (existing optional WSL2 JSON test); Unity MCP PlayMode 4/4 passed; Unity Console compile errors cleared. scripts\\unity_test.ps1 and scripts\\unity_build_server.ps1 returned code 1 before test/build execution while another Unity Editor instance was running; logs contained no compile/build error body.
- Blockers: Batch Unity server build not verified in this turn due existing Unity process/batch startup failure.
- Next: /code-review unity/SurvivalWorld/Assets/Scripts/Shared unity/SurvivalWorld/Assets/Scripts/Server unity/SurvivalWorld/Assets/Scripts/Client unity/SurvivalWorld/Assets/Tests/EditMode/M3SurvivalSystemsTests.cs then /story-done docs/prompts/story_0003/win/unity/06A_M3実装指示書_Windows側_v0.1.md

## Local Runtime Note - DS verification
- Always use WSL distro `Ubuntu-26.04` for Linux Dedicated Server runtime checks. Do not use the default `Ubuntu` distro for DS verification because its glibc is too old for the current DS build.

## Session Extract - /story-done 2026-07-16 Story_0003
- Verdict: COMPLETE WITH NOTES
- Story: docs/prompts/story_0003/win/unity/06A_M3実装指示書_Windows側_v0.1.md - M3 Windows DS gameplay logic and Client UI foundation
- Acceptance evidence: Unity MCP EditMode 46 total / 45 passed / 1 ignored; Unity MCP PlayMode 4/4 passed; Windows Client build success; Linux Dedicated Server build success; backend Docker Go smoke passed; real Unity DS on Ubuntu-26.04 + Windows Client smoke passed; outbox unpublished=0/total=3.
- Notes: DS runtime verification must use Ubuntu-26.04. No dedicated automated multi-client script was found; client and DS builds are current for manual multi-client E2E.
- Tech debt logged: None
- Next recommended: Sprint close-out QA sequence if both Windows and WSL2 M3 scopes are closed; otherwise complete the matching WSL2-side story-done.

## Session Extract - /dev-story 2026-07-17 M5 Windows
- Story: docs/prompts/story_0005/win/unity/08A_M5実装指示書_Windows側_v0.1.md - M5 Windows DS ActionDecision and WorldEvent local effects
- Files changed: unity/SurvivalWorld/Assets/Scripts/Server/AI/AIDecisionClient.cs, unity/SurvivalWorld/Assets/Scripts/Server/AI/AIActorController.cs, unity/SurvivalWorld/Assets/Scripts/Server/AI/AIActorSystem.cs, unity/SurvivalWorld/Assets/Scripts/WorldEvent/, unity/SurvivalWorld/Assets/Tests/EditMode/M5WorldEventEditModeTests.cs
- Test written: unity/SurvivalWorld/Assets/Tests/EditMode/M5WorldEventEditModeTests.cs covering primitive step validation/application, expired decision rejection, great_hunt caps, rare_resource budget cap, rare_buyer_rush count/duration, proposal conflict rejection, and proposal version mismatch rejection
- Verification: dotnet build unity/SurvivalWorld/SurvivalWorld.Tests.EditMode.csproj passed; scripts\unity_test.ps1 passed; scripts\unity_build_server.ps1 passed
- Deviations: Generated ActionDecision currently has no template_version or lease_until fields; Generated files were left untouched per Windows/WSL2 boundary, with lease enforced from created_at_unix_ms + template max duration and available state/template/step validation applied.
- Blockers: None
- Next: /code-review unity/SurvivalWorld/Assets/Scripts/Server/AI/AIDecisionClient.cs unity/SurvivalWorld/Assets/Scripts/WorldEvent unity/SurvivalWorld/Assets/Tests/EditMode/M5WorldEventEditModeTests.cs then /story-done docs/prompts/story_0005/win/unity/08A_M5実装指示書_Windows側_v0.1.md
## Session Extract - /dev-story 2026-07-17 M6 Windows
- Story: docs/prompts/story_0006/win/unity/09A_M6実装指示書_Windows側_v0.1.md - M6 Windows DS Buyer/Economy purchase flow
- Files changed: unity/SurvivalWorld/Assets/Scripts/Economy/, unity/SurvivalWorld/Assets/Scripts/Server/AI/Economy/, unity/SurvivalWorld/Assets/Scripts/Player/NetworkBuyerPurchaseCommandBridge.cs, unity/SurvivalWorld/Assets/Scripts/Inventory/InventoryService.cs, unity/SurvivalWorld/Assets/Scripts/Inventory/InventoryRuntimeService.cs, unity/SurvivalWorld/Assets/Scripts/Inventory/AIInventoryAdapter.cs, unity/SurvivalWorld/Assets/Scripts/Server/AI/AIActorSystem.cs, unity/SurvivalWorld/Assets/Scripts/Server/ServerBootstrap.cs, unity/SurvivalWorld/Assets/Scripts/Config/SurvivalRuntimeConfig.cs, unity/SurvivalWorld/Assets/Tests/EditMode/M6EconomyEditModeTests.cs
- Test written: M6EconomyEditModeTests covering API-confirmed purchase grant without outbox, command_id idempotency, pre-API validation rejection for distance/status/version/stock, version mismatch full snapshot reconciliation, AI Purchase primitive integration, and deterministic rare buyer spawn window
- Verification: scripts\unity_test.ps1 passed (EditMode 68 total / 67 passed / 1 ignored; PlayMode 4/4 passed); scripts\unity_build_server.ps1 passed; scripts\unity_build_client.ps1 passed
- Deviations: Generated RegisterBuyer/DespawnBuyer RPCs are not present yet in Assets/Generated, so generated proto files were left untouched per Windows/WSL2 boundary and EconomyGrpcClient exposes placeholder failure paths for those calls until 09B-generated C# lands. CommitPurchase/CommitSale use the current generated EconomyService client.
- Blockers: None for Windows compile/test/build. Live RegisterBuyer/DespawnBuyer E2E remains dependent on 09B proto/API generation.
- Next: /code-review unity/SurvivalWorld/Assets/Scripts/Economy unity/SurvivalWorld/Assets/Scripts/Server/AI/Economy unity/SurvivalWorld/Assets/Scripts/Player/NetworkBuyerPurchaseCommandBridge.cs unity/SurvivalWorld/Assets/Scripts/Server/ServerBootstrap.cs unity/SurvivalWorld/Assets/Tests/EditMode/M6EconomyEditModeTests.cs then /story-done docs/prompts/story_0006/win/unity/09A_M6実装指示書_Windows側_v0.1.md
## Session Extract - /dev-story 2026-07-17 M7 Windows
- Story: docs/prompts/story_0007/win/unity/10A_M7実装指示書_Windows側_v0.1.md - M7 Windows asset import and runtime hardening
- Files changed: .gitignore, scripts/unity_import_assets.ps1, unity/SurvivalWorld/ProjectSettings/TagManager.asset, unity/SurvivalWorld/Assets/Editor/AssetImportProcessor.cs, unity/SurvivalWorld/Assets/Editor/BuildScript.cs, unity/SurvivalWorld/Assets/Editor/SurvivalWorld.Editor.asmdef, unity/SurvivalWorld/Assets/Scripts/Auth/MatchmakingJoinFlow.cs, unity/SurvivalWorld/Assets/Scripts/Bootstrap/Bootstrapper.cs, unity/SurvivalWorld/Assets/Scripts/World/GeneratedAssetMetadata.cs, unity/SurvivalWorld/Assets/Tests/EditMode/M1aDevLocalTicketTests.cs, unity/SurvivalWorld/Assets/Tests/EditMode/M7AssetImportProcessorTests.cs, unity/SurvivalWorld/Assets/Tests/EditMode/M7SessionRecoveryTests.cs, unity/SurvivalWorld/Assets/Tests/EditMode/SurvivalWorld.Tests.EditMode.asmdef
- Test written: M7AssetImportProcessorTests covering generated client/server prefab metadata, sockets, interaction points, colliders, LOD and manifest validation; M7SessionRecoveryTests covering 401 refresh/retry; M1aDevLocalTicketTests tampered signature rejection
- Verification: scripts\unity_import_assets.ps1 passed and generated 18 client prefabs plus 18 server prefabs; scripts\unity_test.ps1 passed with EditMode 73/73 and PlayMode 4/4; git diff --check passed for touched files
- Deviations: Current Unity project lacks a GLB importer package, so the import processor used deterministic manifest fallback meshes while preserving metadata/collider/socket/interaction prefab contracts. Adding a GLB importer will switch generated prefabs to source model geometry.
- Blockers: None for current Windows compile/test/import scope
- Next: /code-review unity/SurvivalWorld/Assets/Editor/AssetImportProcessor.cs unity/SurvivalWorld/Assets/Scripts/Auth/MatchmakingJoinFlow.cs unity/SurvivalWorld/Assets/Scripts/Bootstrap/Bootstrapper.cs unity/SurvivalWorld/Assets/Tests/EditMode/M7AssetImportProcessorTests.cs unity/SurvivalWorld/Assets/Tests/EditMode/M7SessionRecoveryTests.cs then /story-done docs/prompts/story_0007/win/unity/10A_M7実装指示書_Windows側_v0.1.md

## Session Extract - /dev-story 2026-07-18
- Story: docs/prompts/story_0008a/win/unity/11A_M8A_PlaytestInteractions実装指示書_Windows側_v0.1.md - M8A Playtest Interactions
- Files changed: Player input/bridges, interaction scanner/targets/controller/seeder, playtest UI, ServerBootstrap M3 command routing, CleaningSystem target registration, PlayerCharacter prefab, M8A EditMode/PlayMode tests
- Test written: unity/SurvivalWorld/Assets/Tests/EditMode/M8APlaytestInteractionsEditModeTests.cs, unity/SurvivalWorld/Assets/Tests/PlayMode/M8APlaytestArenaPlayModeTests.cs
- Blockers: None
- Next: /code-review M8A changed Unity files then /story-done docs/prompts/story_0008a/win/unity/11A_M8A_PlaytestInteractions実装指示書_Windows側_v0.1.md
