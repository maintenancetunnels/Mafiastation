# Launches the Mafiastation server with the repo config (mob collision on, persistent prisoners on).
# Uses the user-local .NET 10 SDK (system-wide dotnet is too old for this repo).
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

& $dotnet run --project "$PSScriptRoot\Content.Server\Content.Server.csproj" -c Debug -- --config-file "$PSScriptRoot\server_config.toml"
