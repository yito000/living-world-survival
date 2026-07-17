package economy

import (
	"context"
	"errors"
	"log"
	"strings"
	"time"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"living-world-survival/services/api/internal/store"
	survivalv1 "living-world-survival/services/gen/go/survival/v1"
)

// PriceCatalog resolves Item Definition prices. itemdef.Catalog satisfies it.
type PriceCatalog interface {
	TagResolver
	SellPrice(id string) (int64, bool)
}

// Server implements survivalv1.EconomyServiceServer (09B 3.1/3.2). It is the
// internal DS→API surface; every persistent write happens here, never on the DS.
type Server struct {
	survivalv1.UnimplementedEconomyServiceServer

	Store   *store.Store
	Catalog PriceCatalog
	Tables  map[string]StockTable
}

// NewServer returns an EconomyService backed by st and catalog, loading the
// embedded Buyer Stock Definitions.
func NewServer(st *store.Store, catalog PriceCatalog) (*Server, error) {
	tables, err := LoadTables()
	if err != nil {
		return nil, err
	}
	return &Server{Store: st, Catalog: catalog, Tables: tables}, nil
}

// RegisterBuyer generates the Buyer's finite stock deterministically from the
// DS-assigned seed and persists Buyer + stock in one transaction (09B 3.5/3.2).
// Idempotent on idempotency_key.
func (s *Server) RegisterBuyer(ctx context.Context, req *survivalv1.RegisterBuyerRequest) (*survivalv1.RegisterBuyerResponse, error) {
	if req.GetIdempotencyKey() == "" {
		return nil, status.Error(codes.InvalidArgument, "idempotency_key is required")
	}
	if req.GetWorldId() == "" {
		return nil, status.Error(codes.InvalidArgument, "world_id is required")
	}
	table, ok := s.Tables[req.GetInventoryTableId()]
	if !ok {
		return nil, status.Errorf(codes.InvalidArgument, "unknown inventory_table_id %q", req.GetInventoryTableId())
	}

	// price_modifier_bp=0 would make everything cost the floor price, which is
	// almost certainly an unset field rather than a free sale — default to ×1.0.
	modifierBP := int64(req.GetPriceModifierBp())
	if modifierBP <= 0 {
		modifierBP = priceScaleBP
	}

	generated, err := GenerateStock(table, req.GetSeed(), modifierBP, s.Catalog)
	if err != nil {
		log.Printf("economy: RegisterBuyer generate stock: %v", err)
		return nil, status.Error(codes.Internal, "stock generation failed")
	}

	rows := make([]store.StockRow, 0, len(generated))
	for _, g := range generated {
		rows = append(rows, store.StockRow{
			ItemDefinitionID:  g.ItemDefinitionID,
			UnitPrice:         g.UnitPrice,
			RemainingQuantity: g.RemainingQuantity,
		})
	}

	buyerID, stock, err := s.Store.RegisterBuyer(ctx, store.BuyerRegistration{
		IdempotencyKey:   req.GetIdempotencyKey(),
		WorldID:          req.GetWorldId(),
		RegionID:         req.GetRegionId(),
		Seed:             req.GetSeed(),
		InventoryTableID: req.GetInventoryTableId(),
		PriceModifierBP:  int32(modifierBP),
		SpawnAt:          msToTime(req.GetSpawnAtUnixMs()),
		DespawnAt:        msToTime(req.GetDespawnAtUnixMs()),
		Stock:            rows,
	})
	if err != nil {
		log.Printf("economy: RegisterBuyer: %v", err)
		return nil, status.Error(codes.Internal, "register buyer failed")
	}

	out := make([]*survivalv1.BuyerStockEntry, 0, len(stock))
	for _, r := range stock {
		out = append(out, &survivalv1.BuyerStockEntry{
			StockEntryId:      r.StockEntryID,
			ItemDefinitionId:  r.ItemDefinitionID,
			UnitPrice:         r.UnitPrice,
			RemainingQuantity: int32(r.RemainingQuantity),
			Version:           r.Version,
		})
	}
	return &survivalv1.RegisterBuyerResponse{BuyerInstanceId: buyerID, Stock: out}, nil
}

// DespawnBuyer moves a Buyer to PREPARING (reject new purchases) or DESPAWNED
// (close the remaining stock and announce it) — 09B 3.2.
func (s *Server) DespawnBuyer(ctx context.Context, req *survivalv1.DespawnBuyerRequest) (*survivalv1.DespawnBuyerResponse, error) {
	if req.GetBuyerInstanceId() == "" {
		return nil, status.Error(codes.InvalidArgument, "buyer_instance_id is required")
	}
	target, err := buyerStatusOf(req.GetTargetStatus())
	if err != nil {
		return nil, status.Error(codes.InvalidArgument, err.Error())
	}

	err = s.Store.DespawnBuyer(ctx, req.GetBuyerInstanceId(), target)
	if errors.Is(err, store.ErrNotFound) {
		return &survivalv1.DespawnBuyerResponse{Status: survivalv1.ResultStatus_RESULT_STATUS_REJECTED}, nil
	}
	if err != nil {
		log.Printf("economy: DespawnBuyer: %v", err)
		return nil, status.Error(codes.Internal, "despawn buyer failed")
	}
	return &survivalv1.DespawnBuyerResponse{Status: survivalv1.ResultStatus_RESULT_STATUS_OK}, nil
}

