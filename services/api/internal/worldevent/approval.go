package worldevent

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"time"

	"github.com/nats-io/nats.go"
)

// ProposalSubject is what the Director publishes to (14.3). The wildcard binds
// every server's proposals; the DS-facing announcement goes back out on
// worldevent.result.
const ProposalSubject = "worldevent.proposal.*"

// AllowedTemplates is the Allowed ID set for world events (10.3). A proposal
// naming anything else is discarded — the LLM must not invent event templates
// (17章 MVP-SEC-008).
var AllowedTemplates = map[string]bool{
	"world_event.great_hunt":      true,
	"world_event.rare_resource":   true,
	"world_event.rare_buyer_rush": true,
}

// Cooldowns is the same-kind cooldown per template (10.4). Set to the event's
// own MVP duration (15分/15分/10分) so a region cannot chain the same event
// back-to-back. The DS enforces the in-event caps (08A 3.2); this only gates
// admission.
var Cooldowns = map[string]time.Duration{
	"world_event.great_hunt":      15 * time.Minute,
	"world_event.rare_resource":   15 * time.Minute,
	"world_event.rare_buyer_rush": 10 * time.Minute,
}

// Reason codes returned to the Director on rejection (10.4).
const (
	ReasonUnknownTemplate  = "unknown_template"
	ReasonTemplateInactive = "template_inactive"
	ReasonRegionConflict   = "region_conflict"
	ReasonCooldown         = "cooldown_active"
	ReasonInvalidParams    = "invalid_params"
)

// maxActivePerRegion is the same-region conflict threshold (10.4): one live
// event (PROPOSED or ACTIVE) per region at a time.
const maxActivePerRegion = 1

// Proposal is the EventProposal wire form (proto EventProposal, JSON-encoded).
// Params holds the 付録B.2 body — region_tags / reason_tags /
// requested_intensity / start_after_sec / start_before_sec.
type Proposal struct {
	ProposalID string          `json:"proposal_id"`
	TemplateID string          `json:"template_id"`
	WorldID    string          `json:"world_id"`
	RegionID   string          `json:"region_id"`
	Params     json.RawMessage `json:"params"`
	// Score is a normalized rule-evaluation ratio in [0,1], not a currency or a
	// quantity — proto EventProposal types it as double. MVP 13.1's int64 rule
	// does not apply.
	Score float64 `json:"score"` //nolint:forbidigo
}

// proposalParams is the subset of 付録B.2 the approval path validates. Concrete
// spawn counts / budgets / coordinates are deliberately absent: the LLM never
// decides those (基本設計 8.2) — the rule engine and the DS do.
type proposalParams struct {
	// RequestedIntensity is a normalized [0,1] ratio (付録B.2), not a currency or
	// a quantity — MVP 13.1's int64 rule does not apply. Pointer so a missing
	// field is distinguishable from an explicit 0.
	RequestedIntensity *float64 `json:"requested_intensity"` //nolint:forbidigo
}

// Approver subscribes to world event proposals, runs the DB-decidable subset of
// the 10.4 approval checks, and registers or rejects each one (3.6). The DS runs
// the load/locality checks it alone can answer (08A 3.3).
type Approver struct {
	Store   registrar
	Results *ResultPublisher
	nc      *nats.Conn
	sub     *nats.Subscription
}

// registrar is the store surface the Approver needs, narrowed so unit tests can
// substitute a fake without a live Postgres.
type registrar interface {
	RegisterWorldEvent(ctx context.Context, proposalID, templateID, worldID, regionID string, params []byte) (string, error)
	CountActiveInRegion(ctx context.Context, worldID, regionID string) (int, error)
	LastWorldEventAt(ctx context.Context, worldID, templateID string) (time.Time, bool, error)
	ActiveTemplateVersion(ctx context.Context, templateID string) (int32, bool, error)
}

// NewApprover wires an Approver to a NATS connection.
func NewApprover(nc *nats.Conn, st registrar, results *ResultPublisher) *Approver {
	return &Approver{Store: st, Results: results, nc: nc}
}

