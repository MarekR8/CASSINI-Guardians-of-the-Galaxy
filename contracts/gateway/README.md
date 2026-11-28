# Gateway link simulator

IRIS² and GOVSATCOM have no public API, so the Gateway simulates the field link. Only requests with the header `X-Cassini-Link: field` use it. Requests without the header are local to the coordination point and are forwarded directly.

| Method and path | Body | Result |
| --- | --- | --- |
| `GET /api/link/queries/link-status` | — | Current profile, all profiles, pending count and recent field commands (no bodies) |
| `POST /api/link/commands/link-profile` | `{ "commandId": "…", "profile": "terrestrial" \| "iris2" \| "govsatcom" \| "down" }` | `200` with the status; `400 UnknownProfile`, `409 CommandIdConflict` |
| `POST /api/<service>/commands/<op>` + `X-Cassini-Link: field` | The owner's command, with `commandId` | See below |

Field command outcomes:

- The link is up and the queue is empty: the Gateway forwards the command after the simulated transit time and returns the owner's answer. `X-Cassini-Link-Transit-Ms` gives the simulated delay.
- The link is `down`, older commands are still waiting or the owner cannot be reached: `202` with `{ "status": "Queued", "entry": {…} }`. This is not a delivery confirmation.
- A retry with the same `commandId` and body returns the same queue entry. When the link is down and the command was already delivered, the Gateway returns the stored answer with `X-Cassini-Link-Replay`.
- The same `commandId` with another body returns `409 CommandIdConflict`. A command without `commandId` returns `400 CommandIdRequired`. A body over 64 KiB returns `413`.

The forwarder sends queued commands in order with an unchanged body. The owner's `commandId` receipts prevent duplicate records. A 5xx answer or an unreachable owner keeps the entry queued, with backoff of up to 8 s. A 4xx answer ends delivery as `Rejected` and keeps the owner's status. The queue is stored in `link-simulator.json` in `Demo:DataDirectory` and survives a restart.

| Profile | Added round trip | Throughput | Basis |
| --- | --- | --- | --- |
| `terrestrial` | 10 ms | 100 Mbit/s | Demo assumption |
| `iris2` | 40 ms | 20 Mbit/s | LEO round trip in the 30–50 ms range (task 6); simulated |
| `govsatcom` | 600 ms | 2 Mbit/s | GEO round trip of about 600 ms (task 6); simulated |
| `down` | — | — | Commands wait in the queue |