// buyerStatusOf maps the wire target_status (PREPARING / DESPAWNED) onto the
// persisted status. An unknown value is rejected rather than defaulted: guessing
// could silently keep a Buyer sellable after the DS asked it to stop.
func buyerStatusOf(target string) (string, error) {
	switch strings.ToUpper(strings.TrimSpace(target)) {
	case "PREPARING":
		return store.BuyerStatusPreparing, nil
	case "DESPAWNED":
		return store.BuyerStatusDespawned, nil
	default:
		return "", errors.New("target_status must be PREPARING or DESPAWNED")
	}
}

// CommitPurchase confirms a purchase in a single transaction (MVP 12.2). Player
// and AI go through the identical path (AT-013).
func (s *Server) CommitPurchase(ctx context.Context, req *survivalv1.CommitPurchaseRequest) (*survivalv1.CommitPurchaseResponse, error) {
	if req.GetIdempotencyKey() == "" {
		return nil, status.Error(codes.InvalidArgument, "idempotency_key is required")
	}
	if req.GetStockEntryId() == "" || req.GetPurchaserId() == "" {
		return nil, status.Error(codes.InvalidArgument, "stock_entry_id and purchaser_id are required")
	}

	res, err := s.Store.CommitPurchase(ctx, store.PurchaseInput{
		IdempotencyKey:   req.GetIdempotencyKey(),
		BuyerInstanceID:  req.GetBuyerInstanceId(),
		StockEntryID:     req.GetStockEntryId(),
		PurchaserID:      req.GetPurchaserId(),
		InventoryVersion: req.GetInventoryVersion(),
	})
	if err != nil {
		log.Printf("economy: CommitPurchase: %v", err)
		return nil, status.Error(codes.Internal, "commit purchase failed")
	}

	granted := make([]*survivalv1.ItemRef, 0, len(res.GrantedDefinitionIDs))
	for i, defID := range res.GrantedDefinitionIDs {
		ref := &survivalv1.ItemRef{ItemDefinitionId: defID}
		if i < len(res.ItemInstanceIDs) {
			ref.ItemInstanceId = res.ItemInstanceIDs[i]
		}
		granted = append(granted, ref)
	}
	return &survivalv1.CommitPurchaseResponse{
		Status:                       purchaseStatusOf(res.Outcome),
		GrantedItems:                 granted,
		ItemInstanceIds:              res.ItemInstanceIDs,
		NewPersistedInventoryVersion: res.NewInventoryVersion,
		Charged:                      &survivalv1.Money{Amount: res.Charged},
	}, nil
}

func purchaseStatusOf(o store.PurchaseOutcome) survivalv1.PurchaseStatus {
	switch o {
	case store.PurchaseCommitted:
		return survivalv1.PurchaseStatus_PURCHASE_STATUS_COMMITTED
	case store.PurchaseDuplicate:
		return survivalv1.PurchaseStatus_PURCHASE_STATUS_DUPLICATE
	case store.PurchaseOutOfStock:
		return survivalv1.PurchaseStatus_PURCHASE_STATUS_OUT_OF_STOCK
	case store.PurchaseInsufficientFunds:
		return survivalv1.PurchaseStatus_PURCHASE_STATUS_INSUFFICIENT_FUNDS
	default:
		return survivalv1.PurchaseStatus_PURCHASE_STATUS_REJECTED
	}
}

// CommitSale confirms a sale in a single transaction (09B 3.7). Prices come from
// the Item Definition master; a Client-supplied price is never used (MVP-SEC-006).
func (s *Server) CommitSale(ctx context.Context, req *survivalv1.CommitSaleRequest) (*survivalv1.CommitSaleResponse, error) {
	if req.GetIdempotencyKey() == "" {
		return nil, status.Error(codes.InvalidArgument, "idempotency_key is required")
	}
	if req.GetSellerId() == "" {
		return nil, status.Error(codes.InvalidArgument, "seller_id is required")
	}

	items := make([]store.SaleItem, 0, len(req.GetItems()))
	for _, it := range req.GetItems() {
		price, ok := s.Catalog.SellPrice(it.GetItemDefinitionId())
		if !ok {
			return nil, status.Errorf(codes.InvalidArgument, "unknown item_definition_id %q", it.GetItemDefinitionId())
		}
		items = append(items, store.SaleItem{
			ItemDefinitionID: it.GetItemDefinitionId(),
			ItemInstanceID:   it.GetItemInstanceId(),
			UnitSellPrice:    price,
		})
	}

	res, err := s.Store.CommitSale(ctx, store.SaleInput{
		IdempotencyKey:  req.GetIdempotencyKey(),
		BuyerInstanceID: req.GetBuyerInstanceId(),
		SellerID:        req.GetSellerId(),
		Items:           items,
	})
	if err != nil {
		log.Printf("economy: CommitSale: %v", err)
		return nil, status.Error(codes.Internal, "commit sale failed")
	}
	return &survivalv1.CommitSaleResponse{
		Status:                       saleStatusOf(res.Outcome),
		Proceeds:                     &survivalv1.Money{Amount: res.Proceeds},
		NewPersistedInventoryVersion: res.NewInventoryVersion,
	}, nil
}

func saleStatusOf(o store.SaleOutcome) survivalv1.ResultStatus {
	switch o {
	case store.SaleOK:
		return survivalv1.ResultStatus_RESULT_STATUS_OK
	case store.SaleDuplicate:
		return survivalv1.ResultStatus_RESULT_STATUS_DUPLICATE
	default:
		return survivalv1.ResultStatus_RESULT_STATUS_REJECTED
	}
}

// msToTime converts unix millis to UTC, falling back to now for unset values so
// spawn_at/despawn_at are never the epoch by accident.
func msToTime(ms int64) time.Time {
	if ms <= 0 {
		return time.Now().UTC()
	}
	return time.UnixMilli(ms).UTC()
}
