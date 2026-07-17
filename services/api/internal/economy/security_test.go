package economy

import (
	"strings"
	"testing"

	survivalv1 "living-world-survival/services/gen/go/survival/v1"

	"google.golang.org/protobuf/reflect/protoreflect"
)

// MVP-SEC-006「Damage/Loot/Drop/Craft Result/Purchase Price を Client 入力から
// 採用しない」を **契約として固定** する。
//
// 現状 CommitPurchaseRequest には価格に相当するフィールドが 1 つも無く、API は
// stock_entry_id から自分で価格を引く。つまり呼び出し側は価格を渡す手段が構造的に
// 無い＝改竄しようがない。これは「改竄値を無視する」より強い保証だが、誰かが
// あとから price を足せば黙って崩れる。このテストはその退行を検知する。
//
// 価格を Request に足す必要が本当に出たら、このテストを消す前に「その値を
// サーバが採用しないこと」を示すテストへ置き換えること。
func TestSecurityPurchaseRequestCarriesNoClientPrice(t *testing.T) {
	// 価格・金額に相当し得るフィールド名の断片。
	forbidden := []string{
		"price", "amount", "cost", "money", "currency", "charge",
		"total", "fee", "value", "discount",
	}

	for _, tc := range []struct {
		name string
		msg  protoreflect.ProtoMessage
	}{
		{"CommitPurchaseRequest", (*survivalv1.CommitPurchaseRequest)(nil)},
		{"CommitSaleRequest", (*survivalv1.CommitSaleRequest)(nil)},
	} {
		fields := tc.msg.ProtoReflect().Descriptor().Fields()
		for i := 0; i < fields.Len(); i++ {
			field := strings.ToLower(string(fields.Get(i).Name()))
			for _, bad := range forbidden {
				if strings.Contains(field, bad) {
					t.Errorf(
						"%s.%s looks like a client-supplied price: MVP-SEC-006 requires "+
							"the API to be price-authoritative (it resolves price from "+
							"stock_entry_id / the Item Definition catalog)",
						tc.name, field)
				}
			}
		}
	}
}

// RegisterBuyer は price_modifier_bp を受け取るが、これは DS が決めてよい
// 「値付けの倍率」であって Client 入力ではない（DS は権威側）。単価そのものは
// Item Definition カタログ由来で、GenerateStock がサーバ側で算出する。
// ここでは「Buyer 登録経路にも単価の直接指定が無い」ことを固定する。
func TestSecurityRegisterBuyerCarriesNoUnitPrice(t *testing.T) {
	fields := (*survivalv1.RegisterBuyerRequest)(nil).ProtoReflect().Descriptor().Fields()
	for i := 0; i < fields.Len(); i++ {
		name := strings.ToLower(string(fields.Get(i).Name()))
		// price_modifier_bp（倍率）は許容。単価/総額の直接指定は許容しない。
		if name == "price_modifier_bp" {
			continue
		}
		for _, bad := range []string{"unit_price", "price", "amount", "cost"} {
			if strings.Contains(name, bad) {
				t.Errorf("RegisterBuyerRequest.%s allows a caller-supplied unit price "+
					"(MVP-SEC-006: prices come from the Item Definition catalog)", name)
			}
		}
	}
}
