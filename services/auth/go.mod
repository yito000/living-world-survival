module living-world-survival/services/auth

go 1.22

require (
	github.com/golang-jwt/jwt/v5 v5.2.1
	github.com/jackc/pgx/v5 v5.7.2
	golang.org/x/crypto v0.31.0
	google.golang.org/grpc v1.68.1
	living-world-survival/services/gen/go v0.0.0
)

// 生成 gRPC/protobuf スタブは同一リポジトリの相対ディレクトリを参照する（2.1）。
replace living-world-survival/services/gen/go => ../gen/go

require (
	github.com/jackc/pgpassfile v1.0.0 // indirect
	github.com/jackc/pgservicefile v0.0.0-20240606120523-5a60cdf6a761 // indirect
	github.com/jackc/puddle/v2 v2.2.2 // indirect
	golang.org/x/net v0.29.0 // indirect
	golang.org/x/sync v0.10.0 // indirect
	golang.org/x/sys v0.28.0 // indirect
	golang.org/x/text v0.21.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20240903143218-8af14fe29dc1 // indirect
	google.golang.org/protobuf v1.35.2 // indirect
)
