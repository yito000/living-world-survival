# LLM Worker サービス（Python / NATS consumer）
FROM python:3.11-slim AS base
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
WORKDIR /app

COPY pyproject.toml ./
COPY worker ./worker
RUN pip install --no-cache-dir .

EXPOSE 8084
CMD ["python", "-m", "worker.main"]
