# AI pilot bridge protocol

The bridge uses one UTF-8 JSON request and one JSON response per Windows named-pipe connection.
The client creates the pipe with current-user-only access. Each caller-chosen request ID is echoed
verbatim so concurrent orchestration can reject mismatched responses.

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
| `join` | Join through the normal game ticker | Loopback, account allowlist, and join gate |
| `goal` | Start a deterministic move/interact/pickup goal | Server-validated target or bounded destination |
| `goal_status` | Read current executor state | Read-only |
| `stop` | Cancel the goal and release all held input | Always allowed when bridge is enabled |
| `say` | Speak through normal chat and moderation | Separate client/server gates and rate limit |

Goal kinds are `move_relative`, `move_to`, `move_to_entity`, `interact`, and `pickup`. The server
path planner returns coordinates, but the connected client traverses them by producing ordinary
movement inputs. Door interaction still goes through normal interaction handling.

## Observation shape

An observation contains attachment state, current map position, active hand, goal status, and a
bounded nearest-first entity array. Entity entries contain only information already replicated to
that client: opaque local ID, display name, prototype ID when available, relative position,
distance, and interaction hints. IDs expire from the target allowlist after a short observation
window.
