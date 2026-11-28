# Refactor unification handoff

Branch: `feature/refactor-unification`; verified base: `e73867ac8251b204504b64d65bb074698d639859` (`origin/develop`).
The original working tree was clean; the guide remains on `feature/refactoring-guide` (`8394157`).

## Checkpoint 1 — baseline CI and contract inventory

- Removed the duplicate SH-A01 destination-routing invocation from standard CI; its original mandatory invocation remains.
- Updated `src/README.md` to describe the actual projects and ownership, not the empty initial placeholders.
- Added a synthetic `AssemblyPointChanged.v1` contract example matching the Situation catalog producer and documented its routing key. No runtime contract changed.
- The current runtime comprises five independent .NET owners plus Gateway. All default state is local; optional broker and database paths are retained.

## Runtime inventory (initial, to refine alongside each slice)

| Owner | Writes / reads | Storage, events and profiles | Existing checks |
| --- | --- | --- | --- |
| Management | Alerts, progress, EWSS and Grouping commands; incident/case/plan queries | JSON / optional PostgreSQL, outbox and memory/RabbitMQ; Grouping fixture opt-in | Offline, Grouping, Management unit/full |
| Situation | Closures, reviews, imports, assembly-point commands; snapshot and catalog reads | JSON, optional catalog PostgreSQL and outbox; sample/Valencia | Offline, destination, catalogs, site events |
| Routing | Draw and instruction commands; distributions, destination and instruction reads | Local journal, optional RabbitMQ; graph, recorded/live ORS and forecast modes | Routing unit, destination, reservation/publication journeys |
| Dispatch | Assistance, unit assignment and progress commands; task and ranking reads | JSON / optional PostgreSQL; optional RabbitMQ notifications | Offline, ORS, PostgreSQL and notification suites |
| Sheltering | Catalog, assignment, reservation, admission/departure writes; availability and reception reads | JSON / optional PostgreSQL; catalog and allocation outboxes, optional RabbitMQ; demo/readonly/OIDC catalog access | Catalog/reception, reservation, publication, broker, database and browser suites |
| Gateway | Forwarding, link-profile and queue operations; composed client views | Local state, optional sign-in, loopback-only public entry | Offline, connectivity, Gateway unit and browser suites |

## Confirmed gaps and priorities

| Priority | Evidence | Classification / acceptance |
| --- | --- | --- |
| P1 | `scripts/offline_demo.py` selects either offline or shelter tests; `compose.sheltering.yaml` is an opt-in overlay | Confirmed missing default integration. Default sample must include the fifth owner and a mandatory same-session workflow. |
| P1 | Grouping evidence and Routing destination evidence only wired in test setup; existing rehearsal uses separate fresh-state suites | Confirmed integration gap, not yet a combined journey. Keep existing regression suites and add a real same-state scenario. |
| P1 | `src/Management/Program.cs`, `src/Dispatch/Program.cs` and local Gateway demo wiring | Authorization and data-scope review required: confirm intended demo vs authenticated behavior and preserve fail-closed protected paths. No security defect claimed without validation. |
| P2 | `docs/offline-demo.md` vs `README.md`; launcher accepts only sample/Valencia, while README still mentions Warsaw | Confirmed documentation discrepancy; preserve actual supported runtime and research removed Warsaw assets before making a behavior change. |
| P2 | `offline-ci.yml` duplicate destination check | Corrected in this checkpoint. PostgreSQL job service blocks match the jobs that reference them on this base. |
| P2 | `src/README.md` and missing event fixture | Corrected in this checkpoint. |
| P2 | `docs/tools/README.md` only provides the initial short list | External verification and integration matrix pending. No live hardware, real provider credentials or partner APIs have been verified. |

## Verification / limits

- `dotnet build Cassini.Offline.slnx -m:1`: passed (0 errors; 4 NU1900 warnings, vulnerability feed unavailable).
- `python -m json.tool contracts/events/AssemblyPointChanged.v1.json` and `git diff --check`: passed.
- `docker compose config --quiet`: passed; engine access denied in restricted execution, so image build/start not verified.
- GitHub connector returned no pull-request workflow run for the merge SHA; baseline remote CI status not established.
- Initial HTTP tests in restricted execution could not reach the local ready endpoint although the service was listening. Repeat outside that restriction on alternate local ports: all passed (`offline_demo.py --test --no-build`, `offline_demo.py --sheltering --test --no-build`, `destination_routing_integration.py --no-build`). Coverage included alert/draw/closure, Dispatch, connectivity, catalogs and restart, reception and restart, first-leg fallback and blocked exit. No external live data was used.

## Checkpoint 2 — sample launcher includes Sheltering

