# syntax=docker/dockerfile:1
# API サービス（Go）— マルチステージビルド
FROM golang:1.22-bookworm AS build
WORKDIR /src

COPY . .
# go.sum が未コミットでも成立するよう tidy で依存グラフを確定してからビルド。
RUN go mod tidy
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
    go build -trimpath -ldflags="-s -w" -o /out/apid ./cmd/apid

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/apid /apid
EXPOSE 8082
USER nonroot:nonroot
ENTRYPOINT ["/apid"]
