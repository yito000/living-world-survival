package worldevent

import (
	"context"
	"encoding/json"

	"github.com/nats-io/nats.go"

	"living-world-survival/services/api/internal/store"
	"living-world-survival/services/common/obs"
)

// ResultSubject carries Approved / Rejected / Completed announcements (14.3).
// Core NATS (not JetStream): a missed result is recoverable — the DS re-reads
// state via the gRPC surface, and a dropped Rejected simply means the Director
// waits for the next evaluation (10.4).
const ResultSubject = "worldevent.result"

// Result statuses published on worldevent.result (3.6).
const (
	ResultApproved  = "approved"
	ResultRejected  = "rejected"
	ResultCompleted = "completed"
)

// Result is the worldevent.result payload. Encoded as JSON (the M4 wire
// convention): the proto has no Result message, and adding one would be a
// breaking change for a purely internal announcement.
type Result struct {
	Status          string          `json:"status"`
	ProposalID      string          `json:"proposal_id,omitempty"`
	EventInstanceID string          `json:"event_instance_id,omitempty"`
	TemplateID      string          `json:"template_id,omitempty"`
	WorldID         string          `json:"world_id,omitempty"`
	RegionID        string          `json:"region_id,omitempty"`
	ReasonCode      string          `json:"reason_code,omitempty"`
	Stats           json.RawMessage `json:"stats,omitempty"`
}

// ResultPublisher announces approval decisions and event completion.
type ResultPublisher struct {
	nc *nats.Conn
}

// NewResultPublisher returns a publisher over the given NATS connection.
func NewResultPublisher(nc *nats.Conn) *ResultPublisher { return &ResultPublisher{nc: nc} }

// publish は ctx の相関フィールドを引き継いでログを出す。announce の失敗は
// 呼び出し側の処理を止めない（コミット済みの遷移は覆せない）ので warn 止まり。
func (p *ResultPublisher) publish(ctx context.Context, r Result) {
	if p == nil || p.nc == nil {
		return
	}
	ctx = obs.WithFields(ctx, obs.Fields{WorldID: r.WorldID})
	data, err := json.Marshal(r)
	if err != nil {
		obs.L(ctx).Warn("marshal world event result failed",
			"error", err.Error(), "status", r.Status)
		return
	}
	if err := p.nc.Publish(ResultSubject, data); err != nil {
		obs.L(ctx).Warn("publish world event result failed",
			"error", err.Error(), "status", r.Status, "subject", ResultSubject)
	}
}

// PublishApproved announces that a proposal was accepted and registered.
func (p *ResultPublisher) PublishApproved(ctx context.Context, proposal Proposal, instanceID string) {
	p.publish(ctx, Result{
		Status:          ResultApproved,
		ProposalID:      proposal.ProposalID,
		EventInstanceID: instanceID,
		TemplateID:      proposal.TemplateID,
		WorldID:         proposal.WorldID,
		RegionID:        proposal.RegionID,
	})
}

// PublishRejected announces that a proposal was declined, with the reason code
// the Director records. Per 10.4 the Director must NOT re-generate a free-form
// alternative — it waits for the next evaluation window.
func (p *ResultPublisher) PublishRejected(ctx context.Context, proposal Proposal, reasonCode string) {
	p.publish(ctx, Result{
		Status:     ResultRejected,
		ProposalID: proposal.ProposalID,
		TemplateID: proposal.TemplateID,
		WorldID:    proposal.WorldID,
		RegionID:   proposal.RegionID,
		ReasonCode: reasonCode,
	})
}

// PublishCompleted announces an event's end plus its aggregate stats (3.6 終了時).
func (p *ResultPublisher) PublishCompleted(ctx context.Context, inst store.WorldEventInstance) {
	p.publish(ctx, Result{
		Status:          ResultCompleted,
		ProposalID:      inst.ProposalID,
		EventInstanceID: inst.EventInstanceID,
		TemplateID:      inst.TemplateID,
		WorldID:         inst.WorldID,
		RegionID:        inst.RegionID,
		Stats:           json.RawMessage(inst.Stats),
	})
}
