package main

import (
	"crypto/rand"
	"fmt"
	"os"
	"sort"
	"strconv"
	"sync"
	"time"
)

// opStats は 1 操作種別のカウンタと、参考用のクライアント側レイテンシ。
//
// 注意（10B 6章）: ここで測るのはハーネス側 RTT であり、Gate の入力ではない。
// Gate は API の grpc_server_handling_seconds と auth の ds_tick_seconds で判定する。
// RTT を tick_ms や「サーバー処理時間」と混同しないこと。
type opStats struct {
	Attempted int64  `json:"attempted"`
	Succeeded int64  `json:"succeeded"`
	Failed    int64  `json:"failed"`
	Rejected  int64  `json:"rejected"` // 業務的に正常な非成功（OUT_OF_STOCK 等）
	P50MS     int64  `json:"client_latency_p50_ms"`
	P95MS     int64  `json:"client_latency_p95_ms"`
	MaxMS     int64  `json:"client_latency_max_ms"`
	LastError string `json:"last_error,omitempty"`

	samples []time.Duration
}

type stats struct {
	mu sync.Mutex
	m  map[string]*opStats
}

func newStats() *stats { return &stats{m: map[string]*opStats{}} }

func (s *stats) get(kind string) *opStats {
	st, ok := s.m[kind]
	if !ok {
		st = &opStats{}
		s.m[kind] = st
	}
	return st
}

// record は 1 操作の結果を積む。outcome は "ok" / "rejected" / "error"。
func (s *stats) record(kind string, d time.Duration, outcome string, err error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	st := s.get(kind)
	st.Attempted++
	switch outcome {
	case "ok":
		st.Succeeded++
	case "rejected":
		st.Rejected++
	default:
		st.Failed++
		if err != nil {
			st.LastError = err.Error()
		}
	}
	st.samples = append(st.samples, d)
}

func (s *stats) snapshot() map[string]*opStats {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make(map[string]*opStats, len(s.m))
	for k, st := range s.m {
		samples := append([]time.Duration(nil), st.samples...)
		sort.Slice(samples, func(i, j int) bool { return samples[i] < samples[j] })
		cp := *st
		cp.samples = nil
		cp.P50MS = pctMS(samples, 50)
		cp.P95MS = pctMS(samples, 95)
		if len(samples) > 0 {
			cp.MaxMS = int64(samples[len(samples)-1] / time.Millisecond)
		}
		out[k] = &cp
	}
	return out
}

// pctMS は昇順 samples の p パーセンタイルをミリ秒（整数）で返す。参考値なので
// 補間はせず nearest-rank で足りる（Gate 判定はサーバー側ヒストグラムが担う）。
func pctMS(samples []time.Duration, p int) int64 {
	if len(samples) == 0 {
		return 0
	}
	idx := (len(samples)*p + 99) / 100
	if idx > 0 {
		idx--
	}
	return int64(samples[idx] / time.Millisecond)
}

func envOr(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func envInt(k string, def int) int {
	v := os.Getenv(k)
	if v == "" {
		return def
	}
	n, err := strconv.Atoi(v)
	if err != nil {
		fatal("%s=%q は整数ではありません: %v", k, v, err)
	}
	return n
}

func envDur(k string, def time.Duration) time.Duration {
	v := os.Getenv(k)
	if v == "" {
		return def
	}
	d, err := time.ParseDuration(v)
	if err != nil {
		fatal("%s=%q は duration ではありません（例 60s, 4m）: %v", k, v, err)
	}
	return d
}

// newUUID は UUIDv4 を返す（mm-smoke と同じ理由で外部依存を足さない）。
func newUUID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(err)
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}
