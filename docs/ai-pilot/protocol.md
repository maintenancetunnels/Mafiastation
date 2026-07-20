# AI pilot bridge protocol

The bridge uses one UTF-8 JSON request and one JSON response per Windows named-pipe connection.
The client creates the pipe with current-user-only access. Each caller-chosen request ID is echoed
verbatim so concurrent orchestration can reject mismatched responses.

Normal sandboxed content sees only primitive request/response objects. A narrowly gated trusted
helper performs the operating-system pipe and JSON operations; it authorizes only an explicit
development headless launch whose effective final server address parses as UDP loopback and whose
effective final bridge CVars name the same validated pipe. Full-release builds cannot authorize it.

```json
{"version":1,"id":"opaque-id","action":"move","arguments":{"direction":"east","durationMs":250}}
```

```json
{"version":1,"id":"opaque-id","ok":true,"data":{"accepted":true}}
```

Errors use `ok: false`, a short `error` string, and optional structured `data`. The bridge accepts
only protocol version 1, a bounded line length, a bounded request rate, and one request per pipe
connection.

## Actions

| Action | Purpose | Principal bounds |
| --- | --- | --- |
| `status` | Report bridge, connection, and attachment state | Read-only |
| `observe` | Return self and nearby replicated entities | Radius/count limited; local opaque entity IDs |
| `move` | Hold one direction using normal client input | 50-1000 ms, then automatic release |
| `interact` | Primary-interact with a recently observed entity | Recent observation and in-range checks |
| `pickup` | Pick up a recently observed entity | Recent observation and in-range checks |
| `drop` | Drop the active-hand item | Normal content input action |
| `swap_hands` | Cycle the active hand | Normal content input action |
| `ready` | Toggle lobby readiness | Separate server lifecycle gate |
| `join` | Join through the normal game ticker as `arguments.job` | Loopback, account/job allowlists, available slot, and join gate |
| `goal` | Start a deterministic move/interact/pickup goal | Server-validated target or bounded destination |
| `goal_status` | Read current executor state | Read-only |
| `stop` | Cancel the goal and release all held input | Always allowed when bridge is enabled |
| `say` | Speak locally or over common radio through normal chat and moderation | `channel` is only `local` or `radio`; separate client/server gates and rate limit |

`say` accepts `{"text":"...","channel":"local|radio"}` and defaults to `local` for older
callers. `radio` is transmitted through the same common-radio `;` path used by a normal player, so
the character still needs functioning radio equipment and the message obeys ordinary delivery.
Free-form prefixes and department-channel names are rejected; models cannot use `say` to select an
unreviewed chat surface.

Goal kinds are `move_relative`, `move_to`, `move_to_entity`, `interact`, and `pickup`. The server
path planner returns coordinates, but the connected client traverses them by producing ordinary
movement inputs. Door interaction still goes through normal interaction handling. While a goal is
`planning` or `moving`, the model runner sends only `goal_status`; terminal state causes a fresh
observation and returns control to the model. Goal status also reports the current position,
active waypoint position, and remaining waypoint distance when available, so stalls can be
diagnosed without exposing another control surface.

## Observation shape

An observation contains attachment state, current map position, active hand, coarse mob condition,
goal status, and a bounded nearest-first entity array. Entity entries contain only information
already replicated to that client: opaque local ID, display name, coarse kind (`character`, `item`,
or `object`), coarse mob condition when applicable, relative position, distance, and interaction
hints. Raw prototype IDs are not exposed. Only top-level entities on the controlled character's
current grid are included, excluding its inventory/body/action descendants. IDs expire from the
target allowlist after a short observation window.

`recentSpeech` contains at most 16 local, whisper, or radio messages that the normal chat UI
actually delivered to this client during the last three minutes. It excludes the controlled
character's own messages and non-IC/hidden chat, normalizes whitespace, and caps each message at
300 characters. This is the only dialogue memory supplied by the bridge; there is no global chat
feed or direct model-to-model channel.

`speech` reports `lastSpokeSecondsAgo` and `lastChannel` for the controlled character's own most
recent pilot transmission. This small self-memory prevents repetition and preserves conversational
continuity; elapsed silence is not itself a reason to speak. It resets when the controlled
character changes or detaches.

Each observation also includes live booleans for movement, interaction, pickup, drop, hand swap,
deterministic goals, observation, joining, and speech. The model validator rejects an action when
its reported capacity is false. The bridge checks the same action blocker/hand state immediately
before emitting input, and server-assisted goals are validated again by the server.

On a successful `join`, the response includes `assignedJob`. Crew mode refuses to start the model
unless this value is present and exactly matches its reviewed requested job. If a client is already
joined, the server verifies its mind's actual job before treating the request as idempotent.
