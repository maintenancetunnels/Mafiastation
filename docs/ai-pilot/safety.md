# AI pilot safety model

The pilot is intentionally a two-key local test feature.

- Client and server enable CVars default to `false`.
- The transport is a current-user-only local named pipe, never a listening TCP service.
- Robust's content sandbox remains enabled. A narrow development-only helper owns pipe/JSON I/O;
  it is inert without the trusted launch flag, headless mode, a strictly parsed final loopback
  address, and exact enabled bridge CVars, and it compiles inert in full-release builds.
- Server-assisted actions require a loopback game connection and an explicit account allowlist.
- Lobby joining and speech have independent server gates. Speech also has an independent client
  gate, a short length limit, a rate limit, and passes through normal chat moderation.
- Model output is not trusted. The lab rebuilds an allowlisted request, discards unknown fields,
  clamps movement duration, bounds coordinate goals, and requires recent observation membership
  for target IDs.
- Every observation carries dynamic capacity flags. Model actions are checked against them, the
  client checks current action blockers/hand state again, and path/lifecycle requests cross a
  separate server authority boundary.
- Held movement is released after its deadline, on `stop`, on bridge shutdown, and on client
  disconnect. The deterministic executor has path, stall, distance, and wall-clock limits.
- Observations are radius/count limited, expose only state already replicated to that client, and
  include only top-level world entities on the controlled character's grid—not inventory, organs,
  actions, or other internal descendants. They expose coarse kinds and conditions rather than raw
  entity prototype IDs.
- Crew dialogue memory is limited to recent IC chat actually delivered by that client's normal chat
  UI. There is no omniscient chat stream or hidden model-to-model bus.
- Crew rosters accept only a reviewed catalog of ordinary jobs and temperaments. Unknown JSON
  fields are rejected, so a roster cannot add a free-form secret mission or antagonist hint. The
  runner verifies the server-assigned job before invoking any model.
- Crew prompts explicitly deny hidden-role, objective, game-rule, administrator, and human-control
  knowledge. Suspicion must be based on concrete speech or conduct perceived by that client. This
  is an epistemic guardrail, not proof that a model will reason well; run artifacts remain auditable.
- Replays suppress speech and lobby lifecycle actions unless explicitly enabled.
- The launcher tracks process objects and terminates only headless clients it started.
- API keys come from environment variables and are excluded from logs and command arguments.
- Server-owned hybrid NPCs retain ordinary HTN as their routine layer. Their independent
  `mafia.director.npc_hybrid_enabled` gate defaults off; the model may select only an opaque,
  pre-authored HTN mapping, capacity is rechecked at completion, and the lease returns to routine.

Do not enable this configuration on a public server. The server-side loopback and allowlist checks
are defense in depth, not a claim that autonomous public-server play is acceptable.

## Suggested local test configuration

Use dedicated accounts such as `CrewJanitor` and `CrewDoctor`, bind the game server to loopback,
allowlist only the exact ordinary jobs in the roster, leave speech disabled for movement tests,
use a low observation radius, and store run artifacts outside production log retention. Review
model-generated speech before enabling its gate. The human antagonist should join independently;
never encode that identity in a roster, model prompt, or endpoint-side shared context.

## Failure behavior

Invalid or stale targets fail closed. A model endpoint failure stops after a configured number of
consecutive errors. Scenario steps use explicit deadlines. Process cleanup and input release are
best-effort in the harness and independently enforced again inside the client bridge. Oversized or
malformed pipe requests are rejected per connection without stopping the listener.
