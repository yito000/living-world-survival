-- 0006 の逆操作（golang-migrate down）。作成の逆順で戻す。

ALTER TABLE item_definitions DROP COLUMN IF EXISTS sell_price;

ALTER TABLE purchase_transactions
    DROP COLUMN IF EXISTS new_inventory_version,
    DROP COLUMN IF EXISTS granted_definition_ids,
    DROP COLUMN IF EXISTS item_instance_ids,
    DROP COLUMN IF EXISTS stock_entry_id;

DROP TABLE IF EXISTS asset_rankings;
DROP TABLE IF EXISTS buyer_stock;      -- buyer_instances への FK を先に落とす
DROP TABLE IF EXISTS buyer_instances;

-- (0) の TEXT 化を UUID へ戻す。0006 適用中に非UUID の purchaser/buyer が入っていると
-- この USING キャストは失敗する（想定内: down は非UUID id を持たない開発DBでのみ可逆）。
ALTER TABLE purchase_transactions ALTER COLUMN purchaser TYPE UUID USING purchaser::uuid;
ALTER TABLE purchase_transactions ALTER COLUMN buyer     TYPE UUID USING buyer::uuid;
