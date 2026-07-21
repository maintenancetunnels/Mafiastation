# Full "simulated station" launch: server wired for keyless local-LLM hybrid NPC crew.
#   - Codex's server-owned hybrid NPCs (MobMafiaHybridPrisoner): HTN routine + LLM goal/dialogue
#   - Brain: local qwen2.5:7b via Ollama, through Tools/json_proxy.py (forces clean JSON), NO key
#   - Soft mob collision on (movement.mob_pushing)
#   - Spawns $Crew NPCs on the station floor each round (mafia.crew.hybrid_count)
#
# Prereqs (this script starts the proxy for you): Ollama running with qwen2.5:7b-instruct pulled.
param([int]$Crew = 4, [int]$CrewModelPort = 11500)

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$root = $PSScriptRoot

# JSON-forcing proxy in front of Ollama (keyless brain).
$proxyUp = $false
try { Invoke-WebRequest -Uri "http://127.0.0.1:$CrewModelPort/v1/models" -TimeoutSec 3 -ErrorAction Stop | Out-Null; $proxyUp = $true } catch {}
if (-not $proxyUp) {
    $py = (Get-Command python3 -ErrorAction SilentlyContinue).Source
    if (-not $py) { $py = (Get-Command python -ErrorAction SilentlyContinue).Source }
    Start-Process -FilePath $py -ArgumentList (Join-Path $root 'Tools\json_proxy.py') -WindowStyle Hidden
    Start-Sleep -Seconds 3
    Write-Host 'started json_proxy'
}

$cv = @(
    '--cvar','game.lobbyenabled=false','--cvar','game.map=Asterisk',
    '--cvar','movement.mob_pushing=true',                                  # soft mob collision
    '--cvar','events.enabled=true',                                        # only the 2 rare entity scares can fire; ambient is forcerule-only
    '--cvar','mafia.llm.enabled=true','--cvar','mafia.llm.allow_unauthenticated=true',
    '--cvar','mafia.llm.provider=openai-compatible',
    "--cvar","mafia.llm.endpoint=http://127.0.0.1:$CrewModelPort/v1/chat/completions",
    '--cvar','mafia.llm.model=qwen2.5:1.5b-instruct',
    '--cvar','mafia.llm.timeout_seconds=90',                               # local 7B is slow; give it room
    '--cvar','mafia.llm.requests_per_minute=120',                          # crew chatter needs many calls (default 2)
    '--cvar','mafia.director.enabled=true',                                # master LLM director gate
    '--cvar','mafia.director.npc_hybrid_enabled=true',                     # goal/capability escalation
    '--cvar','mafia.director.npc_dialogue_enabled=true',
    '--cvar','mafia.director.npc_dialogue_allow_speech=true',              # NPCs may speak IC
    '--cvar','mafia.director.npc_dialogue_minimum_seconds=20',             # chatter cadence (default 120)
    "--cvar","mafia.crew.hybrid_count=$Crew"
)
$server = Start-Process -FilePath $dotnet -ArgumentList (@('exec', (Join-Path $root 'bin\Content.Server\Content.Server.dll'), '--config-file', (Join-Path $root 'server_config.toml')) + $cv) `
    -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $root 'station_server.log') -RedirectStandardError (Join-Path $root 'station_server_err.log')
"SERVER PID: $($server.Id)" | Set-Content (Join-Path $root 'station_server.pid')
Write-Host "server PID $($server.Id) booting with $Crew hybrid LLM NPCs; join with ./join_station.ps1"
