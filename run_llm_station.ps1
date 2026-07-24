# Launches a real Mafiastation round with a roster of AI-controlled connected players.
#
# The default roster contains twelve normal crew clients. Each client receives only its own
# bounded observation and uses the same movement, interaction, inventory, and chat paths as a
# human player. Server-owned hybrid NPCs are explicitly disabled for this launcher.
#
# OpenAI setup (keeps the key out of PowerShell history):
#   $env:OPENAI_API_KEY = [System.Net.NetworkCredential]::new(
#       '', (Read-Host 'OpenAI API key' -AsSecureString)).Password
#   ./run_llm_station.ps1
#
# Keyless twelve-client bridge smoke:
#   ./run_llm_station.ps1 -NoLlm -Port 1213

param(
    [string]$Roster,
    [ValidateSet('openai-responses', 'openai-compatible', 'anthropic')]
    [string]$Provider = 'openai-responses',
    [string]$Endpoint = 'https://api.openai.com/v1/responses',
    [string]$Model = 'gpt-5.6-luna',
    [ValidateSet('none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max')]
    [string]$ReasoningEffort = 'none',
    [ValidateRange(1, 32)]
    [int]$ModelMaxConcurrency = 4,
    [ValidateRange(1, 65535)]
    [int]$Port = 1212,
    [ValidateRange(30, 600)]
    [int]$StartupTimeoutSeconds = 420,
    [ValidateRange(1, 32)]
    [int]$ClientStartupBatchSize = 4,
    [ValidateRange(256, 4096)]
    [int]$ClientGcHeapMiB = 1024,
    [ValidateRange(0, 9)]
    [int]$ClientGcConserveMemory = 7,
    [switch]$NoLlm,
    [switch]$KeepServer
)

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$root = $PSScriptRoot
$project = Join-Path $root 'Tools\AiPilotLab\AiPilotLab.csproj'
$labRuntimeDirectory = Join-Path $root 'bin\AiPilotLab'
$labAssembly = Join-Path $labRuntimeDirectory 'Mafiastation.AiPilotLab.dll'
$engineProject = Join-Path $root 'RobustToolbox\Robust.Client\Robust.Client.csproj'
$rebuiltEngineAssembly = Join-Path $root 'RobustToolbox\bin\Client\Robust.Client.dll'
$sourceClientDirectory = Join-Path $root 'bin\Content.Client'
$aiClientDirectory = Join-Path $root 'bin\Content.AiClient'
$sourceClientAssembly = Join-Path $sourceClientDirectory 'Content.Client.dll'
$clientAssembly = Join-Path $aiClientDirectory 'Content.Client.dll'
$serverAssembly = Join-Path $root 'bin\Content.Server\Content.Server.dll'

if ([string]::IsNullOrWhiteSpace($Roster)) {
    $Roster = Join-Path $root 'Tools\AiPilotLab\Crews\realistic-station.json'
}
$Roster = [IO.Path]::GetFullPath($Roster)

foreach ($requiredPath in @($dotnet, $project, $engineProject, $sourceClientAssembly, $serverAssembly, $Roster)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required file is missing: $requiredPath"
    }
}

$rosterData = Get-Content -LiteralPath $Roster -Raw | ConvertFrom-Json
$agents = @($rosterData.agents)
if ($agents.Count -eq 0) {
    throw "Crew roster contains no agents: $Roster"
}

