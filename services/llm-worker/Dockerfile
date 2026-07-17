# syntax=docker/dockerfile:1
# LLM Worker サービス（Python / NATS consumer）— マルチステージ・非 root（3.8）。
# uv.lock を正本に依存を固定する。pyproject から直接 `pip install .` すると
# 推移依存が浮動し、復旧/負荷の再現性が崩れる（落とし穴「RC の image を latest
# 参照にしない」と同じ理由）。
FROM ghcr.io/astral-sh/uv:0.11.28 AS uv

FROM python:3.11-slim AS deps
COPY --from=uv /uv /bin/uv
WORKDIR /app
# uv.lock（コミット済み）から hash 付き requirements を書き出す。--frozen で
# lock の再解決を禁止し、lock と pyproject がずれていればビルドを失敗させる。
COPY pyproject.toml uv.lock ./
RUN uv export --frozen --no-dev --no-emit-project -o /tmp/requirements.txt

FROM python:3.11-slim AS base
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
WORKDIR /app

# --require-hashes で lock 記載の hash と一致しない wheel を拒否する。
COPY --from=deps /tmp/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir --require-hashes -r /tmp/requirements.txt \
    && rm -f /tmp/requirements.txt

# アプリ自身は wheel 化せず配置するだけにする。`pip install .` はビルド分離で
# hatchling を都度取得してしまい、せっかく固定した依存の外側が浮動するため。
# WORKDIR=/app が import ルートになるので worker パッケージはそのまま解決できる。
COPY worker ./worker

# 非 root 実行（3.8）。/app は読み取りだけできれば足りるので所有権は root のまま。
RUN useradd --system --no-create-home --uid 10002 llmworker
USER llmworker

EXPOSE 8084
# 標準ライブラリだけで /readyz を叩く（llm-worker は 8084）。依存断（NATS）の間は
# 503→unhealthy になるが、これは意図した挙動で再起動の引き金にはしない。
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
    CMD ["python", "-c", "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://localhost:8084/readyz',timeout=3).status==200 else 1)"]
CMD ["python", "-m", "worker.main"]
