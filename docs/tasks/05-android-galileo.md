# Task 5 — Android: Galileo, OSNMA, EGNOS, EWSS

**Owner:** __________
**Directories:** `apps/offline-android/`
**Technologies:** Galileo Open Service (`GnssStatus`, `LocationManager`), OSNMA (simulated flag), EGNOS, EWSS (alert reception, simulated)

## What the demo shows

A resident's phone: a Galileo position with its evidence (constellation, OSNMA, EGNOS). A short EWSS alert arrives and the app shows the route stored offline. The resident leaves the zone and sends an **exit confirmation** with position evidence. Without a network the report waits in a queue and is delivered after reconnecting.

## Technology concept

- **Galileo**: the current `GalileoReader.cs` confirms Galileo was used in the fix. The position now goes into a progress report.
- **OSNMA**: Android exposes no authentication flag. `IOsnmaVerifier` has `SimulatedOsnmaVerifier` (always labeled `simulated`) and a slot for real verification from raw navigation messages (`GnssNavigationMessage` + e.g. OSNMAlib on the server side) in Stage B.
- **EGNOS**: the flag is set only when the receiver reports SBAS use (`GnssStatus.CONSTELLATION_SBAS` + `usedInFix`). Otherwise `unknown`, never `true` by assumption.
- **EWSS**: a short message via Galileo. `IEwssReceiver` with `MockEwssReceiver` (button / file in assets). The alert opens the stored route; it does not carry the route.

## Scope

### Stage A (demo)
- [ ] "Confirm exit" screen: collects position, `observedAt`, `accuracy`, `evidence { galileoUsed, osnma: "simulated"|"verified"|"none", egnos: "applied"|"unknown" }`.
- [ ] HTTP client for Management `POST /commands/progress` (Gateway address in settings) with an offline queue and a stable `commandId` on retry. Default mock mode: local storage + JSON preview.
- [ ] `MockEwssReceiver`: alert with a level and text → banner + opens the route.
- [ ] Every simulated field is visibly labeled as a simulation on screen.

### Stage B (completion)
- [ ] Capture `GnssNavigationMessage` (Galileo I/NAV) to a file as input for real OSNMA verification.
- [ ] Fetch the current route from Routing when connected and compare versions (an older version never overwrites a newer one).

## Dependencies

- You wait for: **W2** (report contract from person 6). Until then, store reports locally in the agreed draft JSON.
- Waiting on you: person 7 (confirmed exits counter on the dashboard).
- **Requires hardware**: a phone with Galileo (most new ones). Test in airplane mode with GNSS on.

## Where to find data

- List of Galileo-enabled phones: `usegalileo.eu`. Raw measurements: the GnssLogger app (Google) for comparison.
- OSNMA: Galileo OSNMA documentation (ICD, EUSPA), OSNMAlib on GitHub.
- EWSS: Galileo Emergency Warning Satellite Service description (EUSPA). Message format: CAMF.

## How to test

```sh
dotnet run --project apps/offline-android/tests/Offline.Tests.csproj -p:NuGetAudit=false
dotnet build apps/offline-android/Cassini.Offline.Android.csproj -c Release
```
- Unit test: EGNOS is never `applied` without SBAS `usedInFix`; OSNMA in the mock is always `simulated`; a resend keeps the same `commandId`.
- On a device: airplane mode → report queued → Wi-Fi on the Gateway's network → exactly one record in `GET /api/management/queries/incident`.
- Note: `apps/offline-android` currently has no `INTERNET` permission. Add it deliberately and document it in the app README.

## Where to save results

- GNSS logs from trials: outside Git (they contain real positions!). Commit only an anonymized, shifted trace `data/demo/gnss/replay-*.json` labeled `replay`.
- Section below: phone model, whether Galileo/SBAS are visible, time to fix.

## Results

_(fill in)_
