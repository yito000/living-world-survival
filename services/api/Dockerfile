# syntax=docker/dockerfile:1
# API サービス（Go）— マルチステージビルド。
# M2: api は生成 gRPC/protobuf スタブ（services/gen/go）を replace で参照するため、
# ビルドコンテキストはリポジトリ直下（compose の context: ..）。両モジュールを取り込む。
FROM golang:1.22-bookworm AS build
WORKDIR /src

# 生成スタブ module と api module を配置（replace: ../gen/go が解決できる階層）。
COPY services/gen/go ./services/gen/go
COPY services/api ./services/api
WORKDIR /src/services/api
# go.sum が未コミットでも成立するよう tidy で依存グラフを確定してからビルド。
RUN go mod tidy
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
    go build -trimpath -ldflags="-s -w" -o /out/apid ./cmd/apid

FROM gcr.io/distroless/static-debian12:nonroot
# WORKDIR=/ を明示。ITEM_DEFINITIONS_PATH=data/item_definitions.json（相対）が /data/ を指す。
WORKDIR /
COPY --from=build /out/apid /apid
# Item Definition 正本（3.8）。/data/item_definitions.json に配置。
COPY --from=build /src/services/api/data /data
# 8082: HTTP/health, 8092: 内部 gRPC WorldData/ActorState。
EXPOSE 8082 8092
USER nonroot:nonroot
ENTRYPOINT ["/apid"]
