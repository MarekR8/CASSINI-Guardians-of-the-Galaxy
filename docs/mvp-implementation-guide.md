# MVP implementation guide

This guide takes the project from the documentation branch to a runnable flood evacuation demo. Implement the four [business workflows](business-processes.md) in one vertical slice. Start with the [technology stack](mvp-technology-stack.md) and use the [interface specification](mvp-interfaces.md) as the wire contract. Use the [storage and state design](mvp-storage-and-state.md) for migrations and handlers, and the [routing and demo specification](mvp-routing-and-demo.md) for shared fixtures and acceptance tests. The paths, commands and settings below describe the files to implement.

## Deliverables and scope

- Four .NET services with separate databases or database owners, migrations, health endpoints and published development OpenAPI documents.
- One React app with coordinator, resident, dispatcher and response unit views. Situation Data supplies map layers, Management supplies case progress, Routing supplies authorized instructions and Dispatch supplies assistance state.
- One versioned demo dataset with deterministic provider replay, one container setup and an automated test that follows the incident through the bridge closure.
- One documented way to switch Routing to a hosted OpenRouteService provider and to import a sourced Copernicus or FlyPix layer.

The MVP ends at an eligible point outside the hazard zone and a confirmed exit. Shelter allocation and relationship-based grouping belong to later releases. The first runnable slice uses the recorded provider and synthetic people and units. Device and provider credentials are only prerequisites for the corresponding live demonstrations.

## Bootstrap the repository

| Path to create | What belongs there |
| --- | --- |
| `Cassini.sln`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.config/dotnet-tools.json` | Solution, pinned .NET SDK and `dotnet-ef`, shared build options and central package versions. Enable nullable reference types and restore locks. |
| `src/Management`, `src/Situation`, `src/Routing`, `src/Dispatch` | One ASP.NET Core project per service. Organize each into `Domain`, `Application`, `Infrastructure` and `Endpoints`. Keep aggregate operations in `Domain`, command orchestration in `Application`, EF, HTTP and AMQP adapters in `Infrastructure` and DTO mapping and authorization in `Endpoints`. |
| `apps/web` | React, TypeScript, Vite, Leaflet, role-specific views and API clients. Keep the Node version and npm lockfile with the app. |
| `contracts/http`, `contracts/events` | Version-one contract examples and JSON schemas for event envelopes and payloads. Export OpenAPI from each running API and compare public operation names and examples in CI. |
| `data/demo`, `experiments/routing` | A demo manifest, GeoJSON, provider replay fixtures, expected results and the Python reference model. Use the same IDs in frontend examples and integration tests. |
| `tests/Management.Tests`, `tests/Situation.Tests`, `tests/Routing.Tests`, `tests/Dispatch.Tests`, `tests/e2e` | xUnit domain and HTTP integration checks, and a Playwright Chromium test for the full flow. |
| `compose.yaml`, `infra/nginx`, `infra/postgres`, `infra/keycloak`, `.env.example`, `.github/workflows/ci.yml` | Local runtime, gateway route table, database setup, demo identity realm, environment variable template and CI. |

Each service registers its own `DbContext`, repositories for aggregate roots, internal query models, validation handlers and `TimeProvider`. Use in-process calls for aggregates in the same service. Share contract shapes through the published interface and test fixtures. Keep EF entities and domain objects in their owning project.

## Local configuration and startup

| Setting | Demo target and responsibility |
| --- | --- |
| Infrastructure | Docker Compose v2 on Linux x86-64, or Docker Desktop with WSL 2 on Windows. The browser entry point is `http://localhost:8080`. NGINX serves the web app and strips `/api/{service}` before forwarding requests to the private service on port `8080`. |
| Databases | PostgreSQL 17 and PostGIS from the selected container image. Bootstrap `management_db`, `situation_db`, `routing_db`, `dispatch_db` and `keycloak_db` with separate database owners. Enable PostGIS in `situation_db`. Give each service only its own connection string. |
| Broker | RabbitMQ internal port `5672`. Declare `evacuation.events`, subscriber queues, retry limits and dead-letter bindings before consumers are ready. Use the topic bindings from the interface specification. |
| Identity | Realm `evacuation`, public issuer `http://localhost:8080/auth/realms/evacuation` for local use. Configure Keycloak's `/auth` prefix, public hostname and private backchannel. Validate the public issuer and configured API audience even when metadata and keys are fetched privately. Test sign-in and JWT validation through the actual Compose network. [Keycloak hostname configuration](https://www.keycloak.org/server/hostname). |
| Service URLs | Set internal URI configuration for `Management`, `Situation`, `Routing` and `Dispatch` to their Compose DNS names on port `8080`. Use one typed HTTP client per downstream API, cancellation and finite connection and request timeouts. |
| Provider | `Routing__Provider=Recorded` by default. Mount `data/demo` read-only. For an explicit live run use `Routing__Provider=OpenRouteService` with a server-side base URL and key. Both modes expose the same internal provider interface. |
| Secrets | Document keys and non-secret examples in `.env.example`. Read database, AMQP, Keycloak service credentials and provider keys from local ignored files or the CI secret store. Keep browser configuration limited to public values. |
| Readiness | `/health/live` checks process liveness. `/health/ready` checks the service's local database connectivity and schema readiness. Show broker state separately so commands backed by a functioning local outbox can remain available during a broker interruption. |

