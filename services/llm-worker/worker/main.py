"""LLM worker entrypoint (M0 skeleton).

Subscribes to ``ai.decision.request`` on NATS and replies with a *mocked*
ActionDecision (LLM calls are mocked in M0; ``LLM_MOCK=1``). A tiny HTTP
health server runs in a background thread so ``make smoke`` can confirm the
worker is alive.

Heavy/model work is intentionally kept out of the message callback path
(BSD 4.3 / R7); in M0 the mock is trivial.
"""

from __future__ import annotations

import asyncio
import json
import os
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from typing import Any

try:  # pragma: no cover - import guard
    import nats
except Exception:  # pragma: no cover
    nats = None  # type: ignore[assignment]

DECISION_REQUEST_SUBJECT = "ai.decision.request"


def build_mock_decision(request: dict[str, Any]) -> dict[str, Any]:
    """Produce a deterministic mocked ActionDecision for a decision request.

    Pure function — no I/O — so it is unit-testable without a broker.
    """
    actor_id = request.get("actor_id", "unknown")
    state_version = int(request.get("state_version", 0))
    return {
        "decision_id": f"mock-{actor_id}-{state_version}",
        "actor_id": actor_id,
        "state_version": state_version,
        "template_id": "idle.wait",
        "steps": [{"action_template_id": "idle.wait", "params": {}}],
        "mock": True,
    }


def _result_subject(request: dict[str, Any]) -> str:
    server_id = request.get("server_id", "unknown")
    return f"ai.decision.result.{server_id}"


class _HealthHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 (stdlib naming)
        if self.path in ("/healthz", "/readyz"):
            body = json.dumps({"status": "ok", "service": "llm-worker"}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, *_args: Any) -> None:  # silence per-request logging
        return


def start_health_server(port: int) -> HTTPServer:
    server = HTTPServer(("0.0.0.0", port), _HealthHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server


async def run() -> None:
    port = int(os.getenv("LLM_WORKER_PORT", "8084"))
    start_health_server(port)

    url = os.getenv("NATS_URL", "nats://localhost:4222")
    if nats is None:
        # No broker library available; keep the health server alive.
        while True:  # pragma: no cover
            await asyncio.sleep(3600)

    nc = await nats.connect(url, max_reconnect_attempts=-1, reconnect_time_wait=2)

    async def on_request(msg: Any) -> None:
        try:
            request = json.loads(msg.data.decode())
        except json.JSONDecodeError:
            return
        decision = build_mock_decision(request)
        await nc.publish(_result_subject(request), json.dumps(decision).encode())

    await nc.subscribe(DECISION_REQUEST_SUBJECT, cb=on_request)
    print(f"llm-worker: subscribed to {DECISION_REQUEST_SUBJECT} at {url}")  # noqa: T201

    stop = asyncio.Event()
    await stop.wait()  # run until cancelled


def main() -> None:
    try:
        asyncio.run(run())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