// Start subscribes to worldevent.proposal.*.
func (a *Approver) Start(ctx context.Context) error {
	sub, err := a.nc.Subscribe(ProposalSubject, func(msg *nats.Msg) {
		a.handle(ctx, msg.Data)
	})
	if err != nil {
		return fmt.Errorf("worldevent: subscribe %s: %w", ProposalSubject, err)
	}
	a.sub = sub
	log.Printf("worldevent: approver subscribed to %s", ProposalSubject)
	return nil
}

// Stop unsubscribes.
func (a *Approver) Stop() {
	if a.sub != nil {
		if err := a.sub.Unsubscribe(); err != nil {
			log.Printf("worldevent: unsubscribe: %v", err)
		}
		a.sub = nil
	}
}

func (a *Approver) handle(ctx context.Context, data []byte) {
	var p Proposal
	if err := json.Unmarshal(data, &p); err != nil {
		log.Printf("worldevent: drop malformed proposal (%d bytes)", len(data))
		return
	}
	if p.ProposalID == "" || p.WorldID == "" {
		log.Printf("worldevent: drop proposal missing proposal_id/world_id")
		return
	}

	reason, err := a.check(ctx, p)
	if err != nil {
		// A check could not be evaluated (DB trouble). Do not reject on a
		// transient failure — say nothing and let the next evaluation retry.
		log.Printf("worldevent: approval check failed for %s: %v", p.ProposalID, err)
		return
	}
	if reason != "" {
		log.Printf("worldevent: rejected proposal %s (%s): %s", p.ProposalID, p.TemplateID, reason)
		a.Results.PublishRejected(p, reason)
		return
	}

	id, err := a.Store.RegisterWorldEvent(ctx, p.ProposalID, p.TemplateID, p.WorldID, p.RegionID, p.Params)
	if err != nil {
		log.Printf("worldevent: register approved proposal %s: %v", p.ProposalID, err)
		return
	}
	log.Printf("worldevent: approved proposal %s (%s) -> instance %s", p.ProposalID, p.TemplateID, id)
	a.Results.PublishApproved(p, id)
}

// check runs the DB-decidable 10.4 checks and returns a reason_code, or "" when
// the proposal is admissible. A non-nil error means a check could not run.
func (a *Approver) check(ctx context.Context, p Proposal) (string, error) {
	if !AllowedTemplates[p.TemplateID] {
		return ReasonUnknownTemplate, nil
	}
	if err := validateParams(p.Params); err != nil {
		return ReasonInvalidParams, nil
	}

	// Template Version: the template must still be active in action_templates.
	if _, ok, err := a.Store.ActiveTemplateVersion(ctx, p.TemplateID); err != nil {
		return "", err
	} else if !ok {
		return ReasonTemplateInactive, nil
	}

	// 同一 Region 競合: at most one live event per region.
	n, err := a.Store.CountActiveInRegion(ctx, p.WorldID, p.RegionID)
	if err != nil {
		return "", err
	}
	if n >= maxActivePerRegion {
		return ReasonRegionConflict, nil
	}

	// 同種 Cooldown: the same template may not re-fire within its window.
	last, ok, err := a.Store.LastWorldEventAt(ctx, p.WorldID, p.TemplateID)
	if err != nil {
		return "", err
	}
	if ok && time.Since(last) < Cooldowns[p.TemplateID] {
		return ReasonCooldown, nil
	}
	return "", nil
}

// validateParams enforces the 付録B.2 bounds the DB cannot express. Intensity is
// the only LLM-authored numeric that reaches the rule engine, so it is range-
// checked here as well as in the worker's schema (defence in depth, 3.3).
func validateParams(raw []byte) error {
	if len(raw) == 0 {
		return nil
	}
	var pp proposalParams
	if err := json.Unmarshal(raw, &pp); err != nil {
		return fmt.Errorf("worldevent: params: %w", err)
	}
	if pp.RequestedIntensity != nil && (*pp.RequestedIntensity < 0 || *pp.RequestedIntensity > 1) {
		return fmt.Errorf("worldevent: requested_intensity %v out of [0,1]", *pp.RequestedIntensity)
	}
	return nil
}
