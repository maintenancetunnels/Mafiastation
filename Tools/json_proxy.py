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

# Ollama honours a full JSON schema in `format` and constrains decoding to it, so even a
# small model must emit exactly this shape. The NPC dialogue path is the strictest consumer;
# detect it by its signature keys and pin the schema so 1.5b models stop failing the parser.
DIALOGUE_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["shouldSpeak", "text", "tone"],
    "properties": {
        "shouldSpeak": {"type": "boolean"},
        "text": {"type": "string", "maxLength": 240},
        "tone": {"type": "string",
                 "enum": ["neutral", "curious", "warm", "wary", "urgent", "hostile"]},
    },
}


def _looks_like_dialogue(payload):
    try:
        blob = json.dumps(payload.get("messages", []))
        return "shouldSpeak" in blob and "tone" in blob
    except Exception:
        return False


_TONES = ["neutral", "curious", "warm", "wary", "urgent", "hostile"]
_TONE_ALIASES = {
    "friendly": "warm", "happy": "warm", "kind": "warm", "cheerful": "warm",
    "nervous": "wary", "cautious": "wary", "suspicious": "wary", "afraid": "wary",
    "angry": "hostile", "aggressive": "hostile", "annoyed": "hostile",
    "excited": "urgent", "alarmed": "urgent", "panicked": "urgent",
    "inquisitive": "curious", "interested": "curious",
    "calm": "neutral", "casual": "neutral", "flat": "neutral",
}


def _coerce_dialogue(content):
    """Guarantee the dialogue object matches the strict parser: 3 keys, tone in the enum."""
    try:
        obj = json.loads(content)
    except Exception:
        return content
    if not isinstance(obj, dict):
        return content

    raw_tone = str(obj.get("tone", "neutral")).strip().lower()
    tone = raw_tone if raw_tone in _TONES else None
    if tone is None:  # substring match: "friendly and excited" -> warm
        for word in re.findall(r"[a-z]+", raw_tone):
            if word in _TONES:
                tone = word
                break
            if word in _TONE_ALIASES:
                tone = _TONE_ALIASES[word]
                break
    if tone is None:
        tone = "neutral"

    should = bool(obj.get("shouldSpeak", True))
    text = str(obj.get("text", ""))
    if not should:
        text = ""
    return json.dumps({"shouldSpeak": should, "text": text[:240], "tone": tone})


def _openai_envelope(content):
    return json.dumps({
        "id": "proxy-dialogue",
        "object": "chat.completion",
        "model": "proxy",
        "choices": [{
            "index": 0,
            "message": {"role": "assistant", "content": content},
            "finish_reason": "stop",
        }],
        "usage": {},
    }).encode("utf-8")


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length)

        # Dialogue requests need real constrained decoding, which only ollama's native
        # /api/chat honours (the /v1 endpoint ignores the schema). Route those there and
        # translate the reply back into the OpenAI shape the caller expects.
        try:
            payload = json.loads(raw)
        except Exception:
            payload = None

        if payload is not None and _looks_like_dialogue(payload):
            data = self._forward_native_dialogue(payload)
            if data is not None:
                self._send_json(data)
                return
            # fall through to the generic path if the native call failed

        try:
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

        self._send_json(data)

    def _send_json(self, data):
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _forward_native_dialogue(self, payload):
        native = {
            "model": payload.get("model"),
            "messages": payload.get("messages", []),
            "format": DIALOGUE_SCHEMA,
            "stream": False,
            "options": {"temperature": payload.get("temperature", 0.7)},
        }
        req = urllib.request.Request(
            UPSTREAM + "/api/chat", data=json.dumps(native).encode("utf-8"),
            headers={"Content-Type": "application/json"}, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=180) as resp:
                obj = json.loads(resp.read())
            content = obj.get("message", {}).get("content", "")
            return _openai_envelope(_coerce_dialogue(content))
        except Exception:
            return None


if __name__ == "__main__":
    print(f"json_proxy: {LISTEN[0]}:{LISTEN[1]} -> {UPSTREAM} (forcing json_object)")
    ThreadingHTTPServer(LISTEN, Handler).serve_forever()
