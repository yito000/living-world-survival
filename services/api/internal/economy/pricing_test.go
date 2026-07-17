package economy

import "testing"

// 09B 5章 Unit: computeUnitPrice の整数丸め（floor）・最低価格1・オーバーフロー無し。
func TestComputeUnitPrice(t *testing.T) {
	tests := []struct {
		name      string
		basePrice int64
		buyerBP   int64
		worldBP   int64
		want      int64
	}{
		{"×1.0 は base のまま", 100, 10000, 10000, 100},
		{"×1.5 の buyer modifier", 100, 15000, 10000, 150},
		{"×0.5 の buyer modifier", 100, 5000, 10000, 50},
		{"buyer と world の両方が効く", 100, 15000, 20000, 300},
		// 端数は各段で切り捨て: 99*15000/10000 = 148.5 → 148。
		{"切り捨て（floor）", 99, 15000, 10000, 148},
		// 段ごとに floor するので (7*3333/10000)=2 → 2*10000/10000=2。
		{"段階ごとに切り捨てる", 7, 3333, 10000, 2},
		{"0 円の base でも最低価格 1", 0, 10000, 10000, minUnitPrice},
		{"切り捨てで 0 になっても最低価格 1", 1, 1, 10000, minUnitPrice},
		{"負の base は最低価格 1（購入者に入金させない）", -500, 10000, 10000, minUnitPrice},
		{"負の modifier は最低価格 1", 100, -10000, 10000, minUnitPrice},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := computeUnitPrice(tt.basePrice, tt.buyerBP, tt.worldBP); got != tt.want {
				t.Errorf("computeUnitPrice(%d, %d, %d) = %d, want %d",
					tt.basePrice, tt.buyerBP, tt.worldBP, got, tt.want)
			}
		})
	}
}

// 入力を上限で切っても 2 段の中間積が int64 を溢れず、結果が正のままであること（3.4）。
// 素朴な clamp は 1 段目しか守れず 2 段目で溢れる（負値に化ける）ため、両段を検証する。
func TestComputeUnitPriceNoOverflow(t *testing.T) {
	const huge = int64(1) << 62

	got := computeUnitPrice(huge, huge, huge)
	if got <= 0 {
		t.Fatalf("computeUnitPrice(huge, huge, huge) = %d; オーバーフローで非正になった", got)
	}
	// 入力は maxBasePrice / maxModifierBP へ丸められるので上限は決定的。
	want := maxBasePrice * maxModifierBP / priceScaleBP * maxModifierBP / priceScaleBP
	if got != want {
		t.Errorf("computeUnitPrice(huge, huge, huge) = %d, want %d", got, want)
	}

	// 上限そのものの入力でも同じく正であること。
	if got := computeUnitPrice(maxBasePrice, maxModifierBP, maxModifierBP); got <= 0 {
		t.Errorf("computeUnitPrice(上限入力) = %d; want > 0", got)
	}
}
