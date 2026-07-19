# Mafiastation AI Pilot Lab

`AiPilotLab` drives explicitly enabled headless SS14 clients on a loopback test server. It sends
bounded JSON actions over a current-user named pipe; the client bridge turns those requests into
the same input commands and interaction systems used by a normal player.

This is a local testing tool, not a server bot API. Both the client and server gates default to
off. The server additionally requires a loopback connection and an account allowlist.

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

The model chooses at most once per second. Its output is revalidated against an action allowlist,
and target IDs must appear in the latest bounded observation. Add `--allow-speech` only when both
the client and server speech gates are also enabled.

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
[safety.md](../../docs/ai-pilot/safety.md) for the bridge contract and threat model.

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
