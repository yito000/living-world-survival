# syntax=docker/dockerfile:1
# Auth サービス（Go）— マルチステージビルド
FROM golang:1.22-bookworm AS build
WORKDIR /src

# 依存を先に解決してレイヤキャッシュを効かせる
COPY . .
# go.sum が未コミットでも成立するよう tidy で依存グラフを確定してからビルド。
RUN go mod tidy
RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
    go build -trimpath -ldflags="-s -w" -o /out/authd ./cmd/authd

FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/authd /authd
EXPOSE 8081
USER nonroot:nonroot
ENTRYPOINT ["/authd"]
