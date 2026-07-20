# Attaches LLM crew to the ALREADY-RUNNING Mafiastation server (does not start a server).
# Run this in the same window where you set $env:OPENAI_API_KEY.
param(
    [int]$Crew = 5,
    [string]$Model = 'gpt-5.4-nano',
    [int]$DurationSeconds = 3600
)

if (-not $env:OPENAI_API_KEY -and -not $env:MAFIA_LLM_KEY) {
    Write-Error 'Set $env:OPENAI_API_KEY in THIS window first.'
    exit 1
}
$keyEnv = if ($env:MAFIA_LLM_KEY) { 'MAFIA_LLM_KEY' } else { 'OPENAI_API_KEY' }

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
& $dotnet run --project (Join-Path $PSScriptRoot 'Tools\AiPilotLab\AiPilotLab.csproj') -- launch `
    --client (Join-Path $PSScriptRoot 'bin\Content.Client\Content.Client.dll') `
    --server 127.0.0.1:1212 `
    --count $Crew `
    --goal 'You are a crew member aboard a Nanotrasen space station. Do your job, explore, and talk with your crewmates in character - use radio (;) and local speech. React honestly to anything strange: flickering lights, whispers, announcements that do not add up, statues you do not remember. Trust is optional.' `
    --provider openai-compatible `
    --endpoint https://api.openai.com/v1/chat/completions `
    --model $Model `
    --api-key-env $keyEnv `
    --duration-seconds $DurationSeconds
