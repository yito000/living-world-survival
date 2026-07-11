# WorldState サービス（FastAPI / Python）
FROM python:3.11-slim AS base
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1
WORKDIR /app

COPY pyproject.toml ./
COPY app ./app
RUN pip install --no-cache-dir .

EXPOSE 8083
# WORLDSTATE_PORT を尊重（既定 8083）
CMD ["sh", "-c", "uvicorn app.main:app --host 0.0.0.0 --port ${WORLDSTATE_PORT:-8083}"]
