# Task 4 — Dispatch, response units and OpenRouteService

**Owner:** __________
**Directories:** `src/Dispatch/`, `src/Routing/Providers/` (new), `data/demo/ors/`
**Technologies:** OpenRouteService Directions API, typed `HttpClient`, EGNOS (unit position integrity, simulated)

## What the demo shows

A person without a car asks for transport. The dispatcher sees a unit, its position with a quality indicator (EGNOS), and approach and outbound routes drawn on real roads from ORS. A second request cannot book the same unit. After the bridge closes, the unit's route is recalculated.

## Technology concept

- **OpenRouteService**: `POST /v2/directions/{profile}/geojson` with `start`, destination and `options.avoid_polygons` (the closed bridge polygon). Profile `driving-car` for units, `foot-walking` for pedestrians. `IRouteProvider` has `OrsRouteProvider` (live, key from configuration) and `RecordedRouteProvider` (response from a file, key = hash of the normalized request). A missing recording is an explicit `FixtureMissing` error, never a silent substitution.
- **An ORS candidate is not the truth**: before booking, the geometry is validated in Situation (`ValidateRoute`, W5).
- **EGNOS**: the unit reports its position with an `egnosApplied` flag and `hpl` (protection level). The mock returns values from a file. No confirmation or `hpl` too large → `PositionUnverified`, and the dispatcher confirms the start point manually.

## Scope

### Stage A (demo)
- [x] `IRouteProvider` + both providers; configuration `Integrations:Ors:Mode`, `Integrations:Ors:BaseUrl`, `Integrations:Ors:ApiKey` (user-secrets).
- [x] Capture and verify ORS approach and outbound responses before and after bridge closure. The repository includes synthetic fixtures in `data/demo/ors/recorded/` for repeatable tests. Verified hosted recordings are generated under `artifacts/`.
- [x] Dispatch uses the provider for approach and outbound legs; keep the existing double-booking guard and `commandId` idempotency.
- [x] Unit position with `positionEvidence { source, egnosApplied, hpl, mode }` in `GET /queries/assignments`.

### Stage B (completion)
- [ ] Rank units by approach time using the ranking from task 3 (W4).
- [ ] React to `RouteWithdrawn.v1` / `SituationChanged.v1` from RabbitMQ (W3) instead of manual `replan`.
- [x] Dispatch in PostgreSQL: active unit and request booking uniqueness as database constraints.

## Dependencies

