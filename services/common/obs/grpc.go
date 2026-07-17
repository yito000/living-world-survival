package obs

import (
	"context"
	"path"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
)

// correlationMD は gRPC metadata 上の相関 ID キー（HTTP の X-Correlation-Id と対）。
// gRPC の metadata キーは小文字でなければならない。
const correlationMD = "x-correlation-id"

// UnaryServerInterceptor は相関 ID の引き継ぎ・レイテンシ計測・アクセスログを付ける。
// 10B 3.1 の「購入 P95≤500ms（DB commit 含む）」はこの計測点で見る（DS からの
// CommitPurchase 呼び出しが handler を抜けるまで＝commit 済み）。
func UnaryServerInterceptor() grpc.UnaryServerInterceptor {
	return func(ctx context.Context, req any, info *grpc.UnaryServerInfo,
		handler grpc.UnaryHandler) (any, error) {
		cid := correlationFromMD(ctx)
		if cid == "" {
			cid = NewCorrelationID()
		}
		ctx = WithFields(ctx, Fields{CorrelationID: cid})

		start := time.Now()
		resp, err := handler(ctx, req)
		elapsed := time.Since(start)

		method := path.Base(info.FullMethod)
		code := status.Code(err)
		GRPCDuration.WithLabelValues(method, code.String()).Observe(elapsed.Seconds())

		l := L(ctx).With("method", method, "code", code.String(),
			"duration_ms", elapsed.Milliseconds())
		if err != nil && code != codes.NotFound {
			// err.Error() は握り潰さず出す。gRPC のエラー文言に秘匿値を混ぜないのは
			// 各ハンドラの責務（MVP-SEC-002）。
			l.Warn("grpc request failed", "error", err.Error())
		} else {
			l.Info("grpc request")
		}
		return resp, err
	}
}

func correlationFromMD(ctx context.Context) string {
	md, ok := metadata.FromIncomingContext(ctx)
	if !ok {
		return ""
	}
	if v := md.Get(correlationMD); len(v) > 0 {
		return v[0]
	}
	return ""
}

// WithCorrelation は発信側 context に相関 ID を載せる（サービス間で ID を貫くため）。
func WithCorrelation(ctx context.Context, cid string) context.Context {
	if cid == "" {
		return ctx
	}
	return metadata.AppendToOutgoingContext(ctx, correlationMD, cid)
}
