"""Tiny translator: forces Ollama to emit clean JSON for the AI pilot lab.

The pilot lab sends OpenAI-style chat completions but does not set response_format,
so weak local models wrap their JSON in markdown and fail the strict parser. This
proxy injects response_format=json_object into every request and forwards to Ollama,
then strips any stray markdown fences from the reply as a belt-and-suspenders measure.

    python3 json_proxy.py            # listens on 127.0.0.1:11500 -> 127.0.0.1:11434
"""
import json
import re
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LISTEN = ("127.0.0.1", 11500)
UPSTREAM = "http://127.0.0.1:11434"
FENCE = re.compile(r"^\s*```(?:json)?\s*|\s*```\s*$", re.IGNORECASE)


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length)
        try:
            payload = json.loads(raw)
            payload["response_format"] = {"type": "json_object"}
            body = json.dumps(payload).encode("utf-8")
        except Exception:
            body = raw

        req = urllib.request.Request(
            UPSTREAM + self.path, data=body,
            headers={"Content-Type": "application/json"}, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=180) as resp:
                data = resp.read()
        except Exception as exc:
            self.send_response(502)
            self.end_headers()
            self.wfile.write(str(exc).encode("utf-8"))
            return

        # Belt and suspenders: strip markdown fences from the message content.
        try:
            obj = json.loads(data)
            for choice in obj.get("choices", []):
                msg = choice.get("message", {})
                if isinstance(msg.get("content"), str):
                    msg["content"] = FENCE.sub("", msg["content"]).strip()
            data = json.dumps(obj).encode("utf-8")
        except Exception:
            pass

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    print(f"json_proxy: {LISTEN[0]}:{LISTEN[1]} -> {UPSTREAM} (forcing json_object)")
    ThreadingHTTPServer(LISTEN, Handler).serve_forever()
