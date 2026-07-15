package store

import (
	"context"
	"fmt"
)

// InventoryRow is the persistent inventory record (API is the sole Writer of
// inventory_entries / item_instances — MVP 12.2.1 / 付録C). The purchase commit
// path (Economy.CommitPurchase) is M6; M2 only provides this Writer foundation.
type InventoryRow struct {
	InventoryID    string
	OwnerType      string
	OwnerID        string
	SlotCapacity   int
	WeightCapacity int64
	Version        int64
}

// EntryRow is a single inventory slot entry.
type EntryRow struct {
	SlotIndex        int
	ItemDefinitionID string
	ItemInstanceID   *string
	Quantity         int
	Reserved         int
}

// EnsureInventory returns the inventory_id for (ownerType, ownerID), creating it
// if absent (1 owner 1 inventory — inventories_owner_uq). Idempotent.
func (s *Store) EnsureInventory(ctx context.Context, ownerType, ownerID string, slotCapacity int, weightCapacity int64) (string, error) {
	invID := NewUUID()
	err := s.pool.QueryRow(ctx,
		`INSERT INTO inventories (inventory_id, owner_type, owner_id, slot_capacity, weight_capacity)
		 VALUES ($1, $2, $3, $4, $5)
		 ON CONFLICT (owner_type, owner_id) DO UPDATE SET owner_id = EXCLUDED.owner_id
		 RETURNING inventory_id::text`,
		invID, ownerType, ownerID, slotCapacity, weightCapacity,
	).Scan(&invID)
	if err != nil {
		return "", fmt.Errorf("store: ensure inventory: %w", err)
	}
	return invID, nil
}

// UpsertEntry writes/overwrites a slot entry for an inventory (API-side Writer).
func (s *Store) UpsertEntry(ctx context.Context, inventoryID string, e EntryRow) error {
	_, err := s.pool.Exec(ctx,
		`INSERT INTO inventory_entries
		   (inventory_id, slot_index, item_definition_id, item_instance_id, quantity, reserved)
		 VALUES ($1, $2, $3, $4, $5, $6)
		 ON CONFLICT (inventory_id, slot_index) DO UPDATE
		    SET item_definition_id = EXCLUDED.item_definition_id,
		        item_instance_id   = EXCLUDED.item_instance_id,
		        quantity           = EXCLUDED.quantity,
		        reserved           = EXCLUDED.reserved`,
		inventoryID, e.SlotIndex, e.ItemDefinitionID, e.ItemInstanceID, e.Quantity, e.Reserved,
	)
	if err != nil {
		return fmt.Errorf("store: upsert entry: %w", err)
	}
	return nil
}

// GetEntries returns all slot entries for an inventory, ordered by slot index.
func (s *Store) GetEntries(ctx context.Context, inventoryID string) ([]EntryRow, error) {
	rows, err := s.pool.Query(ctx,
		`SELECT slot_index, item_definition_id, item_instance_id::text, quantity, reserved
		   FROM inventory_entries WHERE inventory_id = $1 ORDER BY slot_index ASC`,
		inventoryID,
	)
	if err != nil {
		return nil, fmt.Errorf("store: get entries: %w", err)
	}
	defer rows.Close()

	var entries []EntryRow
	for rows.Next() {
		var e EntryRow
		if err := rows.Scan(&e.SlotIndex, &e.ItemDefinitionID, &e.ItemInstanceID, &e.Quantity, &e.Reserved); err != nil {
			return nil, fmt.Errorf("store: scan entry: %w", err)
		}
		entries = append(entries, e)
	}
	return entries, rows.Err()
}
