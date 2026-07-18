# Mafiastation LLM gateway and advisory moderation

This subsystem provides a provider-neutral, server-only JSON LLM gateway plus an optional
moderation consumer. It is deliberately advisory: model output can create an incident record and
notify admins, but it cannot punish, ban, mute, alter a sentence, run a command, or invoke an
arbitrary game action.

The same `MafiaLlmGatewaySystem.CompleteStructuredAsync` contract is intended for future NPC and
event-director consumers. Those consumers must submit a schema, validate the response against
game-owned allowlists, and hand only bounded goals to the existing HTN/utility AI. An LLM should
never directly choose entity-system calls or execute player-provided instructions.

## Player data flow

Enabling moderation sends accepted chat text, timestamps, channel kinds, character/account names,
stable user IDs, and non-admin persistent-security penalty reasons to the configured endpoint.
With a remote endpoint, that data leaves the game server. Operators must evaluate their provider,
retention agreement, player notice/consent, and applicable privacy rules before enabling it; use a
trusted local endpoint when remote processing is not acceptable. Provider error bodies and API
keys are never written to moderation logs.

## Enabling Anthropic

Keep the key outside checked-in configuration:

```powershell
$env:MAFIA_LLM_KEY = "..."
```

Then set these server CVars:

```toml
[mafia.llm]
enabled = true
provider = "anthropic"
endpoint = "https://api.anthropic.com/v1/messages"
model = "claude-haiku-4-5"

[mafia.moderation]
enabled = true
```

`MAFIA_LLM_KEY` takes precedence over the confidential `mafia.llm.api_key` CVar.

## OpenAI-compatible and local models

OpenAI, Ollama, vLLM, LM Studio, and other compatible servers use:

```toml
[mafia.llm]
enabled = true
provider = "openai-compatible"
endpoint = "http://127.0.0.1:11434/v1/chat/completions"
model = "your-model"
allow_unauthenticated = true
response_format = "json_object"
```

Only enable unauthenticated mode for a trusted endpoint. If a compatibility server rejects
`response_format`, set it to `"none"`. Servers with strict OpenAI structured-output support can use
`"json_schema"`.

## Cost and failure behavior

- Requests are batched every 30 seconds or 40 messages by default.
- All consumers share per-minute and per-round token guards.
- Missing credentials or a disabled gateway are silent.
- Transport/provider failures are logged at most once every five minutes.
- Only cited evidence from validated flags is written to
  `moderation_incidents.json` beneath the server user-data directory.
- `mafia.moderation.allow_automatic_actions` is reserved; no automatic punitive action exists.
