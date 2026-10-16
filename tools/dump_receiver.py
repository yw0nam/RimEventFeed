"""Receives the RimEventFeed research mod's events, prints each one and appends it to events.jsonl."""

import json
import sys
from datetime import datetime
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

OUT = Path(__file__).with_name("events.jsonl")


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        received = datetime.now().isoformat(timespec="milliseconds")
        try:
            event = json.loads(raw)
        except json.JSONDecodeError as e:
            print(f"--- {received} invalid JSON ({e}): {raw!r}", flush=True)
            event = {"invalid": raw.decode("utf-8", "replace")}
        else:
            print(f"--- {received} {self.path} {len(raw)} bytes", flush=True)
            print(json.dumps(event, ensure_ascii=False, indent=2), flush=True)
        with OUT.open("a", encoding="utf-8") as f:
            f.write(json.dumps({"receivedAt": received, "event": event}, ensure_ascii=False) + "\n")
        self.send_response(204)
        self.end_headers()

    def log_message(self, format, *args):  # silence the default per-request access log
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8799
    print(f"listening on http://127.0.0.1:{port}, writing {OUT}", flush=True)
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()