- You wait for: W1 (point coordinates from person 2's graph), W5 (validation), W4 and W3 in Stage B.
- Waiting on you: person 7 (unit route geometry on the map).

## Where to find data

- ORS key: sign in to [HeiGIT Account](https://account.heigit.org/) and copy the basic key in the API Key tab.
- Docs: `giscience.github.io/openrouteservice` → Directions, Routing options (`avoid_polygons`).
- EGNOS: HPL/integrity descriptions on EUSPA pages; choose realistic mock values (HPL of a few to a dozen metres).

## How to test

```sh
python scripts/offline_demo.py --test        # default offline graph, no key
python tests/dispatch_ors_integration.py     # after building the solution
dotnet user-secrets set Integrations:Ors:ApiKey <key> --project src/Routing/Cassini.Routing.csproj
Integrations__Ors__Mode=live dotnet run --project src/Routing/Cassini.Routing.csproj   # manual recording
```
- Test: missing recording → `FixtureMissing`; same input → same response; a candidate through the closed bridge → rejected by validation.
- Integration test: two parallel bookings of the same unit → only one wins (already in `tests/offline_integration.py`, must not break).

## Where to save results

- Recordings: `data/demo/ors/recorded/<hash>.json` + `index.json` (request → file, recording date).
- Section below: API limits, response times, ORS vs demo graph differences.

## Results

Routing owns the provider request and candidate validation. It uses the current
Situation revision and known closure geometry before returning either leg.
Dispatch checks that revision before saving a booking or replan and retains the
existing command receipt and active booking checks.

| `Integrations:Ors:Mode` | Route source |
| --- | --- |
| `graph` (default) | Local exercise graph |
| `mock` or `recorded` | Strict replay from the published `ors/recorded` directory or `Integrations:Ors:FixtureDirectory` |
| `live` | ORS Directions GeoJSON at `https://api.heigit.org/openrouteservice`. Configure `Integrations:Ors:ApiKey` and optionally `Integrations:Ors:BaseUrl` |

Use `Integrations__Ors__Mode=mock` with `scripts/offline_demo.py` to replay the
bundled examples. The [request index](../../data/demo/ors/index.json) covers Kłodzko
before and after bridge closure. The files are labeled `synthetic`. Their geometry
and duration come from the exercise graph.

To capture actual responses, configure `Integrations__Ors__ApiKey` in your local
environment or use the Routing project's .NET user-secrets. Run
`python scripts/record_ors_routes.py artifacts/ors-recorded`, then run
`python tests/dispatch_ors_integration.py --recordings artifacts/ors-recorded`
after building the solution. To use those recordings in the dashboard, point
`Integrations__Ors__FixtureDirectory` at that directory and use replay mode.
The hash includes the profile and complete normalized request.
Missing entries return `FixtureMissing`. The launcher sets the environment to
Production. Use the environment variable with the launcher and use .NET
user-secrets with a standalone Routing process in Development.

The current closure check is an exercise approximation based on the active
closed segments in Situation. An active closed segment with unknown geometry
blocks ORS routing.
PostGIS intersection with operational closure polygons remains part of task 02.

The API includes `approachGeometry`, `outboundGeometry` and `provenance` in
provider routes. The dashboard draws currently valid vehicle legs and labels
the route source. The unit position fixture labels EGNOS as synthetic. If the
reported quality is missing or the HPL exceeds `Integrations:Egnos:MaxHpl`
(25 metres by default), the dispatcher can pass `confirmedStartNode` with the
known node in an assign or replan command.

Local verification covers capturing responses from an ORS-compatible HTTP
test server and replaying those files for Kłodzko, missing recordings,
manual start position, and rejection of a route crossing the closed bridge.
The browser check covered the position label, both vehicle legs, withdrawal after
bridge closure, the detour and the state with no available exit.

Kłodzko uses a separate vehicle endpoint for `exit-north`, at the actual road
connection. The pedestrian waypoint keeps its original coordinates. The replay
captures are local under `artifacts/ors-verified`.

## Konfiguracja ORS na kolejnych komputerach

Na komputerze z .NET 10 SDK i Pythonem 3 demo Dispatch offline uruchom poleceniem
`python scripts/offline_demo.py --scenario sample`.
Aby odtworzyć gotowe nagrania zespołu,
rozpakuj `ors-verified.zip` do `artifacts/` na komputerze z tą samą wersją
kodu. Pliki JSON powinny znaleźć się w `artifacts/ors-verified`.
W PowerShellu uruchom:

```powershell
$env:Integrations__Ors__Mode = 'recorded'
$env:Integrations__Ors__FixtureDirectory = (Resolve-Path 'artifacts/ors-verified').Path
python scripts/offline_demo.py --scenario sample
```

W Bashu ustaw te same zmienne przez `export`. Wybierz `--scenario sample`
dla Kłodzka. Gotowe nagrania i lokalny graf działają bez klucza.

### Nagrywanie tras lokalnie

1. Zaloguj się do [HeiGIT Account](https://account.heigit.org/). W zakładce
   **API Key** skopiuj swój **Basic Key** przyciskiem **Copy**.
2. W PowerShellu, w katalogu głównym repozytorium, zapisz klucz dla projektu
   Routing. Wklej go dopiero po wyświetleniu pytania w terminalu.

   ```powershell
   $orsKey = Read-Host 'Wklej klucz ORS' -AsSecureString
   dotnet user-secrets set 'Integrations:Ors:ApiKey' ([System.Net.NetworkCredential]::new('', $orsKey).Password) --project src/Routing/Cassini.Routing.csproj
   Remove-Variable orsKey
   ```

   Na macOS i Linuksie można użyć zmiennej sesji w Bashu.

   ```bash
   read -rs -p "Wklej klucz ORS: " Integrations__Ors__ApiKey
   printf '\n'
   export Integrations__Ors__ApiKey
   ```

3. Nagraj i sprawdź trasy.

   ```sh
   python scripts/record_ors_routes.py artifacts/ors-recorded
   dotnet build Cassini.Offline.slnx -m:1 -p:NuGetAudit=false
   python tests/dispatch_ors_integration.py --recordings artifacts/ors-recorded
   ```

   Jeśli system ma polecenie `python3`, użyj go w miejsce `python`. Przy błędzie
   wygasłego certyfikatu zaktualizuj zaufane certyfikaty systemu lub wskaż aktualny
   plik CA przez `SSL_CERT_FILE`. Na komputerze z pakietem `certifi` można w
   PowerShellu ustawić `$env:SSL_CERT_FILE = (python -m certifi)`.

Używaj własnego klucza na komputerach, które nagrywają dane. Klucz pozostaje
w lokalnym profilu lub sesji terminala. Gotowe nagrania można odtwarzać offline
na pozostałych komputerach.

### GitHub Actions

Sekret repozytorium nosi nazwę `OPENROUTESERVICE_API_KEY`. Po przeniesieniu
workflow na domyślną gałąź wybierz **Actions → OpenRouteService live verification
→ Run workflow**. Klucz jest przekazywany tylko do kroku nagrywania. Test
rezerwacji i przeliczenia trasy używa zapisów. Zwykłe **Offline demo CI**
uruchamia się na pushach i PR-ach z lokalnym serwerem testowym.

## Obsługa zadań w panelu Dispatch

- Świeży stan demo ma dwie jednostki. Jednostka 4 ma symulowany HPL powyżej progu.
  Po wybraniu jej z listy dyspozytor musi potwierdzić punkt startu.
- Po zmianie rewizji Dispatch zestawia zapisane odcinki lub geometrię z zamknięciami
  i odcinkami zmienionymi od czasu przydziału. Trasa może pozostać aktualna,
  zostać wycofana albo wymagać ponownej weryfikacji. Po dotarciu do punktu
  odbioru sprawdzany jest pozostały odcinek wyjazdowy.
- **Historia zadania** pokazuje przydział, ręczne potwierdzenia, postęp i
  przeliczenia wraz z czasem, punktem i rewizją. Rozpoznane wycofanie zapisuje się
  w historii przy skutecznym przeliczeniu. Aktualny powód zamknięcia i jego
  czas są widoczne wcześniej w statusie. Historia rejestruje decyzję o ręcznym
  potwierdzeniu pozycji, a tożsamość operatora wymaga logowania.

## Opcjonalna baza Dispatch

Domyślna konfiguracja zapisuje stan w lokalnym JSON. W wariancie PostgreSQL
stan, historia i pokwitowania komend są zapisywane w tej samej transakcji co
aktywne rezerwacje. Baza wymusza unikalność jednostki i zgłoszenia w aktywnym
przydziale. Przy pierwszym uruchomieniu na pustej bazie Dispatch może
zainicjalizować dane z lokalnego `dispatch.json` wskazanego przez
`Demo__DataDirectory`. Kolejne uruchomienia czytają stan z bazy.

Z lokalną bazą ustaw `ConnectionStrings__Dispatch` w środowisku i uruchom:

```sh
dotnet build Cassini.Offline.slnx -m:1 -p:EnableDispatchPostgres=true -p:NuGetAudit=false
python scripts/offline_demo.py --no-build --scenario sample
```

W Compose ustaw `DISPATCH_DB_PASSWORD` i `DISPATCH_DB_CONNECTION` lokalnie.
Użyj hosta `dispatch-db`, bazy `dispatch`, użytkownika `dispatch` i tego samego
hasła w obu ustawieniach. Backend buduje i uruchamia profil dodatkowy:

```sh
docker compose -f compose.yaml -f compose.dispatch-postgres.yaml --profile full build
docker compose -f compose.yaml -f compose.dispatch-postgres.yaml --profile full up
```

Osobny job CI sprawdza równoległe rezerwacje z dwóch procesów Dispatch,
odtworzenie pokwitowania po restarcie i zwolnienie rezerwacji.
