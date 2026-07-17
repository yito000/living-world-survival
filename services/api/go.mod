module living-world-survival/services/api

go 1.22

require (
	github.com/jackc/pgx/v5 v5.7.2
	github.com/nats-io/nats.go v1.37.0
	github.com/prometheus/client_golang v1.20.5
	google.golang.org/grpc v1.68.1
	living-world-survival/services/gen/go v0.0.0
)

require (
	github.com/beorn7/perks v1.0.1 // indirect
	github.com/cespare/xxhash/v2 v2.3.0 // indirect
	github.com/kylelemons/godebug v1.1.0 // indirect
	github.com/munnerz/goautoneg v0.0.0-20191010083416-a7dc8b61c822 // indirect
	github.com/prometheus/client_model v0.6.1 // indirect
	github.com/prometheus/common v0.55.0 // indirect
	github.com/prometheus/procfs v0.15.1 // indirect
)

// 生成 gRPC/protobuf スタブは同一リポジトリの相対ディレクトリを参照する（auth と同一方針, 3.1）。
replace living-world-survival/services/gen/go => ../gen/go

require (
	github.com/jackc/pgpassfile v1.0.0 // indirect
	github.com/jackc/pgservicefile v0.0.0-20240606120523-5a60cdf6a761 // indirect
	github.com/jackc/puddle/v2 v2.2.2 // indirect
	github.com/klauspost/compress v1.17.9 // indirect
	github.com/nats-io/nkeys v0.4.7 // indirect
	github.com/nats-io/nuid v1.0.1 // indirect
	golang.org/x/crypto v0.31.0 // indirect
	golang.org/x/net v0.29.0 // indirect
	golang.org/x/sync v0.10.0 // indirect
	golang.org/x/sys v0.28.0 // indirect
	golang.org/x/text v0.21.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20240903143218-8af14fe29dc1 // indirect
	google.golang.org/protobuf v1.35.2
	living-world-survival/services/common v0.0.0
)

replace living-world-survival/services/common => ../common
