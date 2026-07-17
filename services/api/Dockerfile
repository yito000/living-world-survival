# syntax=docker/dockerfile:1
# API サービス（Go）— マルチステージビルド。
# M2: api は生成 gRPC/protobuf スタブ（services/gen/go）と共通ライブラリ
# （services/common）を replace で参照するため、ビルドコンテキストはリポジトリ直下
# （compose の context: ..）。3 module すべてを取り込む。
FROM golang:1.22-bookworm AS build
WORKDIR /src

# replace 先（../gen/go, ../common）が解決できる階層に各 module を配置する。
COPY services/gen/go ./services/gen/go
COPY services/common ./services/common
COPY services/api ./services/api
WORKDIR /src/services/api
# 依存はコミット済み go.sum の通りに取得する（3.8 / 落とし穴「復旧・負荷の再現性」）。
# ここで go mod tidy を走らせると、イメージビルド時点の解決へ依存グラフが動いて
# RC の再現性が崩れるため使わない。
RUN go mod download
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
# distroless/static には shell も curl も無いため、バイナリ自身の自己診断モードで
# /readyz を叩く（3.8）。依存断（DB/NATS）の間は 503→unhealthy になるが、これは
# 意図した挙動（トラフィックを止める）で再起動の引き金にはしない。
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
    CMD ["/apid", "-healthcheck"]
ENTRYPOINT ["/apid"]
