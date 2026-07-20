# Launches the full Mafiastation protagonist experience:
#   - the server (persistent prisoners, basement, horror layer, accusation votes)
#   - N LLM-piloted crew members as real connected clients (Codex's AiPilotLab)
# Then YOU connect as the protagonist (instructions printed at the end).
#
# LLM crew needs a model (OpenAI is the default brain): set $env:OPENAI_API_KEY first,
# or pass -NoLlm for deterministic scripted crew (no key needed, less lifelike).
#
# Examples:
#   $env:OPENAI_API_KEY = 'sk-...' ; ./run_llm_station.ps1 -Crew 5
#   ./run_llm_station.ps1 -Provider anthropic -Endpoint https://api.anthropic.com/v1/messages -Model claude-haiku-4-5-20251001
#   ./run_llm_station.ps1 -NoLlm -Crew 2          # keyless smoke crew

param(
    [int]$Crew = 5,
    [string]$Provider = 'openai-compatible',
    [string]$Endpoint = 'https://api.openai.com/v1/chat/completions',
    [string]$Model = 'gpt-5-mini',
    [int]$DurationSeconds = 3600,
    [string]$Goal = 'You are a crew member aboard a Nanotrasen space station. Do your job, explore, and talk with your crewmates in character. React honestly to anything strange: flickering lights, whispers, announcements that do not add up, statues you do not remember. Trust is optional.',
    [switch]$NoLlm
)

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$root = $PSScriptRoot

# Pilot1..N for --count mode; PilotAlpha/Beta are the smoke scenario's hardcoded names.
$accounts = ((1..$Crew | ForEach-Object { "Pilot$_" }) + @('PilotAlpha', 'PilotBeta')) -join ','

Write-Host "=== Mafiastation: starting server (crew allowlist: $accounts) ==="
$serverArgs = @(
    'exec', (Join-Path $root 'bin\Content.Server\Content.Server.dll'),
    '--config-file', (Join-Path $root 'server_config.toml'),
    '--cvar', 'game.lobbyenabled=false',
    '--cvar', 'mafia.ai_pilot.server_enabled=true',
    '--cvar', "mafia.ai_pilot.allowed_accounts=$accounts",
    '--cvar', 'mafia.ai_pilot.allow_join=true',
    '--cvar', 'mafia.ai_pilot.allow_speech=true'
)
$server = Start-Process -FilePath $dotnet -ArgumentList $serverArgs -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $root 'station_server.log') `
    -RedirectStandardError (Join-Path $root 'station_server_err.log')

Write-Host "Server PID $($server.Id); waiting for boot..."
Start-Sleep -Seconds 25

Write-Host "=== Launching $Crew LLM crew members ==="
$labArgs = @(
    'run', '--project', (Join-Path $root 'Tools\AiPilotLab\AiPilotLab.csproj'), '--',
    'launch',
    '--client', (Join-Path $root 'bin\Content.Client\Content.Client.dll'),
    '--server', '127.0.0.1:1212',
    '--count', $Crew
)

# Key resolution: MAFIA_LLM_KEY wins; otherwise fall back to OPENAI_API_KEY.
$keyEnv = if ($env:MAFIA_LLM_KEY) { 'MAFIA_LLM_KEY' } elseif ($env:OPENAI_API_KEY) { 'OPENAI_API_KEY' } else { $null }

if ($NoLlm -or -not $keyEnv) {
    if (-not $NoLlm) {
        Write-Warning 'No OPENAI_API_KEY or MAFIA_LLM_KEY set — falling back to scripted smoke crew.'
    }
    $labArgs += @('--scenario', (Join-Path $root 'Tools\AiPilotLab\Scenarios\multi-bot-smoke.json'))
}
else {
    $labArgs += @(
        '--goal', $Goal,
        '--provider', $Provider,
        '--endpoint', $Endpoint,
        '--model', $Model,
        '--api-key-env', $keyEnv,
        '--duration-seconds', $DurationSeconds
    )
}

Write-Host ''
Write-Host '=== YOU ARE THE PROTAGONIST ==='
Write-Host 'In another terminal, launch your own client and join them:'
Write-Host "  & `"$dotnet`" exec `"$root\bin\Content.Client\Content.Client.dll`""
Write-Host 'Then Direct Connect to: localhost:1212'
Write-Host 'You connect from loopback, so you are a full admin. Useful console commands:'
Write-Host '  secpenalty <player> <rounds 1-5> <reason>   - persistent prisoner penalty'
Write-Host '  accuse <character name>                     - call a crew accusation vote'
Write-Host '  forcerule BlackoutHunt | Doppelganger | WhisperEvent | LiminalFlicker'
Write-Host "  (the basement stairwell is at a random vent - go find it)"
Write-Host ''
Write-Host "Stop everything afterwards with:  taskkill /PID $($server.Id) /T /F"
Write-Host ''

& $dotnet @labArgs