$accounts = @($agents | ForEach-Object { [string]$_.username })
if (@($accounts | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    throw "Every roster agent must have a username."
}
$jobs = @($agents | ForEach-Object { [string]$_.job } | Sort-Object -Unique)
if ($jobs.Count -eq 0) {
    throw "Crew roster contains no jobs."
}

Write-Host "=== Validating $($agents.Count)-player connected crew roster ==="
Write-Host '=== Preparing isolated AI Pilot Lab runtime ==='
& $dotnet build $project -o $labRuntimeDirectory
if ($LASTEXITCODE -ne 0) {
    throw "AI Pilot Lab build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $labAssembly -PathType Leaf)) {
    throw "AI Pilot Lab runtime is missing: $labAssembly"
}

& $dotnet exec $labAssembly validate-crew --file $Roster
if ($LASTEXITCODE -ne 0) {
    throw "Crew roster validation failed with exit code $LASTEXITCODE."
}

$endpointUri = [Uri]$Endpoint
if ($endpointUri.Scheme -notin @('http', 'https')) {
    throw 'The model endpoint must use HTTPS, or HTTP for a loopback-only local model.'
}
if ($endpointUri.Scheme -eq 'http' -and -not $endpointUri.IsLoopback) {
    throw 'An HTTP model endpoint is allowed only on loopback.'
}

$keyEnv = if (-not [string]::IsNullOrWhiteSpace($env:MAFIA_LLM_KEY)) {
    'MAFIA_LLM_KEY'
}
elseif (-not [string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY)) {
    'OPENAI_API_KEY'
}
else {
    $null
}

if (-not $NoLlm -and ($endpointUri.Scheme -eq 'https' -or $Provider -eq 'anthropic') -and -not $keyEnv) {
    throw @'
No OpenAI model API key is configured. Set it in this PowerShell session without putting it in
command history:
$env:OPENAI_API_KEY = [System.Net.NetworkCredential]::new(
    '', (Read-Host 'OpenAI API key' -AsSecureString)).Password
'@
}

$listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
if ($listeners.Count -gt 0) {
    $owners = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '
    throw "TCP port $Port is already in use by process ID(s): $owners. Stop that station or choose -Port."
}

$activePilotProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessId -ne $PID -and (
        [string]$_.CommandLine -match '--ai-pilot-local-trusted-bridge' -or
        [string]$_.CommandLine -match 'Mafiastation\.AiPilotLab\.dll\s+launch'
    )
})
if ($activePilotProcesses.Count -gt 0) {
    $owners = @($activePilotProcesses | Select-Object -ExpandProperty ProcessId -Unique) -join ', '
    throw "Another connected-player AI launch is active (process ID(s): $owners). Wait for it to finish before starting a second full-client swarm."
}

Write-Host '=== Preparing isolated headless client runtime ==='
& $dotnet build $engineProject -c Release -p:RobustToolsBuild=false -p:EnableClientScripting=false
if ($LASTEXITCODE -ne 0) {
    throw "Robust.Client build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $rebuiltEngineAssembly -PathType Leaf)) {
    throw "Rebuilt client engine is missing: $rebuiltEngineAssembly"
}

