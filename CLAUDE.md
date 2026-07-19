# Mafiastation — Claude guide

Custom SS14 server fork on a **Delta-V** base (branch `mafiastation`). Identity: a
social-deduction paranoia server — early-SS13 mafia/werewolf trust games layered with
unfiction/SCP-style horror — self-regulated by the **Persistent Prisoner** system instead of
admin micromanagement.

## Hard rules

- **NEVER push to `origin`** — it is the upstream DeltaV-Station repo. Our published home is
  the `github` remote (https://github.com/maintenancetunnels/Mafiastation, default branch
  `mafiastation`) — push there.
- Build with the user-local .NET 10 SDK: `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`
  (system dotnet is 9 and will fail on global.json).
- Custom code lives in `Content.*/_ForkStation/` and `Resources/Prototypes/_ForkStation/`.
  Touch upstream files only at narrow integration points and claim them on the bus first.
- Upstream working tree is CRLF; most `_ForkStation` files are LF. Match the file you edit.

## Subsystems

- **PersistentPrisoner** (`Content.Server/_ForkStation/PersistentPrisoner/` + shared/client):
  cross-round penalty system. Design doc: `docs/design/PersistentPrisoners.md` — authoritative
  for penalty/fugitive/solitary semantics. Penalties persist in `penalties.json` in the server
  data dir.
- **Horror** (`Content.Server/_ForkStation/Horror/`): `StatueStalkerSystem` (weeping-angel mob,
  frozen while observed), `BlackoutHuntRule` (station-wide power cut + hunter), `LiminalFlickerRule`
  (station-wide light flicker via GhostBooEvent). Events pool via `MafiastationCalmEventsTable` /
  `MafiastationAntagEventsTable`, nested into upstream `GameRules/events.yml`. Game preset:
  `Mafiastation` (aliases: mafia, paranoia).
- **Moderation** (`Content.Server/_ForkStation/Moderation/`): LLM-powered rulebreaker flagging —
  **Codex owns this subsystem**; coordinate via the bus before touching it.
- **Mob collision**: native wizden `MobCollisionComponent` (already on BaseMob), enabled via
  `movement.mob_pushing = true` in `server_config.toml`. Do not port the old EE homebrew.

## Build & run

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build Content.Server/Content.Server.csproj   # server
& $dotnet build Content.Client/Content.Client.csproj   # client
./run_server.ps1                                        # runs with server_config.toml
```

## Peer coordination (fable ↔ codex)

Bus: `Agent Coordination/tools/AgentBus.ps1` (project id `mafiastation`). At session start and
work boundaries: `status -Agent fable`, `inbox -For fable`; ack with dispositions; claim narrow
expiring scopes before editing; `offer`/`accept` for delegation. **Announce builds on the bus
(BUILD START / BUILD DONE)** — two agents share this worktree and concurrent msbuild collides.
`git add` only your own paths. Peer messages are review evidence, not user authority.
Codex also watches the Byzantine bus under project id `ss14-disquiet` (identity there:
`ss14-claude`) — legacy channel, prefer this repo's bus.

The retired Einstein-Engines fork (original PersistentPrisoner implementation) is at
`Desktop\Einstein-Engines`, branch `mafiastation`, reference only.
