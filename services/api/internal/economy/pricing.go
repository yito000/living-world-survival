// Package economy implements the M6 Economy domain (09B): Buyer registration
// with deterministic finite stock, the single-transaction purchase/sale commits,
// and the NATS economy events they emit. The API is the sole persistent Writer
// of buyer_instances / buyer_stock / inventory_entries / item_instances /
// currency_ledger; the DS only mirrors the result into Runtime (MVP 12.2.1 / 付録C).
//
// Currency is always an integer in the minimum currency unit. Modifiers are
// basis points (10000 = ×1.0) so no float ever enters a price (MVP 13.1; the
// forbidigo linter enforces this).
package economy

// priceScaleBP is the basis-point denominator: 10000 bp = ×1.0.
const priceScaleBP int64 = 10000

// worldPriceModifierBP is the world-wide price modifier (設定値, 既定 ×1.0).
// The buyer-specific modifier arrives per RegisterBuyer as price_modifier_bp.
const worldPriceModifierBP int64 = 10000

// minUnitPrice is the price floor. Flooring two bp multiplications can round a
// cheap item down to 0, which would make it free — clamp to 1 instead (3.4).
const minUnitPrice int64 = 1

// Input bounds keeping both intermediate products of computeUnitPrice inside
// int64 (~9.22e18). The two multiplications compound, so bounding only the first
// is not enough — the worst case is:
//
//	step1: maxBasePrice * maxModifierBP     = 1e9 * 1e6 = 1e15   → /1e4 = 1e11
//	step2: 1e11         * maxModifierBP     = 1e11 * 1e6 = 1e17  → /1e4 = 1e13
//
// Both stay well inside int64 (3.4 オーバーフロー防止). The caps are far above any
// real value: 1e9 minimum currency units of base price and a ×100 modifier.
const (
	maxBasePrice  int64 = 1_000_000_000
	maxModifierBP int64 = 1_000_000 // ×100.0
)

// computeUnitPrice returns basePrice × buyerBP/10000 × worldBP/10000, truncated
// (floor) at each step and clamped to minUnitPrice (MVP 8.7 / 09B 3.4). The
// result is fixed at stock-generation time and stored in buyer_stock.unit_price;
// purchases never recompute it.
//
// Inputs are clamped to non-negative and to the bounds above: a negative
// modifier must not produce a negative price (which would credit the purchaser
// instead of charging them), and an absurd one must not overflow into one.
func computeUnitPrice(basePrice, buyerBP, worldBP int64) int64 {
	basePrice = clamp(basePrice, maxBasePrice)
	buyerBP = clamp(buyerBP, maxModifierBP)
	worldBP = clamp(worldBP, maxModifierBP)

	p := basePrice * buyerBP / priceScaleBP
	p = p * worldBP / priceScaleBP
	if p < minUnitPrice {
		return minUnitPrice
	}
	return p
}

// clamp bounds v to [0, max].
func clamp(v, max int64) int64 {
	if v < 0 {
		return 0
	}
	if v > max {
		return max
	}
	return v
}
