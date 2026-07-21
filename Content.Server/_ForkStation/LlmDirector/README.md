# Bounded LLM gameplay director

This is an opt-in experiment over existing SS14 authority surfaces. The model does not receive a
tool list and cannot name arbitrary actions. An administrator supplies a finite list of existing
HTN root tasks or game-rule prototypes; the model may return one exact ID or `none`. The server
then independently validates the JSON, allowlist membership, confidence, prototype existence, and
target state.

Autonomous NPC and dialogue modes send a deterministic self snapshot: character name, species,
age, sex/gender/pronouns, ordinary job role, live hair/facial-hair styles and colors, eye/skin
colors, worn and held item names (including explicit empty equipment slots), life/nutrition/fire/
standing/cuffed state, and current controller activity/goal. They also send configured personas,
current/recent HTN goals, bounded model-choice rationales, prior dialogue proposals, and a bounded
window of nearby speaker names and IC speech. The self snapshot deliberately excludes antagonist
roles, objectives, account data, and administrator state. Scene mode sends configured premises,
cast names/current goals, recent beats, and nearby IC speech. Narrative mode sends round
number/time, online player count, active game-rule IDs, an operator-authored theme, and recent
director choices. With a remote endpoint, that data leaves the game server; apply the same player
notice, provider-retention, consent, cross-border processing, deletion, and privacy review
described in the moderation README. Do not enable these modes until that data flow is acceptable
for the server and its players.

Enable the shared gateway, then:

```toml
[mafia.director]
enabled = true
minimum_confidence = 0.7
max_pending_requests = 4
npc_autonomy_enabled = false
npc_minimum_decision_seconds = 60
npc_maximum_speech_memories = 12
npc_hybrid_enabled = false
npc_dialogue_enabled = false
npc_dialogue_allow_speech = false
npc_dialogue_minimum_seconds = 120
npc_dialogue_maximum_characters = 180
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
llmnpchybridstatus <netEntityId>
llmnpchybridescalate <netEntityId> "The routine plan is blocked."
llmnpchybridoff <netEntityId>
llmnpcdialogue <netEntityId> 120 "A terse maintenance custodian; observant, suspicious, and never verbose."
llmnpcdialoguestatus <netEntityId>
llmnpcdialoguenow <netEntityId>
llmnpcdialogueoff <netEntityId>
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

## Goal/capacity hybrid NPCs

`LlmNpcHybridComponent` keeps one ordinary HTN root active for cheap routine behavior. It asks the
director for a bounded choice only after an authored escalation: sustained no-plan state, nearby
IC speech, or an explicit admin request. Each choice ID maps to a server-authored HTN compound
root with required capacities. The model cannot provide a root name, coordinate, entity ID,
operator, or command.

Current movement, interaction, hand, speech, and door capacities are revalidated before queuing
and again when the result completes. An accepted complex root receives a finite lease and then
automatically returns to the routine root; losing capacity ends the lease early. Player-controlled
entities are refused, and this component is mutually exclusive with periodic
`LlmNpcAutonomyComponent`.

The lab prototype `MobMafiaHybridPrisoner` uses normal food/idling HTN for its routine. See
[`docs/ai-pilot/hybrid-control.md`](../../../docs/ai-pilot/hybrid-control.md) for configuration,
the connected-player pilot path, and persistent-prisoner boundaries.

## Contextual NPC dialogue

`llmnpcdialogue` attaches a separate low-frequency dialogue layer to an existing HTN NPC. It
refuses player-controlled entities, remembers a bounded and expiring window of nearby accepted IC
speech and prior proposals, schedules one initial proposal, then only requests another after new
context arrives or an admin uses `llmnpcdialoguenow`. Generated dialogue never supplies commands,
entity IDs, coordinates, HTN goals, or other game-action parameters.

Dialogue generation requires `mafia.llm.enabled`, `mafia.director.enabled`, and the independent
`mafia.director.npc_dialogue_enabled` gate. The response must be an exact three-field JSON object:
`shouldSpeak`, `text`, and an allowlisted `tone`. The parser rejects extra or duplicate fields,
over-length text, control/bidirectional characters, links, and obvious OOC or server/admin
impersonation prefixes. The ordinary chat system still performs its normal action and message
checks, radio-prefix processing is disabled, and every previewed or submitted line is admin-logged.

Valid output is preview-only unless `mafia.director.npc_dialogue_allow_speech` was enabled when
the request was queued and remains enabled at completion. Disabling a generation or speech gate,
reconfiguring/removing the component, changing provider/endpoint/model, possessing the NPC, or
ending the round invalidates the in-flight result. Generated lines are not fed back into this
dialogue system as new observations, limiting model-to-model loops.

The speech gate is a risk boundary, not a content guarantee. Prompt rules and structural checks
cannot prove that a cheap model's line is appropriate for every community or scene. Review
previews and provider behavior before enabling speech, retain admin logs, publish the player-data
notice, and disable the gate immediately if output quality is not acceptable.

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
them. The scene selector cannot generate speech, commands, coordinates, entity IDs, goal IDs,
parameters, or individual actions. Scene configurations are runtime-only and are cleared during
round lifecycle reset. `npc_scenes_enabled` is a separate default-off gate; disabling/removing a scene invalidates
its pending result.

The public system methods accept `DirectorChoiceOption` records, so future game-owned director
systems can provide richer descriptions and station context without exposing direct entity-system
authority. `TryRequestPlan` returns a typed outcome only; its consumer remains responsible for
mapping the selected ID to prevalidated server-owned behavior and checking authorization again.
