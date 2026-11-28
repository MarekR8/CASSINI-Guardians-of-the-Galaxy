# SH-T01 Assembly points and shelter resources

- **Owner:** __________
- **Directories:** `src/Situation/AssemblyPoints/`, `src/Sheltering/Catalog/`, `data/demo/sheltering/`
- **Technologies:** C#, ASP.NET Core, local fixtures, existing Situation snapshots
- **Baseline:** [01 Copernicus hazard layers](../01-copernicus-hazard-layers.md) and [02 road network and closures](../02-road-network-flypix-closures.md).

## What the demo shows

The coordinator opens one assembly point and two shelters. A shelter operator records the usable places, accessible places and intake windows. A site that loses eligibility remains visible with evidence and cannot receive a new assignment.

## Scope

### Stage A (demo)

- [x] Implement `SetAssemblyPoint` in Situation and `ConfigureShelter` in Sheltering, with ownership and expected versions. Record place, access, time windows and source evidence.
- [x] Provide `GetAssemblyPoints` and `GetShelterAvailability` for SH-W1. Include previews of usable capacity, active holds, actual occupancy and the revision checked. SH-T04 and SH-T05 own the operations that change holds and occupancy.
- [x] Seed two synthetic shelters and one assembly point tied to the shared `sample` graph. A point and shelter may share a location, with separate functions and counters.

### Stage B (completion)

- [x] Commit configuration, audit history, command receipts, `AssemblyPointChanged.v1` and `ShelterAvailabilityChanged.v1` in the owner stores. Verify restart recovery of pending events.
- [x] Enforce coordinator activation and site-scoped resource updates with authenticated roles.
- [ ] Verify broker publication and redelivery end to end with SH-T06.

## Dependencies and verification

Receive SH-W0 first. Deliver SH-W1 to SH-T03, SH-T04 and SH-T07. Capture a site configuration and availability example under `contracts/http/` and synthetic source references alongside the scenario. Verify that closed or unverified sites cannot become destinations, that assembly check-in cannot reserve a bed, and that an operating-window change preserves existing occupancy. Contributes to SH-A01, SH-A04 and SH-A06.

## Results

Stage A uses `assembly-north`, `shelter-a` and `shelter-b` from the [synthetic sample fixture](../../../data/demo/sheltering/sample-sites.json), with evidence from the exercise Situation snapshot at revision 0. See the [interface contracts, examples and future handoffs](../../sheltering-interfaces.md).

The offline integration run `python scripts/offline_demo.py --sheltering --test` covers catalog revisions, eligibility, time windows, command retries, concurrent edits, restart recovery and preservation of seeded commitments.

## Stage B operation

Build with `dotnet build Cassini.Offline.slnx -m:1 -p:EnableSiteCatalogStageB=true`.
Set these values for the Situation and Sheltering owners before starting them. The
launcher preserves settings supplied in the environment, so `python scripts/offline_demo.py --sheltering --no-build`
can run either the offline exercise or the authenticated catalogs.

| Environment variable | Purpose |
| --- | --- |
| `SiteCatalog__AccessMode` | `oidc` enforces authenticated catalog writes. Direct startup defaults to `readonly`. The offline launcher and demo Compose overlay select `demo` explicitly for the synthetic scenario. |
| `SiteCatalog__Authority` | Trusted HTTPS issuer, such as the organization's Keycloak realm URI. |
| `SiteCatalog__Audience` | Catalog API audience in access tokens. Configure this separately from the browser's client ID. |
| `Sheltering__IncidentId` | Incident ID for this shelter catalog. Required in OIDC mode. Use distinct state storage for distinct incident deployments. |
| `ConnectionStrings__AssemblyPointCatalog`, `ConnectionStrings__ShelterCatalog` | Connection strings for the owners' separate PostgreSQL databases. Supply credentials through deployment configuration. Apply [the migration](../../../src/Demo/SiteCatalog/001-site-catalog.sql) to each database before starting the owners. |
| `ConnectionStrings__Sheltering` | Use this instead of `ShelterCatalog` when the Sheltering service uses its allocation database. Build with both `EnableSiteCatalogStageB=true` and `EnableShelteringPostgres=true`. Sheltering applies its own migrations to that database. |
| `AssemblyPoints__SeedFile`, `Sheltering__SeedFile` | Catalog fixture paths. The launcher supplies the synthetic fixture. For standalone Situation, set the assembly-point seed path to enable the catalog. |
| `AssemblyPoints__StateFile`, `Sheltering__StateFile` | Local files when no connection is set. An existing file snapshot can bootstrap an empty database catalog. |

The signed access token supplies `site_role`, `incident_scope` and, for operators,
`shelter_scope`. Configure these claims as strings or arrays in the issuer and
manage the grants through administrator-controlled roles and attributes.
Coordinators with the incident scope can configure sites and change their operating
status. A shelter operator also needs the exact shelter scope and can update only
`resources` on an existing shelter. Use the existing command and send the other
fields from the current item. The owner preserves existing commitments.
Send the token as `Authorization: Bearer <access-token>` to Gateway or the owner.
The catalog endpoints accept a Keycloak access token with `typ: Bearer` in the
claims or an RFC 9068 token with an `at+jwt` type header. A missing or invalid
token returns `401`, and insufficient scope returns `403`.

Each committed write records an audit entry with the authenticated subject and
the before and after state. The JSON backend atomically replaces the owner's
snapshot on disk. The PostgreSQL backend locks the owner row and writes the
snapshot in a single transaction. In the allocation database those writes use
the same transaction as the existing Sheltering store with separate audit and
outbox tables. A retry by the same actor returns the
same receipt and keeps the original event ID. Existing Stage A receipts can
still replay in demo mode. Audits and outbox entries stay inside the owner store.

The pending outbox entries carry a routing key, a versioned event envelope and
`publishedAt: null`. The envelope uses the command ID for causation and
correlation in this slice. It contains site identity, state, operating window and
reference to evidence. Shelter events add the changed resource types. SH-T06
will publish the pending entries and verify broker recovery and redelivery.

Run `python tests/sheltering_stage_b.py` on the Stage B build with OpenSSL
available on PATH. The test uses an ephemeral loopback OIDC test issuer and
checks role and scope denial, idempotency and persisted audit and outbox after
restart. Run it with `--postgres` and two fresh, migrated databases to test the
PostgreSQL backend. The loopback HTTP metadata option is for the test issuer.
