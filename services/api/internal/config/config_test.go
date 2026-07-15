package config

import (
	"testing"
	"time"
)

func TestLoadDefaults(t *testing.T) {
	cfg := Load()
	if cfg.HTTPAddr != ":8082" {
		t.Errorf("HTTPAddr default: got %q", cfg.HTTPAddr)
	}
	if cfg.GRPCAddr != ":8092" {
		t.Errorf("GRPCAddr default: got %q", cfg.GRPCAddr)
	}
	if cfg.OutboxInterval != time.Second {
		t.Errorf("OutboxInterval default: got %s", cfg.OutboxInterval)
	}
}

func TestLoadOverrides(t *testing.T) {
	t.Setenv("API_GRPC_PORT", "19092")
	t.Setenv("OUTBOX_RELAY_INTERVAL", "500ms")
	cfg := Load()
	if cfg.GRPCAddr != ":19092" {
		t.Errorf("GRPCAddr override: got %q", cfg.GRPCAddr)
	}
	if cfg.OutboxInterval != 500*time.Millisecond {
		t.Errorf("OutboxInterval override: got %s", cfg.OutboxInterval)
	}
}

func TestDurationOrRejectsInvalid(t *testing.T) {
	t.Setenv("OUTBOX_RELAY_INTERVAL", "not-a-duration")
	if got := Load().OutboxInterval; got != time.Second {
		t.Errorf("invalid duration should fall back to 1s, got %s", got)
	}
}
