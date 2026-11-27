# MVP technology stack

**Decision baseline:** 27 November 2026. This is the selected stack for implementing the four [MVP workflows](business-processes.md). Use a single repository, four independently running API services and one web app. The [interface specification](mvp-interfaces.md) defines the endpoints, message bindings and ownership rules. The paths and deployment settings below describe the implementation target.

## Core stack and versions

| Layer | Selected technology | Where it is used |
| --- | --- | --- |
| API runtime | **C# 14, .NET 10 LTS**, SDK `10.0.404`, runtime `10.0.15`, ASP.NET Core Minimal APIs | Management, Situation Data, Routing and Dispatch. Target `net10.0` in all services. |
| HTTP and contracts | `System.Text.Json`, `IHttpClientFactory` typed clients, `Microsoft.AspNetCore.OpenApi` `10.0.x`, OpenAPI 3.1, RFC 9457 Problem Details | Web and service calls. Keep interface examples in `contracts/` and expose each generated OpenAPI document in the development environment. |
| Persistence | **PostgreSQL 17**, EF Core `10.0.15`, `dotnet-ef` `10.0.15`, `Npgsql.EntityFrameworkCore.PostgreSQL` `10.0.3` | Each service owns its database, credentials and migrations. Use explicit concurrency tokens and database constraints for the rules each service owns. |
| Spatial storage | **PostGIS 3.7** with `postgis/postgis:17-3.7` and `Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` `10.0.3` | Situation Data stores hazard polygons, road lines, closures and exit points. Use SRID 4326 for stored geometries, spatial indexes for intersections, and metric calculations through `geography` or an appropriate projected CRS. |
| Events | **RabbitMQ 4.4**, image `rabbitmq:4.4-management`, AMQP 0-9-1 and `RabbitMQ.Client` `7.2.2` | Durable topic exchange `evacuation.events`, service queues, publisher confirms, bounded retries and dead-letter queues. Implement outbox and consumer deduplication in PostgreSQL. |
| Web runtime and build | **Node.js `24.22.3` LTS**, npm 11, **React and React DOM `19.2.7`**, **TypeScript `5.9.3`**, **Vite `8.4.0`**, `@vitejs/plugin-react` `6.x` | One web app for the coordinator map and resident and unit views. Use native `fetch` and start with periodic refresh of the operational map. |
| Map | **Leaflet `1.9.4`**, `@types/leaflet` `1.9.x`, GeoJSON and the OpenStreetMap standard tile layer for the connected demo | Draw domain layers with `L.geoJSON`. Show OpenStreetMap attribution when its tiles are used. Keep the demo's hazard, road and route overlays local so they can still be drawn if the tile layer is unavailable. |
| Identity | **Keycloak `26.8.2`**, `keycloak-js` `26.2.5`, OpenID Connect Authorization Code with PKCE `S256`, ASP.NET Core `JwtBearer` `10.0.x` | Browser sign-in, API token validation and service identities. Apply case, incident and assignment access checks inside the service that owns the data. |
| Web entry point | **NGINX `1.30` Alpine**, image `nginx:1.30-alpine`, and static Vite build output | Serve the web app and proxy same-origin API requests to the four services. Use Vite's development proxy for local hot reload. |
| Algorithms and validation | **C#** in Routing; **Python `3.14.10`**, `venv` and **NetworkX `3.7`** for experiments | The mathematician produces a reference model and expected results from shared fixtures. Routing evaluates provider candidates and enforces eligibility in C#. |
| Testing | **xUnit.net v3**, `Microsoft.AspNetCore.Mvc.Testing` `10.0.x`, `@playwright/test` `1.x` with Chromium | Domain and API integration checks in .NET, one end-to-end web scenario using Compose. Test with seeded data and recorded provider responses. |
| Operations | **Docker Engine and Docker Compose v2 CLI**, Docker Desktop with WSL 2 on Windows, **GitHub Actions** on `ubuntu-24.04` | Run the complete demo on one x86-64 host. CI builds and tests from the same committed manifests and lockfiles. |
| Diagnostics | ASP.NET Core Health Checks, `ILogger` JSON console logs, W3C `traceparent` and application `correlationId` | Use `/health/live`, `/health/ready` and `docker compose logs`. Carry the correlation ID through HTTP, outbox messages and rejection responses. |