[void](New-Item -ItemType Directory -Path $aiClientDirectory -Force)
foreach ($entry in Get-ChildItem -LiteralPath $sourceClientDirectory -Force) {
    Copy-Item -LiteralPath $entry.FullName -Destination $aiClientDirectory -Recurse -Force
}
Copy-Item -LiteralPath $rebuiltEngineAssembly `
    -Destination (Join-Path $aiClientDirectory 'Robust.Client.dll') -Force

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$runDirectory = Join-Path $root "artifacts\llm-station\$stamp"
[void](New-Item -ItemType Directory -Path $runDirectory -Force)
$serverLog = Join-Path $runDirectory 'server.log'
$serverErrorLog = Join-Path $runDirectory 'server-error.log'
$serverAddress = "127.0.0.1:$Port"
$accountList = $accounts -join ','
$jobList = $jobs -join ','

Write-Host "=== Starting Mafiastation round on $serverAddress ==="
Write-Host "Connected AI players: $($agents.Count)"
Write-Host "Roster: $Roster"
Write-Host "Artifacts: $runDirectory"

$serverArgs = @(
    'exec', $serverAssembly,
    '--config-file', (Join-Path $root 'server_config.toml')
)
if ($NoLlm) {
    $smokeDataDirectory = Join-Path $runDirectory 'server-data'
    [void](New-Item -ItemType Directory -Path $smokeDataDirectory -Force)
    $serverArgs += @('--data-dir', $smokeDataDirectory)
}
$serverArgs += @(
    '--cvar', "net.port=$Port",
    '--cvar', 'game.lobbyenabled=true',
    '--cvar', 'game.defaultpreset=Mafiastation',
    '--cvar', 'mafia.ai_pilot.server_enabled=true',
    '--cvar', "mafia.ai_pilot.allowed_accounts=$accountList",
    '--cvar', "mafia.ai_pilot.allowed_jobs=$jobList",
    '--cvar', 'mafia.ai_pilot.default_job=Passenger',
    '--cvar', 'mafia.ai_pilot.allow_join=true',
    '--cvar', 'mafia.ai_pilot.allow_speech=true',
    '--cvar', 'mafia.director.npc_hybrid_enabled=false',
    '--cvar', 'mafia.crew.hybrid_count=0',
    '"+forcepreset Mafiastation"'
)

$server = Start-Process -FilePath $dotnet -ArgumentList $serverArgs -PassThru -NoNewWindow `
    -RedirectStandardOutput $serverLog `
    -RedirectStandardError $serverErrorLog

$serverReady = $false
$startupDeadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
while ([DateTime]::UtcNow -lt $startupDeadline) {
    if ($server.HasExited) {
        $errorTail = if (Test-Path -LiteralPath $serverErrorLog) {
            (Get-Content -LiteralPath $serverErrorLog -Tail 30) -join [Environment]::NewLine
        }
        else {
            '(no server error log)'
        }
        throw "Server exited during startup with code $($server.ExitCode).`n$errorTail"
    }

    if (Test-Path -LiteralPath $serverLog) {
        $roundStarted = Select-String -LiteralPath $serverLog -SimpleMatch 'Starting round!' -Quiet
        $engineReady = Select-String -LiteralPath $serverLog -SimpleMatch '-> Ready' -Quiet
        $presetFailed = Select-String -LiteralPath $serverLog -SimpleMatch `
            'Fallback - Failed to start round' -Quiet
        if ($presetFailed) {
            Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
            throw "Mafiastation preset failed during forced startup. See $serverLog"
        }
        if ($roundStarted -and $engineReady) {
            $serverReady = $true
            break
        }
    }
    Start-Sleep -Seconds 1
}

if (-not $serverReady) {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    throw "Server did not start a Mafiastation round within $StartupTimeoutSeconds seconds. See $serverLog"
}

Write-Host "Server PID $($server.Id); round is active."

$labArgs = @(
    'exec', $labAssembly, 'launch',
    '--client', $clientAssembly,
    '--server', $serverAddress,
    '--output', (Join-Path $runDirectory 'ai-pilot'),
    '--pipe-prefix', "mafcrew-$Port-$stamp",
    '--startup-timeout-seconds', $StartupTimeoutSeconds,
    '--client-startup-batch-size', $ClientStartupBatchSize,
    '--client-gc-heap-mib', $ClientGcHeapMiB,
    '--client-gc-conserve-memory', $ClientGcConserveMemory
)

if ($NoLlm) {
    $labArgs += @('--crew', $Roster, '--crew-smoke')
}
else {
    $labArgs += @(
        '--crew', $Roster,
        '--provider', $Provider,
        '--endpoint', $Endpoint,
        '--model', $Model,
        '--allow-speech',
        '--client-cvar', 'mafia.ai_pilot.client_allow_speech=true',
        '--model-max-concurrency', $ModelMaxConcurrency,
        '--model-timeout-seconds', 60,
        '--max-tokens', 200
    )
    if ($Provider -eq 'openai-responses') {
        $labArgs += @('--reasoning-effort', $ReasoningEffort)
    }
    if ($keyEnv) {
        $labArgs += @('--api-key-env', $keyEnv)
    }
}

Write-Host ''
Write-Host '=== YOU ARE THE THIRTEENTH PLAYER ==='
Write-Host 'Launch your normal client in another terminal:'
Write-Host "  & `"$dotnet`" exec `"$sourceClientAssembly`""
Write-Host "Direct Connect to: localhost:$Port"
Write-Host ''
Write-Host "AI provider: $(if ($NoLlm) { 'deterministic smoke only' } else { "$Provider / $Model" })"
Write-Host "Server log: $serverLog"
Write-Host ''

$labExitCode = 1
try {
    & $dotnet @labArgs
    $labExitCode = $LASTEXITCODE
}
finally {
    if (($NoLlm -or -not $KeepServer -or $labExitCode -ne 0) -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        [void]$server.WaitForExit(10000)
        Write-Host "Stopped owned server PID $($server.Id)."
    }
}

if ($labExitCode -ne 0) {
    throw "Connected crew launcher failed with exit code $labExitCode. See $runDirectory and artifacts\ai-pilot."
}

if ($KeepServer -and -not $server.HasExited) {
    Write-Host "Crew run finished; server PID $($server.Id) remains active because -KeepServer was supplied."
    Write-Host "Stop it with: Stop-Process -Id $($server.Id)"
}
