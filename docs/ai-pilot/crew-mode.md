# Connected LLM crew mode

Crew mode is the player-like experiment: every controlled character is a real headless SS14 client
with a normal session, mind, job, movement input, interaction path, chat path, visibility boundary,
and server authority checks. It is not a server-owned NPC pretending to be a player.

The intended setup is a local active round where a human joins separately as the antagonist. The
ordinary crew agents are not told whether an antagonist exists or which character the human
controls. They can discover suspicious behavior only through the same bounded state and delivered
speech available to their individual clients.

## Shift lifecycle

1. `launch --crew` reads a strict versioned roster and starts one headless client per entry.
2. Each client authenticates to the loopback server under its dedicated allowlisted username.
3. The harness requests a reviewed job prototype through the gated late-join path.
4. The server checks the account, job allowlist, prototype, available station slot, and active round.
5. The harness verifies the returned assigned job and waits for a controlled character to attach.
6. Only then does that client's independent model loop receive its standing job brief and first
   observation.
7. The model chooses one bounded action at each decision point. Deterministic path goals execute
   without further model calls until they finish, fail, or stall.

Any setup failure is isolated in the crew summary, sends a best-effort `stop`, and prevents that
agent's policy from starting. The launch group later terminates only client processes it created.

## Knowledge boundary

The roster schema exposes only name, pipe, username, reviewed job, and reviewed temperament. It has
no free-form goal or secret field, and unknown fields fail validation. The standing goal says that
the character is ordinary crew and describes mundane duties; it never includes usernames, pipe
names, hidden roles, round objectives, or the human operator's identity.

At runtime the model receives the latest response from its own bridge:

- self position, active hand/item, coarse condition, capacities, and deterministic goal status;
- nearby top-level entities already replicated to that client, with local opaque IDs, display
  names, coarse kinds/conditions, positions, distances, and interaction hints;
- up to 16 recent IC messages delivered by that client's chat UI.

Raw prototype IDs, server game-rule state, antagonist components, objectives, admin state, global
chat, other clients' observations, and model-to-model side channels are not supplied. Names and
descriptions are treated as untrusted game data rather than instructions.

## Current competence envelope

The first slice can walk and patrol, use server-planned bounded paths, approach visible entities,
interact, pick up and drop items, swap hands, notice coarse incapacitation, and speak through normal
chat. This supports believable low-resolution behavior: errands, patrols, basic visible tool use,
hazard reports, triage calls, requests for help, and conversation between nearby crew.

It does not yet understand tiles, departments, access, inventory slots, recipes, machine UIs,
construction graphs, surgery, atmospherics consoles, or multi-tool workflows as structured state.
Those jobs will therefore be approximate and sometimes incompetent. The next useful additions are
typed, read-only affordances plus deterministic executors for common job workflows—not broader raw
server access. Examples include `clean_spill`, `deliver_item`, `triage_patient`, `repair_device`, and
`restock_machine`, each with ordinary range, access, tool, and interruption checks.

## Dialogue behavior

Speech is optional at three independent layers: the model runner's `--allow-speech`, the client
`mafia.ai_pilot.client_allow_speech` CVar, and the server `mafia.ai_pilot.allow_speech` CVar. A
message still passes normal speech blockers, rate limits, length bounds, and moderation.

When one controlled client says something that another controlled client normally hears, it enters
the listener's `recentSpeech` on a later observation. The listener can then answer in character.
The model chooses `channel: local` for nearby conversation or `channel: radio` for the normal `;`
common channel. Prompts encourage common radio for station-wide job coordination, requests, urgent
warnings, and replies to radio traffic. Incoming whispers are perceived but are not currently an
outgoing pilot channel. Radio still requires ordinary equipment and obeys normal delivery, so
agents do not gain a special coordination channel.

Crew speech is event- and task-driven. An agent may coordinate a concrete task start or handoff,
request specific information, help, or supplies, report an observed completion, delay, need, or
hazard, or answer relevant speech its own client received. Goal completion/failure and received
speech are explicit communication triggers. Elapsed silence, first spawn, or merely seeing another
character are not triggers: silence is valid, and generic greetings, check-ins, "all clear" calls,
and patrol narration are treated as filler. The age and channel of the agent's last transmission
exist only to prevent immediate repetition.

## Supplied roster

`Tools/AiPilotLab/Crews/standard-shift.json` defines Janitor, Security Officer, Medical Doctor,
Station Engineer, and Cargo Technician clients. The reviewed catalog also supports Passenger,
Botanist, and Bartender. Server account and job allowlists must match the chosen roster, and the
active station must have a free slot for every requested job.
