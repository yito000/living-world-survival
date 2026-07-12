// buf generate が出力する gRPC/protobuf スタブの Go モジュール（M1, 2.1）。
// auth など各サービスは replace で相対参照する（../gen/go）。
module living-world-survival/services/gen/go

go 1.22

require (
	google.golang.org/grpc v1.68.1
	google.golang.org/protobuf v1.35.2
)

require (
	golang.org/x/net v0.29.0 // indirect
	golang.org/x/sys v0.25.0 // indirect
	golang.org/x/text v0.18.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20240903143218-8af14fe29dc1 // indirect
)
