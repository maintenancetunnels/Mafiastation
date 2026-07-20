# Mafiastation AI Pilot Lab

`AiPilotLab` drives explicitly enabled headless SS14 clients on a loopback test server. It sends
bounded JSON actions over a current-user named pipe; the client bridge turns those requests into
the same input commands and interaction systems used by a normal player.

This is a local testing tool, not a server bot API. Both the client and server gates default to
off. The server additionally requires a loopback connection and an account allowlist.

The normal Robust content sandbox remains enabled. Operating-system pipe and JSON work lives in a
small preloaded helper that is unavailable in full-release builds and remains inert unless the
immutable process arguments specify headless mode, a strict loopback game address, the trusted
bridge flag, and the exact enabled pipe CVars. The launcher supplies that proof and disables
texture preloading for the supported headless path.

## Build and verify

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet test .\Tools\AiPilotLab.Tests\AiPilotLab.Tests.csproj
& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- validate-scenario `
  --file .\Tools\AiPilotLab\Scenarios\basic-movement.json
```

## Run a deterministic local scenario

Configure the local server:

```text
mafia.ai_pilot.server_enabled true
mafia.ai_pilot.allowed_accounts Pilot1
mafia.ai_pilot.allow_join true
mafia.ai_pilot.allow_speech false
```

Then launch a built headless client and run the scenario in one command:

```powershell
& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- launch `
  --client .\bin\Content.Client\Content.Client.dll `
  --server 127.0.0.1:1212 `
  --scenario .\Tools\AiPilotLab\Scenarios\basic-movement.json
```

The launcher enables each client bridge, assigns a unique pipe and username, waits for every
bridge, runs bot steps concurrently, saves JSONL actions and a metric summary, and terminates only
the client processes it started.

Robust optional authentication exposes local usernames as `localhost@Pilot1`; the server accepts
that documented loopback alias for an allowlist entry of `Pilot1`. Give simultaneous pilots unique
allowlisted usernames. The explicit `join` operation is idempotent when a lobby-disabled test
server has already attached the client.

## Run an ordinary LLM crew against a hidden human antagonist

Crew mode launches real connected headless clients, deterministically late-joins each one into a
reviewed ordinary job, and then gives each client an independent bounded model loop. The roster
contains job and temperament only; it has no mission, antagonist, or operator-identity field.

Configure a loopback server with an active round and enough slots for the requested jobs:

```text
mafia.ai_pilot.server_enabled true
mafia.ai_pilot.allowed_accounts CrewJanitor,CrewSecurity,CrewDoctor,CrewEngineer,CrewCargo
mafia.ai_pilot.allow_join true
mafia.ai_pilot.allowed_jobs Janitor,SecurityOfficer,MedicalDoctor,StationEngineer,CargoTechnician
mafia.ai_pilot.allow_speech true
```

Validate and launch the supplied five-person roster against a cheap local OpenAI-compatible model:

```powershell
& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- validate-crew `
  --file .\Tools\AiPilotLab\Crews\standard-shift.json

& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- launch `
  --client .\bin\Content.Client\Content.Client.dll `
  --server 127.0.0.1:1212 `
  --crew .\Tools\AiPilotLab\Crews\standard-shift.json `
  --provider openai-compatible `
  --endpoint http://127.0.0.1:11434/v1/chat/completions `
  --model cheap-local-model `
  --startup-timeout-seconds 180 `
  --allow-speech `
  --client-cvar mafia.ai_pilot.client_allow_speech=true
```

Join separately as the antagonist. Do not put that fact in the roster or model endpoint. Each crew
model receives only its reviewed job brief, its own bounded visible observation, and chat messages
that its client actually received. One crew member's normal `say` or radio message can therefore
enter another member's `recentSpeech`, allowing ordinary player-to-player conversation without a
privileged bot backchannel. Job assignment is verified before a model is started and fails closed
if the server assigned anything else.

Model `say` actions choose either `channel: local` or `channel: radio`. Radio uses the ordinary `;`
common channel, so crew will answer received radio traffic and use comms for station-wide job
coordination when their normal equipment permits it.

The crew policy is intentionally conversational: agents greet, acknowledge calls, ask brief job
questions, and announce relevant work or hazards. A small self-speech timer encourages a useful
check-in after roughly 30-45 quiet seconds while explicitly discouraging consecutive or canned spam.