- The standard `python scripts/offline_demo.py --scenario sample` now starts Sheltering, configures owner URLs and exposes the feature through Gateway. The normal `--test` verifies the feature as well as the original offline regression.
- Legacy `--sheltering` still selects sample, keeps its original state directory and retains the isolated catalog/reception suite. Default Valencia launching without CLI options is unchanged; sample-only data is not enabled for Valencia. No synthetic destination booking is injected into the production-like sample path.
- `python scripts/offline_demo.py --test --no-build --port 15080`, `python scripts/offline_demo.py --sheltering --test --no-build --port 15080`, `python tests/grouping_integration.py --no-build --port 15180`, Python compilation and `git diff --check`: all passed. Valencia inputs were not prepared/run in this checkpoint; no Compose or service code changed.
- Checkpoint 1 pushed as `411bfa07dd8a5474f1cd9aa805a2afb4b907d1d7`; remote SHA matched. PR creation was denied by GitHub integration (`403`); [create PR to develop](https://github.com/MarekR8/CASSINI-Guardians-of-the-Galaxy/compare/develop...feature/refactor-unification?expand=1). The connector only lists PR-triggered workflows and returned none for that commit; CI is not confirmed.

## Checkpoint 3 — default Compose includes Sheltering

- Moved the sample owner, private network, own volume, health check and service URLs into `compose.yaml`. The legacy sample overlay remains usable. Valencia explicitly disables sample seed, destination URL and gateway exposure (it has no verified Sheltering catalog); its service list still has the original five processes. No data from sample is presented as Valencia shelter capacity.
- Added the Valencia Compose configuration check in CI and a mandatory feature assertion in the base-image rehearsal. Standard image build now uses base Compose directly.
- `docker compose config --quiet` passed for base, legacy, Valencia, PostgreSQL and RabbitMQ overlays; base service list includes Sheltering. Docker Desktop engine 29.3.1 / Compose 5.1.1: all six images built. `python tests/sheltering_rehearsal.py --images-only --image-port 16880`: passed base offline/connectivity, legacy catalog, no-internet probe and catalog restart. `offline_demo.py --test --no-build` and `--sheltering --test --no-build` both passed on local port 15080. `git diff --check` passed. Valencia data was not prepared locally; actual Valencia startup and browser UI were not verified here. PostgreSQL/RabbitMQ config validated, but their runtime suites were not run in this checkpoint.
- Checkpoint 2 pushed as `b0eccb64481b11a10e8ca48c9feeed6bd545ed6f`; remote SHA matched. PR remains blocked by the integration; no PR-triggered CI run was returned for checkpoint 2.

## Checkpoint 4 — mandatory default sample journey

- Enabled Grouping's existing synthetic evidence file in the local sample launcher (demo mode only, explicit configuration untouched) and default Compose; Valencia disables it. The Management image carries that file. No synthetic booking or hold fixture was added to the default runtime.
- Added `tests/sample_sheltering_flow.py` to ordinary `offline_demo.py --test` (fresh state distinct from the regression that disables every exit) and the default-image rehearsal. It exercises an alert, weighted draw, full sites with independent first leg, bridge closure/withdrawal and northern exit, explicit simulated position/exit, required-care split rejection, separate pending assistance, reservation, publication, closure withdrawal, new site through the outside-only connector, admission and departure with idempotent receipts. The case-14 allocation is ungrouped; separate cases 15/16 exercise required-care and unresolved transport. This is not yet a live approved group-plan/dispatch-to-reservation chain, and the test intentionally does not claim a verified transport window where none exists.
- Local tests passed: standard launcher with build and again without build, legacy catalog/reception, Grouping, destination routes, reservations, publication, Management/Routing/Sheltering domain checks. The first Management test attempt used an inaccessible OS temp location; rerun with workspace `TEMP`/`TMP` passed. Base and Valencia Compose configs passed. Docker built all six images and `tests/sheltering_rehearsal.py --images-only --image-port 16880` passed all image steps, including the new default sample flow, offline probe and restart. No actual Valencia, device or provider run; no UI/Playwright run yet.
- Checkpoint 3 pushed as `4a7b24325bd79a57caddfbeec02ade8ed7c345e1` and verified. GitHub integration returned no PR-triggered workflows. The public Actions page could not be fetched. CI remains unconfirmed; PR remains blocked by permissions.

## Checkpoint 5 — defensive Gateway query policy

- The authenticated resident role now reads only explicit public Situation/network/routing operations. Private, case-bound and unknown operations default to staff-only; authorized staff and the local demo retain previous behavior. Gateway role-policy unit tests exercise allowed and denied public-operation classes, and the runbook no longer claims all roles can read all queries. This is defense at the Gateway boundary, not complete owner-level authorization; the demo remains local-only.
- `dotnet build Cassini.Offline.slnx -m:1` passed (0 warnings/errors, advisory feed reachable on the authorized run). Gateway tests, standard offline and legacy shelter/reception tests passed. Gateway image rebuilt, fresh default stack became healthy, and `npm test` under `tests/e2e` passed the sample browser journey (1 passed, Valencia intentionally skipped). Only the isolated test project was removed afterwards. Keycloak role tokens/full-profile browser were not exercised; role policy was tested directly. No actual residents or credentials were used.
- Checkpoint 4 pushed as `8873a4403cfd4623bfbfa840a1b61e759e8d87da`, remote SHA matched. Connector returned no PR-triggered workflows; CI remains unconfirmed, PR permission still blocked.

## Next step

Document the verified 12th-edition organizer tools and exact existing offline artifacts/limitations in `docs/tools/README.md`; include blank team-supplied inputs and current service/sequence diagrams. Then reconcile service-level allocation and Grouping authorization, including authenticated owner-to-owner reads, before claiming protected deployment. Extend Grouping to revalidate current owner route evidence and Dispatch to supply confirmed transport evidence to the same-state flow.
