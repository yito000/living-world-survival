# M2（WSL2 / B側）手動E2Eスモーク 証跡

- 日付: 2026-07-15
- 対象: M2 永続化経路（WorldData gRPC / domain_events / outbox→NATS / world_snapshots）
- 実行: `make e2e-m2`（= `scripts/e2e_m2_smoke.sh`）
- 実体: **実 apid コンテナ / 実 PostgreSQL / 実 NATS JetStream**。Dedicated Server ランタイムのみ
  `services/api/cmd/m2smoke`（DS の RuntimePersistenceAgent 相当のクライアント）で模擬。

## 位置づけ（境界）

05B 5章の推奨E2E（7ステップ）のうち **B が単独で保証できる範囲**を実物で通したもの。
Unity Dedicated Server(Windows/A) を用いた真のクロス環境E2E（DS起動→bootstrap ready→
クライアント InventoryCommand 送信）は A/B 合同作業であり、かつ現状 DS ビルドが未収録
コンポ欠落でブロック中のため別途。ここでは DS が API へ発行する gRPC 契約（bootstrap /
AppendEvents / SaveSnapshot / 復元）を実プロセス越しに検証している。

| 05B 推奨ステップ | 本スモークでの検証 |
|---|---|
| 1. backend/WorldData 起動 | infra + api コンテナ起動・migrate・`/readyz` ready |
| 2-3. DS 起動→bootstrap→ready | `LoadBootstrap`（新規 world → 空 snapshot / seq=0）=READY 相当 |
| 4-5. inventory command (ADD/MOVE/DROP/USE) 1本 | ADD→`AppendEvents`(OK)、MOVE/DROP も追加。再送=DUPLICATE(AT-003) |
| 6. outbox flush 後にイベント/スナップショットが残る | domain_events 永続(API採番 seq 1..4)＋起動中 relay が NATS 配信＋`published_at` 確定＋`SaveSnapshot` |
| 7. DS 再起動後 snapshot + event tail から復元 | 再 `LoadBootstrap` が snapshot(seq=2)＋tail[seq3,seq4] を昇順で返す |

## 実行ログ（証跡）

```
  [step 1] world provisioned: ac965e02-880a-4241-8d93-e2a0c699c689
  [step 2] DS bootstrap complete → READY (snapshot='', sequence=0, payload={})
  [step 4] ADD command event persisted (event_id=ev-a048e479-dd31-4998-95f0-cf212d63086a)
  [step 5] idempotency verified (resend → DUPLICATE, AT-003)
      · NATS ← world.ac965e02-...event.actor  {"cmd": "ADD", "slot": 0, "quantity": 5, "item_definition_id": "stone"}
      · NATS ← world.ac965e02-...event.actor  {"cmd": "ADD", "slot": 1, "quantity": 3, "item_definition_id": "wood"}
  [step 6] outbox flushed: domain_events persisted (seq 1,2) + delivered to NATS + published_at set
  [step 6] snapshot saved at sequence=2 (snapshot_id=4ffed024-4514-4402-ad25-0f354234c9c6)
  [step 7] DS restart restore OK: snapshot(seq=2) + event tail [MOVE seq3, DROP seq4] in ascending order

✅ M2 E2E SMOKE PASSED (persistence path verified end-to-end through the running apid)
```

## 再現手順

```bash
make e2e-m2        # infra+api 起動→migrate→DSシミュレータで永続化経路を1本通す
# ローカルログ: build/e2e/m2_smoke.log（gitignore 対象）
```

---

# 追記: 実 Dedicated Server による真のクロス環境E2E（2026-07-15）

DS 再ビルド完了後、Linux DS 成果物（`unity/.../Build/Server/`）を ext4(`$HOME/ds-local`) へ
コピーし `-batchmode -nographics` で起動。**実 Unity DS → 実 apid(:8092) → PostgreSQL** の
M2 永続化ハンドシェイクを実機で確認した（DS シミュレータではなく本物の DS）。

事前準備（B/DB プロビジョニング）: DS 設定の world_id `00000000-0000-0000-0000-000000000201`
を `worlds` に INSERT（LoadBootstrap の NOT_FOUND 回避）。

## 観測結果（実 DS）

1. **LoadBootstrap（DS → API）**: apid ログ
   ```
   grpc: LoadBootstrap world=00000000-0000-0000-0000-000000000201 server_build=dev-local tail=0
   ```
   新規 world → 空 snapshot/seq=0 を返却 = DS bootstrap 成功 → RuntimePersistenceAgent 起動。
2. **SaveSnapshot（DS → API, 30秒周期）**: `world_snapshots` に DS 由来行が生成され
   `worlds.active_snapshot_id` が更新（staging→active 切替が実ペイロードで成功）:
   ```
   snapshot_id=ca714336-... sequence=0 checksum=44136fa3...caaff8a
   worlds.active_snapshot_id = ca714336-...
   ```
3. **checksum 契約 A/B 一致（最重要・落とし穴6.4）**: DS 送信 checksum
   `44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a`
   = `sha256("{}")`（空 payload の SHA-256 hex）。API の照合式と**完全一致** → 常時拒否のリスク解消。

## 到達点

- ✅ 実 DS で **bootstrap（復元源）** と **snapshot（checksum検証+active切替）** が通ることを実機確認。
- ✅ **AppendEvents（実 DS → API）を実機で確認**（A 側 inventory command 配線完了後）。
  Windows Client(PID 56852) → DS `InventoryRuntimeService`(ADD/MOVE/DROP) → RuntimePersistenceAgent
  → `AppendEvents`。`domain_events`(world `...0201`) に3件着弾：

  | seq(API採番) | local_seq | type | aggregate_id | event_id |
  |---|---|---|---|---|
  | 1 | 1 | inventory.item_added | connection:1 | 06FPBB9SKNEJZDM6RGPTP4NVEC |
  | 2 | 2 | inventory.move | connection:1 | 06FPBB9TF8BPEQ9PMPGJFRSDSC |
  | 3 | 3 | inventory.drop | connection:1 | 06FPBB9VGDX44Z8W71DS3VEPPR |

  outbox 3件すべて `published_at` 確定（relay → NATS JetStream 配信）。apid エラー無し。

## 実装バグ修正（実 DS が炙り出した）

初回の実 DS AppendEvents は `store: insert event: invalid input syntax for type uuid: "connection:0"`
(SQLSTATE 22P02) で status 13 失敗した。原因は `domain_events.aggregate_id` が UUID 列だったが、
proto は `string` で実 DS は `connection:N` 等の非UUID識別子を送るため。**migrations 0002 で
`domain_events.aggregate_id` / `actor_runtime_states.actor_id` / `actor_state_projections.actor_id`
を TEXT 化**（event_id と同類, proto契約に整合）。回帰テスト
`TestAppendEventsNonUuidIdentifiers` を追加。稼働 apid はステートメントキャッシュ更新のため再起動。

## 補足

- world_snapshots は seq=0/`{}` のまま（DS が world snapshot に空 payload を出す設計。インベントリ状態は
  Domain Event / actor_runtime_states 側で表現され、world snapshot には含めない DS 側のコンテンツ判断）。
  B 側 SaveSnapshot 契約（staging→checksum→active・checksum A/B一致）は前段で実 DS 実証済み。

## 残（フル自動化・クロス環境E2E）

- CI 用自動E2E化: 本スモークは安定後 `scripts/smoke.sh` 系へ統合可能（現状は手動 `make e2e-m2`）。
- 実 DS 経由 AppendEvents: ゲームクライアント接続＋inventory command で発火（A 側 client と合同）。
