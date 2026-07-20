# Launches YOUR client and auto-connects to the local Mafiastation server.
# Run after run_llm_station.ps1 has the server up.
param([string]$Username = 'Robert')

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
& $dotnet exec (Join-Path $PSScriptRoot 'bin\Content.Client\Content.Client.dll') `
    --username $Username --connect --connect-address 127.0.0.1:1212
