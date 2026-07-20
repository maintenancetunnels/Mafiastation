# Hybrid goal/capacity control

Mafiastation has two hybrid-control surfaces that share one rule: **the model is an executive, not
the locomotion loop**.

## Connected player pilots

The local `AiPilotLab` controls a real connected SS14 client through a current-user named pipe.
The engine bridge emits ordinary movement, interaction, hand, and chat inputs. Multi-step movement
is represented as a bounded deterministic goal:

Robust's content sandbox stays enabled. A small development-only helper owns the current-user pipe
and JSON parser, and starts only for an explicit headless, strict-loopback launch with matching
bridge CVars. It is unavailable in full-release builds; sandboxed game content receives only
primitive request objects and returns primitive response objects.

1. The model or scenario requests `goal`.
2. The server validates the loopback account, distance, target, range, request rate, and path.
3. The client follows the returned waypoints through normal movement inputs.
4. `LlmAgentRunner` polls `goal_status` without calling the model.
5. Completion, failure, cancellation, or a stall returns control to the model.

This is the main cost-saving loop:

```text
observe -> LLM decision -> deterministic goal lease -> status polling -> LLM escalation
```

An observation contains a dynamic `capabilities` object. The model-action validator refuses an
action when the bridge explicitly reports its capacity as false:

- `canMove`
- `canInteract`
- `canPickup`
- `canDrop`
- `canSwapHands`
- `canUseGoals`
- `canObserve`
- `canJoin`
- `canSpeak`

Capacity is checked again inside the client immediately before emitting an input. Server-assisted
goals and lifecycle requests are checked again by the server. Speech length and cooldown values
come from the server authorization snapshot and still pass through ordinary chat checks.

## Server-owned NPCs

`LlmNpcHybridComponent` supervises an existing `HTNComponent`. Its `routineTask` remains active
without any model endpoint. Routine behavior therefore uses normal SS14 HTN branches,
preconditions, utility queries, pathfinding, steering, and interactions.

The component has an authored capacity profile and 2-24 complex goal mappings. A mapping consists
of:

- an opaque choice ID shown to the model;
- an existing HTN compound root owned by the server;
- a short description;
- required capacities.

The model never returns an entity ID, coordinate, command, operator, or arbitrary HTN prototype.
It selects one opaque mapping ID or abstains. The server maps that ID back to a prevalidated HTN
root, rechecks current capacity, gives the root a finite lease, and then automatically restores
the routine root.

Automatic escalation can occur when:

- the routine root has produced no executable plan for the configured interval;
- new nearby IC speech arrives and `escalateOnSpeech` is enabled;
- an administrator explicitly runs `llmnpchybridescalate`.

Losing a required capacity ends a complex lease early. Attaching a player to the entity suspends
hybrid supervision, and periodic `LlmNpcAutonomyComponent` cannot be configured on the same NPC.

## Laboratory NPC

`MobMafiaHybridPrisoner` is an admin-spawnable test actor. Its routine root,
`MafiaHybridPrisonerRoutine`, handles nutrition and idling with ordinary HTN. Its two example
complex choices relocate nearby or remain observant.

Enable escalation only after the shared gateway and director:

```toml
[mafia.llm]
enabled = true

[mafia.director]
enabled = true
npc_hybrid_enabled = true
```

All three gates default to `false`. With `npc_hybrid_enabled = false`, routine HTN continues to
run; only model escalation is unavailable.

Runtime commands:

```text
llmnpchybridstatus <netEntityId>
llmnpchybridescalate <netEntityId> [reason]
llmnpchybridoff <netEntityId>
```

General runtime configuration is also available:

```text
llmnpchybrid <netEntityId> <routineRoot> <cooldownSeconds> <leaseSeconds> \
  <Move+Interact+Hands+...> \
  <choice=HTNRoot@Move+Interact,choice2=HTNRoot@Move> [persona]
```

Prototype configuration is preferred for production content because it keeps capabilities and
goal mappings reviewable in source control.

## Persistent-prisoner use

Player prisoners should use the connected-player pilot only in explicit local experiments.
Server-owned ambient prisoners, guards, or prison-service NPCs can use `LlmNpcHybridComponent`.
Good routine candidates include eating, resting, following an assigned target, going to a labor
marker, and returning to a cell. Social negotiation, an obstructed assignment, an ambiguous
escape opportunity, or a novel conflict can be authored as escalation points.

The [persistent-prisoner design](../design/PersistentPrisoners.md) remains authoritative. Neither
pilot surface may add penalties, change player records, execute admin commands, or bypass ordinary
game interactions.
