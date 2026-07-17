// Package metrics は api 固有の Prometheus 系列を定義する（基本設計第13章 /
// 10B 3.5）。共通系列（HTTP/gRPC/DB latency）は services/common/obs にある。
package metrics

import (
	"living-world-survival/services/common/obs"

	"github.com/prometheus/client_golang/prometheus"
)

var (
	// OutboxDepth は未 publish の outbox_messages 件数（10B 3.2 の Outbox 滞留）。
	// Relay が NATS へ追随できていないと単調増加する。
	OutboxDepth = prometheus.NewGauge(prometheus.GaugeOpts{
		Name: "outbox_depth",
		Help: "Outbox messages awaiting publish (published_at IS NULL).",
	})

	// OutboxOldestAgeSeconds は最古の未 publish メッセージの滞留秒数。
	// depth だけでは「捌けているが常に忙しい」と「詰まっている」を区別できない。
	OutboxOldestAgeSeconds = prometheus.NewGauge(prometheus.GaugeOpts{
		Name: "outbox_oldest_age_seconds",
		Help: "Age of the oldest unpublished outbox message.",
	})

	// OutboxPublished は publish 成功数（result=ok/publish_failed/mark_failed）。
	OutboxPublished = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "outbox_publish_total",
		Help: "Outbox publish attempts by result.",
	}, []string{"result"})

	// EventLagSeconds は domain_events の最新 occurred_at から現在までの秒数
	// （第13章 イベント Lag）。DS が書いた事象を API がどれだけ古く見ているか。
	EventLagSeconds = prometheus.NewGauge(prometheus.GaugeOpts{
		Name: "event_lag_seconds",
		Help: "Seconds since the newest domain_events.occurred_at.",
	})

	// Purchases は購入の結果別件数（第13章 / AT-021）。
	// result=committed/replayed/insufficient_funds/out_of_stock/rejected
	Purchases = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "economy_purchases_total",
		Help: "Purchase commit outcomes.",
	}, []string{"result"})

	// Sales は売却の結果別件数。
	Sales = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "economy_sales_total",
		Help: "Sale commit outcomes.",
	}, []string{"result"})

	// BuyerSoldOut は Buyer が在庫切れで購入を拒否した回数（第13章 Buyer sold-out）。
	//
	// item_id ラベルは付けない。out-of-stock の応答は item を持たず（PurchaseResult は
	// 却下時に GrantedDefinitionIDs が空）、stock_entry_id で代用すると
	// 在庫行の数だけ時系列が増える。件数の推移が分かれば第13章の用途には足りる。
	BuyerSoldOut = prometheus.NewCounter(prometheus.CounterOpts{
		Name: "buyer_sold_out_total",
		Help: "Purchases rejected because the Buyer stock was exhausted.",
	})

	// SnapshotsSaved は Snapshot 作成数（第13章 / 10B 3.1 Snapshot 30秒間隔）。
	SnapshotsSaved = prometheus.NewCounter(prometheus.CounterOpts{
		Name: "world_snapshots_saved_total",
		Help: "World snapshots persisted (staging -> checksum -> active).",
	})

	// RankingRuns はランキング Batch の実行結果（result=ok/failed/already_running）。
	RankingRuns = prometheus.NewCounterVec(prometheus.CounterOpts{
		Name: "ranking_runs_total",
		Help: "Ranking batch runs by result.",
	}, []string{"result"})
)

func init() {
	obs.Registry().MustRegister(
		OutboxDepth, OutboxOldestAgeSeconds, OutboxPublished, EventLagSeconds,
		Purchases, Sales, BuyerSoldOut, SnapshotsSaved, RankingRuns,
	)
}