The current action vocabulary supports movement/path goals, pickup/drop/hand swap, ordinary
interaction, and speech. That is enough for patrols, errands, visible hazard response, basic tool
use, and coordination; complex machine, inventory, medical, and construction UIs are not yet
automated. See [crew-mode.md](../../docs/ai-pilot/crew-mode.md) for the behavioral boundary and
extension path.

## Run a cheap model policy

A local OpenAI-compatible endpoint can run without an API key:

```powershell
& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- launch `
  --client .\bin\Content.Client\Content.Client.dll `
  --server 127.0.0.1:1212 `
  --count 2 `
  --goal "Meet near arrivals and pick up a nearby toolbox" `
  --provider openai-compatible `
  --endpoint http://127.0.0.1:11434/v1/chat/completions `
  --model cheap-local-model `
  --duration-seconds 120
```

For a remote HTTPS endpoint, put the key in `MAFIA_LLM_KEY` (or name a different environment
variable with `--api-key-env`). Keys are never accepted on the command line or written to the
action log. Anthropic's Messages API is also supported with `--provider anthropic` and its full
endpoint URL.

The model is invoked at most once per decision interval. After it starts a deterministic goal,
the runner polls the ordinary executor without model calls until the goal completes, fails,
stalls, or is cancelled. Output is revalidated against both an action allowlist and the current
capacity snapshot, and target IDs must appear in the latest bounded observation. Add
`--allow-speech` only when both client and server speech gates are enabled.

Before its first model call, the runner also preflights bridge authorization. A transient
"not checked" state is polled briefly and followed by a fresh observation; a disabled gate,
non-loopback connection, or non-allowlisted account fails with the server reason without spending
model compute.

OpenAI-compatible requests use standard JSON-object response mode by default. If a model still
wraps or misnames an otherwise recoverable action, the policy safely extracts the first balanced
JSON object and makes at most one repair request before the ordinary consecutive-error limit
applies. Set `--model-repair-attempts 0` to disable repair or
`--disable-json-object-mode` only for an older compatible endpoint that rejects
`response_format`. `--startup-timeout-seconds` accepts 1-600 seconds; raise it when several
headless clients share a busy development machine.

Repeated local logins can leave Robust collision suffixes on reused account names. For generated
`--count` launches, `--username-start-index 5` starts the whole identity set at `Pilot5`
(`pilot5`, `mafiastation-pilot-5`, and so on). Those exact account names must already be present in
the server pilot allowlist; this option does not broaden authorization.

## Record and replay

Every scenario and model run produces line-delimited JSON records for requests, responses, model
decisions, timings, and errors. Replay preserves relative timing (adjustable with `--time-scale`):

```powershell
& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- replay `
  --file .\artifacts\ai-pilot\RUN\actions.jsonl `
  --pipe mafiastation-pilot-1 `
  --time-scale 2
```

Replay skips `say`, `whisper`, `join`, and `ready` by default. Their separate
`--allow-speech` and `--allow-lifecycle` switches make potentially surprising replays explicit.

See [protocol.md](../../docs/ai-pilot/protocol.md) and
[safety.md](../../docs/ai-pilot/safety.md) for the bridge contract and threat model. The full
player-pilot/server-NPC split is described in
[hybrid-control.md](../../docs/ai-pilot/hybrid-control.md).

## Review moderation false positives

The same executable provides a local, advisory-only evaluation console over the server's existing
incident audit. Human labels are written to a separate atomic review document; the incident audit
is never modified.

```powershell
& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- moderation `
  --action list --incidents .\moderation_incidents.json --unreviewed

& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- moderation `
  --action label --incidents .\moderation_incidents.json `
  --incident INCIDENT_GUID --verdict false-positive `
  --reviewer local-reviewer --notes "IC context made this acceptable"

& $dotnet run --project .\Tools\AiPilotLab\AiPilotLab.csproj -- moderation `
  --action summary --incidents .\moderation_incidents.json
```

Summary output compares review coverage and false-positive rate by provider, model, policy version,
and classifier category. JSON and CSV exports contain joined labels and metrics but intentionally
exclude message evidence; CSV formula-like cells are neutralized. No evaluation command changes a
player record, sends data to a model, or applies an administrative action.
