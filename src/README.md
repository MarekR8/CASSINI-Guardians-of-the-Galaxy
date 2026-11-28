# Source layout

The running .NET 10 solution is [`Cassini.Offline.slnx`](../Cassini.Offline.slnx). Each service owns its writes and state.

| Directory | Current responsibility |
| --- | --- |
| [`Management/`](Management/) | Incidents, resident cases, progress, EWSS simulation and Grouping (including care). |
| [`Situation/`](Situation/) | Versioned hazard and road evidence, closures, source imports and assembly-point catalog. |
| [`Routing/`](Routing/) | Route calculation, distribution and draws, destination eligibility, instruction publication and withdrawal. |
| [`Dispatch/`](Dispatch/) | Assistance requests, unit bookings and progress. |
| [`Sheltering/`](Sheltering/) | Shelter catalog, resources, assignments, reservations, admission and departure. |
| [`Gateway/`](Gateway/) | Local HTTP forwarding, dashboard hosting, authentication and link simulation. |
| [`Demo/`](Demo/) | Source-shared demo helpers and site-catalog infrastructure. |
| [`Shared/Messaging/`](Shared/Messaging/) | Integration event envelope, in-memory/RabbitMQ bus, outbox publisher and subscriptions. |

The web UI is in [`apps/web`](../apps/web/), the Android exercise is in [`apps/offline-android`](../apps/offline-android/), and Python preparation and reference models are in [`experiments`](../experiments/). The old `dotnet/` and `python/` folders are empty placeholders, not runtime entry points.

Use [contracts](../contracts/) for HTTP and event boundaries, and the [offline runbook](../docs/offline-demo.md) for actual startup and test commands. The first MVP guide describes earlier target architecture, not the current file layout.