Bring up PostgreSQL, RabbitMQ and Keycloak first, apply all four service migrations, import the identity realm and load a matching fixture version. Then start the APIs and web app. Implement migrations as an explicit setup command or one-shot Compose jobs. A readiness probe should surface a missing schema. Make demo import idempotent using stable IDs. Provide a documented reset for the dedicated demo data and leave local developer data intact.

## Identity, authorization and map views

Use OpenID Connect Authorization Code with PKCE for the browser and JWT bearer validation in every API. Use separate service clients for interservice calls. Record the authorized coordinator or dispatcher for writes. Treat a requested role in a JSON payload as untrusted input. Check resource ownership and incident scope before returning details. Use separate client or service scopes for ingestion, reevaluation and dispatch publication.

| Role | Screen to implement | Data and actions |
| --- | --- | --- |
| Resident | Alert or case screen | Submit a hazard alert, view own instruction and its validity, request help, and submit attributable position or progress reports. Show pending and expired instructions explicitly. |
| Coordinator | Incident map and follow-up panel | View situation, source dates, case positions, route versions and assistance state for the incident. Verify a restriction, review or publish a civilian proposal, and record verified exit. |
| Dispatcher | Assistance panel and response map | View open requests, pickup needs and eligible units, book a unit, approve its route, review route failures and record authorized task progress. |
| Responder | Active task view | Read the assigned pickup and current route, acknowledge it, report arrival and pickup, and confirm task completion. |

Use an authorized Management incident monitor read model for case rows and linked routing and dispatch state. The frontend loads terrain and hazard layers separately from Situation Data through the web entry point. Add `GET /v1/incidents/{incidentId}/monitor` and `GET /v1/response-units/{unitId}/active-assignment` using the [interface specification](mvp-interfaces.md). Fetch at a configurable 3-second interval during the demo, with at most one queued refresh per resource and a manual refresh action. Retain the last observation time when the backend is unreachable. Label reviewed and unverified hazards differently, and render withdrawn or expired instructions with a distinct state. Exact positions stay in authorized operational views. A map marker alone cannot close a case.

## Implementation order and handoffs

| Step | Implement | Ready for the next step when |
| --- | --- | --- |
| 0. Shared foundation | Skeleton APIs, configuration binding, migrations bootstrap, seeded realm, error envelope, outbox and inbox structure, initial contract examples and shared demo clock. | All APIs report readiness after setup. A test user can sign in and query their authorized resource. |
| 1. Alert to map | Management alert and case handlers, Situation Data hazard and network imports, situation snapshot, coordinator map and progress read view. | A demo alert is persisted before a snapshot is fetched. The map distinguishes the reported area, confirmed hazard, review layer and road status. |
| 2. Civilian routing | Provider adapter, profile mapping, scenario graph, geometry and timing validation, proposal review and publication. | A pedestrian gets an eligible route with a version. An inaccessible or blocked candidate returns a reason without publishing guidance. |
| 3. Assistance | Request and response units, route bundle generation, atomic booking, publication workflow and progress reports. | A dispatcher sees the booked unit, the unit sees approach and outbound legs, and a concurrent second request cannot book it. |
| 4. Closure and recovery | Effective restriction, event delivery, affected-route lookup, withdrawal, replacement review, projection refresh and retry behavior. | The closed crossing is removed from current guidance. A valid replacement or explicit unresolved state is visible. Redelivery cannot restore the old route. |
| 5. Demo and verification | Browser acceptance test, repeatable dataset, live-provider smoke test where access exists, and a concise demo runbook. | The fixture run passes from a clean setup without a live provider key, and the presenter can replay the same scenario after setup. |

The [work packages](mvp-work-packages.md) split this work by aggregate. Keep their owner fields available for team assignment. Each package should deliver a contract example and at least one acceptance check that exercises its responsibility through the owning API or domain operation.

## CI, local diagnostics and completion

Set up Actions to run the following stages on a pull request. These are the intended commands after the project skeleton and scripts exist.

```text
dotnet tool restore
dotnet restore Cassini.sln --locked-mode
dotnet build Cassini.sln --no-restore -c Release
dotnet test Cassini.sln --no-build -c Release --filter "Category!=Infrastructure"
npm --prefix apps/web ci
npm --prefix apps/web run typecheck
npm --prefix apps/web run build
docker compose config --quiet
docker compose up --build -d --wait
dotnet test Cassini.sln --no-build -c Release --filter "Category=Infrastructure"
npm --prefix tests/e2e ci
npm --prefix tests/e2e exec -- playwright install --with-deps chromium
npm --prefix tests/e2e run test:e2e
```

Use the xUnit v3 VSTest test-project configuration for the `dotnet test --filter` commands above. Mark tests that need a live PostgreSQL or RabbitMQ dependency with the `Infrastructure` category. Ensure `docker compose up --wait` includes migration and fixture prerequisites through one-shot jobs or run those explicitly before waiting on APIs. On failure collect Compose logs and the Playwright trace. Log `correlationId`, command or event ID, subject reference and problem code. Keep individual coordinates and assistance details out of shared diagnostic logs. Inspect queue retries, oldest outbox entry and the affected subject version when the map lags.

The demo is ready when someone can start from clean fixtures, run the [acceptance matrix](mvp-routing-and-demo.md#acceptance-matrix), and repeat the scenario with the same results. A live OpenRouteService check, a verified Galileo device fix and a FlyPix workspace export are separately identified demonstrations. The presenter can label the replay as such when any live source is unavailable.
