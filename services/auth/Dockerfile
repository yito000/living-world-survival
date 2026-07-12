# syntax=docker/dockerfile:1
# Auth サービス（Go）— マルチステージビルド。
# M1: auth は生成 gRPC/protobuf スタブ（services/gen/go）を replace で参照するため、
# ビルドコンテキストはリポジトリ直下（compose の context: ..）。両モジュールを取り込む。
FROM golang:1.22-bookworm AS build
WORKDIR /src

# 生成スタブ module と auth module を配置（replace: ../gen/go が解決できる階層）。
COPY services/gen/go ./services/gen/go
COPY services/auth ./services/auth
WORKDIR /src/services/auth
# go.sum が未コミットでも成立するよう tidy で依存グラフを確定してからビルド。
RUN go mod tidy
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
    go build -trimpath -ldflags="-s -w" -o /out/authd ./cmd/authd

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/authd /authd
# 8081: REST/health, 9091: 内部 gRPC MatchmakingService。
EXPOSE 8081 9091
USER nonroot:nonroot
ENTRYPOINT ["/authd"]
