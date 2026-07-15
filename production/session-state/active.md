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
