# Bounded LLM gameplay director

This is an opt-in experiment over existing SS14 authority surfaces. The model does not receive a
tool list and cannot name arbitrary actions. An administrator supplies a finite list of existing
HTN root tasks or game-rule prototypes; the model may return one exact ID or `none`. The server
then independently validates the JSON, allowlist membership, confidence, prototype existence, and
target state.

Autonomous NPC and scene modes send configured personas/premises, current/recent HTN goals,
bounded model-choice rationales, and a bounded window of nearby speaker names and IC speech to the
configured endpoint. Narrative mode sends round number/time, online player count, active
game-rule IDs, an operator-authored theme, and recent director choices. With a remote endpoint,
that data leaves the game server; apply the same player notice, provider-retention, consent,
cross-border processing, deletion, and privacy review described in the moderation README. Do not
enable these modes until that data flow is acceptable for the server and its players.

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
narrative_enabled = false
narrative_event_ids = ""
narrative_theme = ""
narrative_initial_delay_seconds = 300
narrative_interval_seconds = 600
narrative_repeat_cooldown_seconds = 1800
narrative_maximum_memories = 8
narrative_allow_event_start = false
npc_scenes_enabled = false
npc_scene_minimum_decision_seconds = 60
```

All gates default to false and all autonomous modes share the gateway request-per-minute and
per-round token budgets.

Examples:

```text
llmnpcgoal <netEntityId> SimpleHostileCompound,SimpleHumanoidHostileCompound "The target is hunting through maintenance."
llmnpcautonomy <netEntityId> 90 SimpleHostileCompound,SimpleHumanoidHostileCompound "A suspicious station custodian who reacts to nearby conversation."
llmnpcautonomyoff <netEntityId>
llmevent LiminalFlicker,BlackoutHunt "Prefer an eerie event that has not just occurred."
llmevent LiminalFlicker,BlackoutHunt --start "Start one fitting event."
llmnarrativestatus
llmnarrativenow
llmnpcscene haunted_pair 120 123,456 hunt:SimpleHostileCompound|SimpleHostileCompound;search:SimpleHumanoidHostileCompound|SimpleHumanoidHostileCompound "Two entities coordinate a tense maintenance encounter."
llmnpcscenestatus [haunted_pair]
llmnpcsceneoff haunted_pair
```

`llmnpcautonomy` adds a low-frequency executive loop to an existing HTN NPC. The NPC remembers a
bounded, expiring window of nearby IC speech, recent root-goal transitions, and recent bounded
choice outcomes. On each cooldown it asks the model to select one configured HTN root or `none`;
locomotion, targeting, combat, and every concrete action continue to run through normal
server-owned HTN operators. Autonomous
requests require both `enabled` and `npc_autonomy_enabled`, and share the global request/token
budgets.

`llmevent` is preview-only unless `--start` is present. Even with `--start`, execution is refused
unless `mafia.director.allow_event_start` is true. This double gate is intentional.

## Autonomous station narrative

`mafia.director.narrative_event_ids` is a comma-separated operator allowlist of existing game-rule
prototype IDs. During an active round, the narrative system excludes already-active rules and
recent selections, builds a bounded telemetry snapshot, and asks for one choice at the configured
cadence. `llmnarrativenow` requests an immediate pass; `llmnarrativestatus` reports gates,
candidate count, cooldown state, and the last safe error.

Narrative choices are previews unless both `mafia.director.allow_event_start` and
`mafia.director.narrative_allow_event_start` remain true from queue time through completion.
Disabling narrative mode, changing its allowlist/theme/start gate, or ending the round invalidates
an in-flight result before execution. The selected rule prototype and current state are validated
again immediately before `GameTicker.StartGameRule`.

## Coordinated NPC scenes

`llmnpcscene` defines a runtime scene for 2-8 existing HTN NPCs. Each semicolon-separated beat has
the form `beatId:goalForSlot0|goalForSlot1|...`; every goal must be an existing HTN compound-root
prototype and should be compatible with the target NPC's blackboard/operators. At least two beats
are required. The model sees a bounded shared blackboard containing
the authored premise, cast names/current goals, recent beats, and nearby IC speech, then returns
only a beat ID or `none`.

On an accepted beat, the server looks up the pre-authored mapping, validates every member and goal
before changing anything, pauses enabled HTN planners, assigns the roots as a batch, and resumes
them. The model cannot generate speech, commands, coordinates, entity IDs, goal IDs, parameters,
or individual actions. Scene configurations are runtime-only and are cleared on round lifecycle
reset. `npc_scenes_enabled` is a separate default-off gate; disabling/removing a scene invalidates
its pending result.

The public system methods accept `DirectorChoiceOption` records, so future game-owned director
systems can provide richer descriptions and station context without exposing direct entity-system
authority. `TryRequestPlan` returns a typed outcome only; its consumer remains responsible for
mapping the selected ID to prevalidated server-owned behavior and checking authorization again.
