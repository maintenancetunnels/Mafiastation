# Bounded LLM gameplay director

This is an opt-in experiment over existing SS14 authority surfaces. The model does not receive a
tool list and cannot name arbitrary actions. An administrator supplies a finite list of existing
HTN root tasks or game-rule prototypes; the model may return one exact ID or `none`. The server
then independently validates the JSON, allowlist membership, confidence, prototype existence, and
target state.

Autonomous NPC mode sends the configured persona, current/recent HTN goals, and a bounded window
of nearby speaker names and IC speech to the configured endpoint. With a remote endpoint, that
data leaves the game server; apply the same player-notice, provider-retention, consent, and privacy
review described in the moderation README.

Enable the shared gateway, then:

```toml
[mafia.director]
enabled = true
minimum_confidence = 0.7
max_pending_requests = 4
npc_autonomy_enabled = false
npc_minimum_decision_seconds = 60
npc_maximum_speech_memories = 12
allow_event_start = false
```

Examples:

```text
llmnpcgoal <netEntityId> SimpleHostileCompound,SimpleHumanoidHostileCompound "The target is hunting through maintenance."
llmnpcautonomy <netEntityId> 90 SimpleHostileCompound,SimpleHumanoidHostileCompound "A suspicious station custodian who reacts to nearby conversation."
llmnpcautonomyoff <netEntityId>
llmevent LiminalFlicker,BlackoutHunt "Prefer an eerie event that has not just occurred."
llmevent LiminalFlicker,BlackoutHunt --start "Start one fitting event."
```

`llmnpcautonomy` adds a low-frequency executive loop to an existing HTN NPC. The NPC remembers a
bounded, expiring window of nearby IC speech and recent root-goal transitions. On each cooldown it
asks the model to select one configured HTN root or `none`; locomotion, targeting, combat, and
every concrete action continue to run through normal server-owned HTN operators. Autonomous
requests require both `enabled` and `npc_autonomy_enabled`, and share the global request/token
budgets.

`llmevent` is preview-only unless `--start` is present. Even with `--start`, execution is refused
unless `mafia.director.allow_event_start` is true. This double gate is intentional.

The public system methods accept `DirectorChoiceOption` records, so future game-owned director
systems can provide richer descriptions and station context without exposing direct entity-system