Version references: [.NET 10 SDK and runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [EF Core package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore), [Npgsql spatial mapping](https://www.npgsql.org/efcore/mapping/nts.html), [PostGIS image](https://hub.docker.com/r/postgis/postgis), [RabbitMQ images](https://github.com/docker-library/docs/blob/master/rabbitmq/README.md), [Node.js LTS release files](https://nodejs.org/download/release/latest-krypton/), [React versions](https://react.dev/versions), [Vite 8 and React plugin](https://v8.vite.dev/blog/announcing-vite8), [Leaflet releases](https://leafletjs.com/download.html), [Keycloak distribution and adapter](https://www.keycloak.org/downloads), [NetworkX installation](https://networkx.org/documentation/stable/install.html).

Pin tooling in `global.json`, `.nvmrc` and `experiments/routing/.python-version`. Pin NuGet versions centrally in `Directory.Packages.props` and commit the restore locks. Use `package-lock.json` and `npm ci` for the frontend. Select exact patches for the package lines in the table as part of the first locked restore. Record resolved image digests after verifying the selected tags on the demo host.

## Binding the technology to the domain

| Module | Owns | Technology and boundary |
| --- | --- | --- |
| `src/Management` | `EvacuationIncident`, `EvacuationCase`, coordinator read model | ASP.NET Core and EF Core in `management_db`. Receives alerts and position or progress reports. Reads Situation Data over HTTP and projects route and assignment events from RabbitMQ. |
| `src/Situation` | `HazardAssessment`, `NetworkSegment`, `SituationSnapshot` | ASP.NET Core, EF Core, PostGIS and NetTopologySuite in `situation_db`. Imports sourced layers and publishes effective restriction changes. Gives Routing versioned spatial and access evidence. |
| `src/Routing` | `RouteInstruction`, candidate proposals and route revisions | ASP.NET Core and EF Core in `routing_db`. A typed `HttpClient` calls OpenRouteService Directions and a separate client validates candidates with Situation Data. Routing stores the active version and publishes changes through RabbitMQ. |
| `src/Dispatch` | `AssistanceRequest`, `ResponseUnit`, assignments and bookings | ASP.NET Core and EF Core in `dispatch_db`. Calls Routing for approach and outbound legs. PostgreSQL enforces active-request and active-unit uniqueness in the booking transaction. |
| `apps/web` | Map composition and the screens for each role | React and Leaflet combine authorized GeoJSON overlays and status data. TypeScript clients follow the HTTP contracts. Web requests go through the same-origin entry point. |
| `experiments/routing` | Reference calculations and expected fixture results | Python and NetworkX operate on `data/demo/` fixtures. Use their expected outcomes to check the C# eligibility and ranking rules. |

Use direct C# calls within a service. Use HTTP for decisions that need a synchronous result and RabbitMQ for facts committed by another service. Configure an outbox worker using `BackgroundService` in each publishing service. For route activation, validate with the latest Situation Data evidence immediately before saving the instruction. The matching command IDs and message keys are defined in the [interface specification](mvp-interfaces.md).

## Maps, routing and European space integrations

| Technology | Exact MVP integration and data passed | Demo status |
| --- | --- | --- |
| **Copernicus EMS** | Select one flood delineation area of interest, with `EMSR756` as the candidate activation. Convert the source vector product to RFC 7946 GeoJSON with GDAL `ogr2ogr` if conversion is needed. Import it into Situation Data with product ID, observation time, coverage and provenance. | Archived Copernicus evidence in the replay. |
| **FlyPix AI** | Use the FlyPix workspace with compatible imagery. Export a selected detection or annotation layer as GeoJSON. Import it as review evidence with imagery date and method. A verified closure then goes through the `NetworkSegment` command. | Real exported review layer if imagery and workspace access are available. Use a labeled reference layer for development. |
| **OpenRouteService** | Hosted Directions API, `POST /v2/directions/{profile}/geojson`, through `HttpClient`. Configure the provider base URL and API key on Routing. Use `foot-walking`, `driving-car` or `wheelchair` as appropriate. Send candidate start and exit or pickup points and applicable `avoid_polygons`. Validate the returned geometry, access and effective restrictions in Situation Data before accepting it. | One live request on the demo graph when available and recorded responses keyed by inputs for repeatable runs. Use synthetic positions in provider requests for the demo. |
| **Galileo** | Android's native location and `GnssStatus` APIs on a compatible device or a labeled replay of a recorded fix. Carry coordinates, `observedAt`, horizontal accuracy if available and verified constellation evidence into a progress report. | Demonstrable measured input if the device can verify Galileo was used in its fix. |
| **EGNOS** | The same position input accepts an augmentation indicator from a compatible receiver that reports actual use. Keep augmentation evidence separate from Galileo constellation evidence. | Hardware-dependent extension. |
| **GOVSATCOM** | Future IP connectivity for an authorized and equipped coordination point. The application retains its existing HTTPS interfaces. | A labeled connectivity interruption and retry demonstrate the application behavior on ordinary connectivity. |
| **IRIS²** | Future alternative or additional satellite backhaul for an equipped coordination point. | Roadmap for connectivity. |
| **EU SST** | Space-object monitoring, collision, fragmentation and re-entry services. | Outside the flood MVP's data flow. |

Integration references: [Copernicus EMS flood activation](https://mapping.emergency.copernicus.eu/news/information-bulletin-173-the-copernicus-emergency-management-service-monitors-floods-in-central-and-eastern-europe/), [GDAL vector conversion](https://gdal.org/en/stable/programs/ogr2ogr.html), [FlyPix export](https://docs.flypix.ai/docs/guides/annotation/edit-and-manage-vectors), [OpenRouteService Directions](https://giscience.github.io/openrouteservice/v9.10.0/api-reference/endpoints/directions/requests-and-return-types), [OpenRouteService routing options](https://giscience.github.io/openrouteservice/api-reference/endpoints/directions/routing-options), [Android GNSS status](https://developer.android.com/reference/android/location/GnssStatus), [EGNOS services](https://www.euspa.europa.eu/eu-space-programme/egnos/services), [GOVSATCOM](https://www.euspa.europa.eu/govsatcom-hub), [ESA IRIS²](https://resilience.esa.int/esa-programme-related-to-eu-secure-connectivity-and-iris%C2%B2), [EU SST](https://www.eusst.eu/services), [OpenStreetMap tile policy](https://operations.osmfoundation.org/policies/tiles/).

## Deployment and development configuration

| Compose service | Image or build | Network or storage |
| --- | --- | --- |
| `web` | Build with `node:24.22.3-bookworm-slim`, serve with `nginx:1.30-alpine` | Browser at `http://localhost:8080`. Proxy `/api/management`, `/api/situation`, `/api/routing` and `/api/dispatch` to the private APIs and `/auth` to Keycloak. |
| `management`, `situation`, `routing`, `dispatch` | Build with `mcr.microsoft.com/dotnet/sdk:10.0.404`, run with `mcr.microsoft.com/dotnet/aspnet:10.0.15` | Each listens privately on port `8080`. Use separate connection strings and database roles. |
| `postgres` | `postgis/postgis:17-3.7` | One local PostgreSQL instance on internal `5432` with a named volume. Create `management_db`, `situation_db`, `routing_db`, `dispatch_db` and `keycloak_db` with separate owners. Enable PostGIS in `situation_db`. |
| `rabbitmq` | `rabbitmq:4.4-management` | Internal AMQP on `5672` and named volume. Expose its management UI on loopback only when required for debugging. |
| `keycloak` | `quay.io/keycloak/keycloak:26.8.2` | Internal port `8080`, `keycloak_db`, demo realm `evacuation`, and browser-facing issuer through `/auth`. |

Use one demo API audience and roles `resident`, `coordinator`, `dispatcher` and `responder`. Bind residents and responders to their authorized cases or assignments. Give services separate client credentials and verify service identity at the receiver. Persist the authorized dispatcher in the booking workflow for later route publication. Configure the browser-facing issuer and private Keycloak backchannel so containerized APIs can discover keys while validating the same issuer used by the web app. External deployments use HTTPS. [Keycloak hostname and backchannel](https://www.keycloak.org/server/hostname), [Keycloak JS adapter](https://www.keycloak.org/securing-apps/javascript-adapter), [Compose](https://docs.docker.com/compose/).

Put non-secret defaults in `.env.example`. Load local secrets from ignored configuration, including database credentials, RabbitMQ and Keycloak service credentials, and the OpenRouteService key. Supply CI credentials through the workflow's secret store if a job requires them. The release check uses the recorded fixture provider path and starts without a live API key.

## Build and verification order

1. **Bootstrap.** Create `Cassini.sln`, the four service projects, `apps/web`, `compose.yaml`, migrations, and demo identity configuration. Mirror the service paths in the existing work packages. The local target command is `docker compose up --build`.
2. **Load the shared scenario.** Seed one flood extent, one bridge and alternative corridor, two residents and one response unit. Keep the fixture IDs identical in the web layers, API examples and algorithm checks.
3. **Connect the workflows.** Build alert to map, pedestrian and unit proposals, assignment, bridge closure, route withdrawal and approval. Pass configuration for hosted or recorded OpenRouteService responses through the same adapter interface.
4. **Verify in CI.** Run locked .NET restore, build and xUnit checks. Run `npm ci`, TypeScript checking and a Vite build. Boot Compose, apply migrations and fixtures, and run a Playwright Chromium scenario. Cover the duplicate unit booking, a withdrawn route after closure, and the distinction between pickup and confirmed exit. Keep the CI run independent of live providers.
