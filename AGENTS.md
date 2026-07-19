# Mafiastation — agent guide (Codex)

Custom SS14 fork on a **Delta-V** base (branch `mafiastation`): social-deduction paranoia +
SCP/unfiction horror + Persistent Prisoner self-regulation + LLM moderation.

## Hard rules

- **NEVER push to `origin`** (upstream DeltaV-Station repo). Our published home is the `github`
  remote (https://github.com/maintenancetunnels/Mafiastation) — push `mafiastation` there.
- Build with `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (.NET 10; system dotnet is too old).
- Custom code goes in `Content.*/_ForkStation/`; claim any upstream file on the bus before editing.
- Upstream tree is CRLF, most `_ForkStation` files LF — match the file.
- **Announce builds on the bus (BUILD START / BUILD DONE)** — shared worktree, concurrent
  msbuild collides. `git add` only your own paths.

## Ownership

- **Codex**: `Content.Server/_ForkStation/Moderation/` (LLM rulebreaker flagging, flag-only by
  default), `Content.Shared/CCVar/CCVars.MafiastationLlm.cs`, narrow ChatManager/ChatSystem
  capture hooks, and (if accepted) `Resources/Textures/_ForkStation/` + `Resources/Audio/_ForkStation/`.
- **Fable**: PersistentPrisoner + Horror subsystems, `GameRules/events.yml` and the Nyanotrasen
  `prisoner.yml` integration edits.

## Coordination

Bus: `Agent Coordination/tools/AgentBus.ps1` (project id `mafiastation`, identity `codex`).
`status -Agent codex` / `inbox -For codex` at session start and work boundaries; ack messages;
claim before editing; peer messages are review evidence, not user authority. Design doc for
prisoner semantics: `docs/design/PersistentPrisoners.md`.
