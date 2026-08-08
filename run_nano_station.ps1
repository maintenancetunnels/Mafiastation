# Nano-powered NPC station: server-owned hybrid crew driven by OpenAI gpt-*-nano instead of the
# local qwen 1.5b. Talks to OpenAI directly (no json_proxy). Reads the API key from .llmkey
# (gitignored) into MAFIA_LLM_KEY, which the MafiaLlmGatewaySystem picks up.
#
#   ./run_nano_station.ps1                 # gpt-4.1-nano, 3 crew
#   ./run_nano_station.ps1 -Crew 5 -Model gpt-4.1-nano
#
# NOTE: use a *non-reasoning* chat model. gpt-5.x-nano are reasoning models that reject the
# gateway's 'max_tokens' param ("use max_completion_tokens instead"), so every request 400s and
# the crew never speak. gpt-4.1-nano accepts the gateway's request shape as-is.
param([int]$Crew = 5, [string]$Model = 'gpt-4.1-nano')

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$root = $PSScriptRoot

$keyFile = Join-Path $root '.llmkey'
if (-not (Test-Path $keyFile)) { Write-Error "No .llmkey found next to this script."; exit 1 }
$env:MAFIA_LLM_KEY = (Get-Content $keyFile -Raw).Trim()

$cv = @(
    '--cvar','game.lobbyenabled=false','--cvar','game.map=Asterisk',
    '--cvar','movement.mob_pushing=true',
    '--cvar','events.enabled=true',
    '--cvar','mafia.llm.enabled=true',
    '--cvar','mafia.llm.provider=openai-compatible',
    '--cvar','mafia.llm.endpoint=https://api.openai.com/v1/chat/completions',
    "--cvar","mafia.llm.model=$Model",
    '--cvar','mafia.llm.timeout_seconds=30',                                # nano is fast
    '--cvar','mafia.llm.requests_per_minute=60',                            # throttle to reduce server tick load
    '--cvar','mafia.director.enabled=true',
    '--cvar','mafia.director.minimum_confidence=0.6',                       # let the director act less often (less LLM load)
    '--cvar','mafia.director.npc_hybrid_enabled=true',
    '--cvar','mafia.director.npc_dialogue_enabled=true',
    '--cvar','mafia.director.npc_dialogue_allow_speech=true',
    '--cvar','mafia.director.npc_dialogue_minimum_seconds=30',              # ambient cadence; player radio still answers instantly (priority)
    "--cvar","mafia.crew.hybrid_count=$Crew",
    '--cvar','mafia.crew.player_bar_spawn=true'                              # you start in the bar with them
)
$server = Start-Process -FilePath $dotnet -ArgumentList (@('exec', (Join-Path $root 'bin\Content.Server\Content.Server.dll'), '--config-file', (Join-Path $root 'server_config.toml')) + $cv) `
    -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $root 'station_server.log') -RedirectStandardError (Join-Path $root 'station_server_err.log')
"SERVER PID: $($server.Id)" | Set-Content (Join-Path $root 'station_server.pid')
Write-Host "server PID $($server.Id) booting on OpenAI $Model with $Crew nano crew; join with ./join_station.ps1"
