# AI pilot safety model

The pilot is intentionally a two-key local test feature.

- Client and server enable CVars default to `false`.
- The transport is a current-user-only local named pipe, never a listening TCP service.
- Server-assisted actions require a loopback game connection and an explicit account allowlist.
- Lobby joining and speech have independent server gates. Speech also has an independent client
  gate, a short length limit, a rate limit, and passes through normal chat moderation.
- Model output is not trusted. The lab rebuilds an allowlisted request, discards unknown fields,
  clamps movement duration, bounds coordinate goals, and requires recent observation membership
  for target IDs.
- Held movement is released after its deadline, on `stop`, on bridge shutdown, and on client
  disconnect. The deterministic executor has path, stall, distance, and wall-clock limits.
- Observations are radius/count limited and expose only state already replicated to that client.
- Replays suppress speech and lobby lifecycle actions unless explicitly enabled.
- The launcher tracks process objects and terminates only headless clients it started.
- API keys come from environment variables and are excluded from logs and command arguments.

Do not enable this configuration on a public server. The server-side loopback and allowlist checks
are defense in depth, not a claim that autonomous public-server play is acceptable.

## Suggested local test configuration

Use dedicated accounts such as `Pilot1` and `Pilot2`, bind the game server to loopback, leave
speech disabled for movement tests, use a low observation radius, and store run artifacts outside
production log retention. Review model-generated speech before enabling its gate.

## Failure behavior

Invalid or stale targets fail closed. A model endpoint failure stops after a configured number of
consecutive errors. Scenario steps use explicit deadlines. Process cleanup and input release are
best-effort in the harness and independently enforced again inside the client bridge.
