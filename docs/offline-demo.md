# CASSINI Offline

Prezentacja odbywa się na [Walencji](../data/local/valencia.md) (powódź z 29 października 2024 r.). Scenariusz Warszawy i przełącznik między scenariuszami (`/ui/companion`) usunięto na develop. Scenariusz `sample` (Kłodzko) zostaje jako scenariusz testów integracyjnych, CI i katalogu schronień (`--sheltering`).

Panel koordynatora działa na jednym komputerze z czterema niezależnymi usługami ASP.NET Core .NET 10 oraz bramą HTTP. Android ma własny scenariusz ćwiczeniowy działający bez bramy. Mieszkańcy, jednostki, zgłoszenia i detekcje użyte w ćwiczeniu są syntetyczne. Sieć dróg OSM, obserwacja Copernicus EMS i obrazy Sentinel‑1 to dane archiwalne przygotowane lokalnie.

## Przygotowanie danych Walencji

Na komputerze z internetem przygotuj jednorazowo lokalny katalog `artifacts/valencia-city/` (ignorowany przez Git) według [instrukcji Walencji](../data/local/valencia.md):

```powershell
python -m pip install -r experiments/network/requirements.txt
python experiments/network/prepare_valencia.py
```

Skrypt potrzebuje pakietu wektorowego EMSR773 AOI 01 w `artifacts/valencia-evidence/emsr773-aoi01/`, pary GeoTIFF Sentinel‑1 z `data/demo/valencia/imagery/` (19 i 31 października 2024 r.) oraz wyciągu OSM z Geofabrik, pobieranego przy pierwszym uruchomieniu. Wynik to graf `city-network.json.gz`, mapa `city-map.svg`, strefy węzłów `city-nodes.svg`, warstwa `flood.geojson` i radar `sentinel-before.jpg` / `sentinel-event.jpg` z `city-imagery.json`.

## Start z Docker Compose

Na komputerze przygotowującym obrazy uruchom:

```sh
docker compose -p cassini-valencia -f compose.yaml -f compose.valencia.yaml build
docker compose -p cassini-valencia -f compose.yaml -f compose.valencia.yaml up -d --no-build --pull never
```

Otwórz [http://127.0.0.1:5080](http://127.0.0.1:5080). Port można zmienić przez `CASSINI_PORT`. `compose.valencia.yaml` montuje `artifacts/valencia-city/` tylko do odczytu w Situation, Routing i Gateway. Docker mapuje na hosta tylko Gateway. Cztery usługi działają w izolowanej sieci Compose. Stan każdej usługi jest zapisywany w jej własnym wolumenie. `docker compose down` zatrzymuje demo z zachowaniem stanu. `docker compose down -v` usuwa stan, więc następne `up` zaczyna ćwiczenie od zera.

Pierwsza budowa potrzebuje obrazów bazowych .NET z lokalnego cache lub z rejestru Microsoft. Gateway pobiera przy pierwszym `restore` pakiet NuGet `Microsoft.AspNetCore.Authentication.JwtBearer`. Później budowa korzysta z cache.

Aby przenieść demo na inny komputer, zapisz pięć obrazów:

```sh
docker image save -o cassini-offline-images.tar cassini-offline/gateway:demo cassini-offline/management:demo cassini-offline/situation:demo cassini-offline/routing:demo cassini-offline/dispatch:demo
```

Przenieś archiwum, `compose.yaml`, `compose.valencia.yaml` i katalog `artifacts/valencia-city/` na komputer o zgodnej architekturze z Dockerem. Następnie uruchom:

```sh
docker image load -i cassini-offline-images.tar
docker compose -p cassini-valencia -f compose.yaml -f compose.valencia.yaml up -d --no-build --pull never
```

[Docker Compose networking](https://docs.docker.com/compose/how-tos/networking/).

Bez Dockera ten sam scenariusz uruchamia `python scripts/offline_demo.py` (domyślnie Walencja, port 5080). Opcja `--data <katalog>` wskazuje nowy katalog stanu. Potrzebny jest .NET 10 SDK i Python 3.

## Przebieg prezentacji (Walencja)

Przed prezentacją zacznij od świeżego stanu: `docker compose ... down -v`, a potem `up -d`, albo `offline_demo.py` z nowym katalogiem `--data`. Usługi nie mają komendy resetu. Przeładuj stronę po resecie, bo identyfikatory komend prezentacji są przechowywane tylko w pamięci karty.

Wybierz **Tryb prezentacji** w panelu. Przycisk **Dalej · wykonaj krok** wykonuje bieżący krok, a **Dalej** przechodzi do następnego. Każdy krok odczytuje świeżą rewizję sytuacji przed komendą. **Ponów „…” (te same commandId)** wysyła ponownie te same komendy i nie tworzy duplikatów. Przy konflikcie rewizji (`RevisionConflict`, `SituationChanged`) panel odświeża widok i pokazuje **Ponów krok**. Można też wybrać dowolny krok z listy.

| # | Krok | Stan | Co widać |
| --- | --- | --- | --- |
| 0 | Świeży stan ćwiczenia | działa | Rewizja sytuacji 0, brak zamknięć, albo ostrzeżenie o stanie po wcześniejszych operacjach. |
| 1 | Obraz sytuacji: EMSR773, Sentinel‑1, strefy | działa | Warstwa EMSR773_AOI01_DEL_PRODUCT_v1 (obserwacja 31.10.2024 18:02 UTC), radar Sentinel‑1 po zdarzeniu, strefy węzłów: 21 833 w strefie zagrożenia, 155 763 poza zalaniem. |
| 2 | Zgłoszenie powodzi od mieszkańca | zablokowany | Management nie obsługuje Walencji (właściciel: Management). |
| 3 | Trasa piesza w mieście (OSM) | działa | `city-route` między dwoma punktami. W próbie: 18 min 56 s, 50 odcinków, rewizja 0. |
| 4 | Rozkład tras w węźle i losowanie (model task 03) | działa, schrony **SYMULACJA** | `evacuation-plan`, `distribution?node=10242238645&profile=independent` i `draw` z `seed: 1` (model `logit-sue`). Rozkład w rewizji 0: Punkt C 37 %, F 12 %, G 51 %; fala 3, termin 43 min (założenie modelu). Trasę z węzłów OSM (`city-node/{id}`) rysuje mapa. W kroku 8 panel podaje stan losowania po zamknięciach (`draws/{commandId}`). Schrony są syntetyczne, ludność to szacunek GHSL. |
| 5 | Prośba o transport → jednostka | zablokowany | Dispatch nie obsługuje Walencji (task 4). |
| 6 | Detekcja FlyPix: zalane przejście | działa, **SYMULACJA** | Import syntetycznej detekcji przez `import-flypix` (`mode: synthetic`). Situation dopasowuje ją do 6 odcinków OSM, z czego 4 leżą na trasie z kroku 3. Detekcja czeka na decyzję. |
| 7 | Zatwierdzenie → zamknięcie odcinków | działa | `review-decision` z `approve`. 6 zamkniętych odcinków, rewizja 2. |
| 8 | Przeliczenie trasy → objazd | działa | Objazd 19 min 02 s zamiast 18 min 56 s, 0 zamkniętych odcinków na nowej trasie. |
| 9 | Odcięcie punktu startowego → brak trasy | działa, **SYMULACJA** | Druga syntetyczna detekcja (3461 odcinków) po zatwierdzeniu odcina start. Panel pokazuje **Brak trasy** jako osobny stan, rewizja 4. |
| 10 | Utrata łącza → IRIS² (symulacja) | działa, **symulacja łącza** | `link-profile` `down`, potem `iris2`. W Walencji krok tylko przełącza profil i pokazuje stan z `link-status` (profil, RTT, kolejka). Nie wysyła komendy z terenu, bo Management i Dispatch nie mają spraw Walencji. |
| 11 | Komunikat EWSS | działa, **SYMULACJA** nadania | `management/ewss-alert` (`level: severe`, tekst ≤ 100 znaków, strefa = prostokąt granic warstwy EMSR773, w próbie `-0.62, 39.24, -0.2622, 39.5929`). Wynik z `ewss-alerts`: `delivery: simulated`, `provenance.mode: mock`. Panel pisze, że brak potwierdzenia odbioru przez telefony. |
| 12 | Potwierdzenie wyjścia → licznik | zablokowany | Management nie obsługuje spraw mieszkańców Walencji; telefon z Galileo: task 5. |

Krok zablokowany pokazuje powód i właściciela, ale nie wykonuje żadnej komendy.

Krok łącza korzysta z symulatora w Gateway (task 6, kontrakt [contracts/gateway/README.md](../contracts/gateway/README.md)). Wysyła `POST /api/link/commands/link-profile {commandId, profile: "down"}`, potem `profile: "iris2"`. W scenariuszu `sample` pomiędzy nimi wysyła zgłoszenie `management/alert` z nagłówkiem `X-Cassini-Link: field`: Gateway odpowiada `202 Queued` (to nie jest potwierdzenie dostawy), ponowienie z tym samym `commandId` zwraca ten sam wpis kolejki, a po przełączeniu panel czeka, aż `link-status` pokaże wpis jako `Delivered` przez `iris2`. Panel porównuje liczbę zgłoszeń w Management przed i po (1 → 2, jedna dostawa). Bez wpisu `Delivered` panel pisze, że brak potwierdzenia oznacza brak dostawy. Profile i ich opóźnienia są założeniami demo, nie specyfikacją IRIS² ani GOVSATCOM.

Krok EWSS wysyła `POST /api/management/commands/ewss-alert` ([przykład](../contracts/management/ewss-alert-command.example.json)). Galileo EWSS nie ma publicznego interfejsu nadawczego, więc Management zapisuje komunikat przez mock (`src/Management/Ewss.cs`). W `sample` strefa to prostokąt granic warstwy zalania ćwiczenia.

W scenariuszu `sample` (CI) wszystkie kroki są wykonywane: zgłoszenie, losowanie, prośba o transport, zamknięcie mostu, przeliczenie, postęp jednostki, łącze, EWSS, potwierdzenie wyjścia i brak wyjścia. Krok wyjścia wysyła `exit-confirmed` z pozycją węzła wyjścia oznaczoną jako symulacja (`mode: simulated`, `galileoUsed: false`, `osnma: none`, `egnos: unknown`), bo Management wymaga dowodu pozycji. Prawdziwą pozycję z Galileo dostarcza telefon (task 5).

Syntetyczne detekcje i punkty trasy pochodzą z [valencia-review.synthetic.json](../apps/web/data/presentation/valencia-review.synthetic.json). Plik ma etykietę **SYMULACJA**, `imageProductId: presentation-synthetic-fixture` i `observedAt: 2024-10-31T18:03:00Z`. Nie jest obserwacją FlyPix. Prawdziwą detekcję z FlyPix importuje się osobno według [instrukcji Walencji](../data/local/valencia.md#flypix-w-przeglądarce).

Poza trybem prezentacji te same operacje są dostępne ręcznie. **Wskaż punkty trasy** służy do testowej trasy pieszej. **Weryfikacja detekcji** zatwierdza lub odrzuca detekcje. **Radar Sentinel‑1** przełącza obraz z 19 i 31 października, a **Pokaż strefy węzłów** pokazuje klasyfikację węzłów.

### Integracje w trybie próbnym

Rozwiń **Integracje · symulacja offline** w panelu Kłodzka lub Walencji. Każdy przycisk odczytuje lokalny przykład oznaczony `mode: mock`. Wyniki zawierają podgląd geometrii i odpowiedź JSON, a status zaczyna się od **Dane syntetyczne**. Odczyt zachowuje bieżącą rewizję sytuacji, trasy i zamknięcia dróg.

| Źródło | Zapytanie przez Gateway | Co pokazuje |
| --- | --- | --- |
| Copernicus EMS | `GET /api/situation/queries/integration-copernicus` | Kod aktywacji, numer AOI, produkt typu delineation, czas obserwacji i przykładowy poligon zalania. |
| Sentinel‑1 | `GET /api/situation/queries/integration-sentinel-1` | Syntetyczną maskę wody i przykładowy szacunek 42 osób. |
| Copernicus DEM | `GET /api/situation/queries/integration-copernicus-dem` | Syntetyczny obszar potencjalnego zagrożenia związanego z niskim położeniem. |
| FlyPix AI | `GET /api/situation/queries/integration-flypix` | Przykładowe identyfikatory rastra i modelu, czas obserwacji oraz poligon detekcji z syntetyczną pewnością modelu. |
| Miasto · `GetData` | `GET /api/situation/queries/city-data` | Syntetyczną liczbę 120 osób w jednej strefie. |
| Miasto · `GetMap` | `GET /api/situation/queries/city-map` | Granicę tej strefy w GeoJSON WGS 84. Identyfikatory zestawu i strefy łączą oba odczyty. |

Mapa Walencji korzysta z lokalnych plików archiwalnych EMSR773 i Sentinel‑1. W osobno uruchomionym ćwiczeniu `python experiments/network/import_valencia_review.py --mock` pobiera odpowiedź FlyPix z lokalnego API i dodaje syntetyczną detekcję do kolejki. Zamknięcie wymaga decyzji operatora. Ten sam skrypt przyjmuje manifest z eksportem GeoJSON z przeglądarki. [Instrukcja Walencji](../data/local/valencia.md#flypix-w-przeglądarce)

`ICitySource` jest punktem podłączenia przyszłego źródła, np. systemu HR albo rejestru ludności. Szczegóły pobrania i agregacji zależą od systemu wybranego przez operatora. Kontrakt demonstracyjny używa sum według stref i granic tych stref.

### Katalog punktów zbiórki i schronień (`sample`)

Do ćwiczenia `sample` można dołączyć katalog SH-T01:

```sh
python scripts/offline_demo.py --sheltering --port 5480
```

W [panelu lokalnym](http://127.0.0.1:5480) rozwiń **Punkty zbiórki i schronienia**. Panel pojawia się tylko, gdy `GET /features` zwraca `{"sheltering": true}`, i nie występuje w Walencji. Scenariusz pokazuje punkt północny i dwa schronienia z syntetycznymi limitami, rezerwacjami i zajętością. Właściciele katalogów obsługują zmiany przez [komendy HTTP](sheltering-interfaces.md#sh-t01-site-catalog-and-destination-reads). Zamknięte schronienie zostaje w panelu, a Routing pomija je w liście kandydatów. Ta lista nie potwierdza przejezdności trasy.

Przygotowanie obrazów i uruchomienie alternatywne (nakładka ma własny healthcheck, więc działa z `--wait`):

```sh
docker compose -p cassini-sheltering -f compose.yaml -f compose.sheltering.yaml build
docker compose -p cassini-sheltering -f compose.yaml -f compose.sheltering.yaml up -d --no-build --pull never --wait
```

Ustaw `CASSINI_PORT` na wolny port, gdy zwykłe demo już działa na 5080. Test katalogów uruchom przez `python scripts/offline_demo.py --sheltering --test --port 5480`.

#### Panel „Schronienia i przydziały” (SH-T07)

Przy `--sheltering` pod katalogiem pojawia się karta **Schronienia i przydziały**. Pokazuje:

- obiekty i ich dostępność;
- przyjęcia w każdym schronieniu (oczekujący, spóźnieni, obecni, przyjęcia i wyjścia);
- zatwierdzone plany grup podróżnych;
- tabelę przydziałów.

Każda kolumna tabeli pochodzi od innego właściciela:

| Kolumna | Właściciel | Uwagi |
| --- | --- | --- |
| Wyjście ze strefy | Management | |
| Check-in w punkcie | brak | Żadna usługa nie prowadzi go jeszcze. Panel pisze „brak właściciela danych”. |
| Rezerwacja | Sheltering | |
| Opublikowana trasa | SH-T06 | Jeszcze nie podłączona. Panel pisze „nieopublikowana · SH-T06”. |
| Przyjęcie | Sheltering | |

Wycofany przydział zawsze ma etykietę „wycofana”. Gdy odczyt się nie powiedzie albo Situation ma nowszą rewizję niż ta, przy której sprawdzono schronienia, panel podaje czas i rewizję ostatniego spójnego odczytu. Rezerwacje oznacza wtedy jako „ostatnio … (niepotwierdzone)” i ukrywa przyciski akcji.

Konta operatorów działają tylko wtedy, gdy Sheltering ma ustawione `Sheltering__OperatorsFile`. Wtedy przyjęcia odczytuje i zgłasza tylko posiadacz tokenu z tego pliku (granica ról SH-T05 Stage B). Token wpisuje się w polu **Token operatora ćwiczenia**. Panel trzyma go tylko w pamięci karty i wysyła wyłącznie w swoich zapytaniach.

- Koordynator widzi wszystkie schronienia i nie może zgłaszać przyjęć.
- Operator schronienia widzi i obsługuje tylko swoje schronienie.

Ćwiczeniowe tokeny generuje `scripts/offline_demo.py` w katalogu stanu (`operator-tokens.json`). Nie trafiają do Git. Z Keycloakiem (profil `full`) panel używa tokenu logowania. Konta Sheltering i Keycloak nie są ze sobą łączone.

Próba generalna i testy:

```sh
python scripts/offline_demo.py --scenario sample --test    # regresja bazowa
python tests/sheltering_rehearsal.py                        # macierz SH-A01–SH-A08 na świeżym stanie
python tests/sheltering_rehearsal.py --browser              # + ścieżka w przeglądarce (Playwright)
docker compose -f compose.yaml -f compose.sheltering.yaml build
python tests/sheltering_rehearsal.py --images-only          # SH-A08 na gotowych obrazach, bez pobierania i budowania
python -m pip install playwright==1.49.1 && python -m playwright install chromium
python tests/sheltering_browser.py --screenshots artifacts/sheltering-browser
```

`sheltering_rehearsal.py` buduje usługi raz, a każdy zestaw testów uruchamia na nowym katalogu stanu. Wynik każdego punktu SH-A zapisuje w `artifacts/sheltering-rehearsal.json`. Opcja `--images-only` działa tylko na gotowych obrazach, zawsze z `--no-build --pull never` i na pustych wolumenach:

1. Uruchamia regresję `sample` na `compose.yaml`.
2. Uruchamia ćwiczenie schronień na `compose.yaml` + `compose.sheltering.yaml`.
3. Restartuje Situation i Sheltering, a potem sprawdza zachowany stan.
4. Sprawdza, że kontener Sheltering w sieci `internal` nie łączy się z internetem.

W CI robi to job `verify-sheltering-images`.

### Ranking jednostek (`sample`)

W karcie **Pomoc terenowa** przycisk **Porównaj czasy dojazdu** odczytuje `GET /api/dispatch/queries/unit-candidates?requestId=…`. Lista jednostek jest wtedy posortowana według rankingu Dispatch i pokazuje czas dojazdu albo powód braku kandydatury. Ranking jest ważny tylko dla rewizji sytuacji i wersji prośby, z których powstał; po zmianie rewizji panel go odrzuca.

## Mapa, warstwy i legenda

Mapa używa Leaflet 1.9.4 serwowanego lokalnie z `apps/web/vendor/leaflet/`. Nie ma kafelków ani zewnętrznych żądań. W Walencji widok jest ograniczony do wspólnego obszaru zdjęć Sentinel‑1 (okno taska 03, `city-imagery.json` → `scenes.event.bounds`): nie da się oddalić poza ten obszar, a nagłówek mapy pokazuje **Walencja · obszar Sentinel-1**. Z warstw zagrożeń Walencja pokazuje tylko obserwację Copernicus EMS (`copernicus-ems-archive`). Tło tworzą lokalne pliki: mapa dróg OSM, radar Sentinel‑1 i strefy węzłów. Przyciski **+**, **−** i **Resetuj widok** sterują widokiem. Kółko myszy i przeciąganie też działają.

Legenda pod mapą jest generowana z rejestru warstw i podzielona na grupy **Sytuacja**, **Sieć i trasy** oraz **Tło mapy**. Każda pozycja pokazuje źródło, tryb, czas obserwacji i identyfikator produktu, np. `Copernicus EMS (archiwum) · zapis · 31 paź 2024, 18:02 UTC · EMSR773_AOI01_DEL_PRODUCT_v1`. Odznaki pochodzenia:

- **SYMULACJA** i **Warstwa ćwiczeniowa** dla danych `mock` i `synthetic`;
- **Dane operatora** dla danych `recorded`, `live` i `manual` zaimportowanych przez operatora;
- **Plik lokalny** dla podkładu przygotowanego offline;
- **Dane nieaktualne**, gdy ostatni odczyt właściciela się nie powiódł. Warstwa pokazuje wtedy ostatni znany stan, a nie potwierdzoną dostępność.

Style rozróżniają zalanie z obserwacji (EMS, Sentinel‑1), detekcję do weryfikacji (FlyPix), zamknięty odcinek, trasę bieżącą, trasę wycofaną i brak trasy. W Walencji stan trasy testowej to **Trasa testowa OSM dostępna w rewizji N** albo **Brak trasy w rewizji N**, a wylosowana trasa ewakuacji (krok 4) ma własny wpis w legendzie i stan **Trasa aktywna** albo **Trasa wycofana po zmianie dostępności**. W scenariuszu `sample` dochodzą wylosowana trasa, trasa wycofana, trasa niepotwierdzona, trasy jednostek i pozycje jednostek z dowodem pozycji. EGNOS jest oznaczany tylko wtedy, gdy dowód pozycji potwierdza jego zastosowanie.

## Panel metryk

Panel **Metryki ćwiczenia** podaje przy każdej wartości jej źródło:

- **z danych serwisu**: znaczniki czasu zwrócone przez usługi;
- **pomiar sesji**: chwile zaobserwowane w tej karcie przeglądarki od jej otwarcia albo od **Wyzeruj pomiar sesji**;
- **dane serwisu → pomiar sesji**: od znacznika usługi do chwili wyświetlenia.

Brakujące dane nie są zastępowane szacunkiem. W Walencji cztery metryki z one-pagera (zgłoszenie → widok sytuacji, czas przydziału, zamknięcie → wycofanie, osoby bez potwierdzonego wyjścia) mają stan **niedostępne w tym scenariuszu**, bo Management i Dispatch nie obsługują Walencji. Losowanie trasy ewakuacji w Walencji istnieje (task 03), ale metryka 3 dotyczy tras jednostek i wycofania w scenariuszu `sample`. W `sample` metryka 4 bierze liczniki Management (`counters`, **z danych serwisu**): wartość to `unconfirmed` z `confirmedOutside + unconfirmed`. Poza strefą liczą się tylko jawne potwierdzenia wyjścia z pozycją (INV-12). Udział pozycji symulowanych (`confirmedOutsideSimulated`) ma odznakę **SYMULACJA**, a szacunek GHSL bez potwierdzonego wyjścia jest opisany jako szacunek. Mapa pokazuje tylko liczbę osób na węzeł (INV-14). Lista spraw i legenda pokazują tryb pozycji wyjścia. Sekcja **Walencja · pomiary z danych rzeczywistych** pokazuje:

| Metryka | Źródło | Uwagi |
| --- | --- | --- |
| Obserwacja EMS → decyzja | serwis | Od `observedAt` EMSR773 do `decidedAt` zatwierdzenia. Archiwum jest odtwarzane, więc odstęp obejmuje czas od archiwum. |
| Zobrazowanie → decyzja | serwis | Od `observedAt` detekcji do `decidedAt`. |
| Decyzja → zamknięcie w panelu | serwis → sesja | Od `decidedAt` do pojawienia się odcinków na mapie. |
| Zamknięcie → przeliczenie trasy | sesja | Od zatwierdzenia do przeliczenia trasy testowej. |
| Węzły w strefie zagrożenia | serwis | `zoneCounts` z Routing. |
| Szacunek ludności w strefie | — | Warstwa EMSR773 nie zawiera szacunku ludności. |

## Logowanie Keycloak (profil `full`)

Domyślny stack działa bez logowania. `/auth-config.json` zwraca wtedy `enabled: false`, a panel nie pokazuje paska logowania. Aby włączyć logowanie:

1. Skopiuj `.env.example` do `.env` (plik ignorowany przez Git). Wpisz własne hasła w `KC_BOOTSTRAP_ADMIN_PASSWORD` i `CASSINI_DEMO_PASSWORD`. Odkomentuj `COMPOSE_PROFILES=full` i `CASSINI_AUTH_ENABLED=true`.
2. Uruchom `docker compose up -d` (dla Walencji z `-f compose.yaml -f compose.valencia.yaml`). Profil uruchamia `quay.io/keycloak/keycloak:26.8.2` z realmem `cassini` z [deploy/keycloak/realm.json](../deploy/keycloak/realm.json). Obraz trzeba raz pobrać.
3. Panel przekierowuje do logowania Keycloak (`http://localhost:8180`, kod z PKCE S256) i wysyła token do Gateway. Gateway sprawdza token (`JwtBearer`, audience `cassini-gateway`) i role z `realm_access.roles`. Usługi za Gateway nie sprawdzają tokenu.

Realm zawiera czterech syntetycznych użytkowników ze wspólnym hasłem `CASSINI_DEMO_PASSWORD`:

| Użytkownik | Rola | Komendy dozwolone przez Gateway | Widok w panelu |
| --- | --- | --- | --- |
| `demo-coordinator` | `coordinator` | wszystkie | pełny |
| `demo-dispatcher` | `dispatcher` | `dispatch/commands/*` | bez sterowania ćwiczeniem, zgłoszenia, losowania, decyzji o detekcjach, trybu prezentacji i postępu mieszkańca |
| `demo-responder` | `responder` | `dispatch/commands/progress`, `management/commands/progress` | postęp zadania jednostki i mieszkańca, bez prośby, przydziału i przeliczenia |
| `demo-resident` | `resident` | `routing/commands/draw`; odczyt tylko `situation` i `routing` | obraz sytuacji, trasy i losowanie w węźle. Nie widzi spraw innych mieszkańców ani zadań jednostek i nie potwierdza wyjścia, dopóki token nie zawiera `caseId` wiążącego użytkownika z jego sprawą |

Symulator łącza i EWSS podlegają tym samym regułom: `link/commands/link-profile` i `management/commands/ewss-alert` może wysłać tylko koordynator, `link/queries/link-status` czytają role sztabowe (nie mieszkaniec). Komenda przekazana z terenu (`X-Cassini-Link: field`) ma zwykłą ścieżkę `/api/{usługa}/commands/{operacja}`, więc Gateway sprawdza ją według tej samej tabeli przed kolejką.

Każda rola może czytać `GET /api/*/queries/*`. Bez tokenu Gateway zwraca 401, a bez uprawnienia 403. Użytkownik bez znanej roli dostaje widok tylko do odczytu. Ukrywanie przycisków w panelu ma charakter pomocniczy, bo uprawnienia egzekwuje Gateway. Gdy Keycloak jest niedostępny, panel pokazuje **Brak połączenia z usługą logowania** i nie wysyła komend. Powiązanie mieszkańca z jego sprawą wymaga atrybutu `caseId` w tokenie, którego realm jeszcze nie wydaje.

### Profil `full`: Keycloak i Management na PostgreSQL z RabbitMQ

Profil `full` łączy dwie niezależne części. `compose.yaml` dodaje Keycloak, a nakładka `compose.management-full.yaml` (task 6) przełącza Management na PostgreSQL (`management_db`) i szynę RabbitMQ oraz dodaje usługi `rabbitmq` i `management-db`. Nakładka wymaga `--profile full` oraz zmiennych `RABBITMQ_PASSWORD` i `MANAGEMENT_DB_PASSWORD`.

```sh
# tylko logowanie (zmienne z .env: COMPOSE_PROFILES=full, CASSINI_AUTH_ENABLED=true, hasła Keycloak)
docker compose up -d
# logowanie + Management na PostgreSQL i RabbitMQ (dodatkowo RABBITMQ_PASSWORD i MANAGEMENT_DB_PASSWORD w .env)
docker compose -f compose.yaml -f compose.management-full.yaml --profile full up -d --build
# Management na PostgreSQL i RabbitMQ bez Keycloak: wskaż usługi, Keycloak nie jest zależnością Gateway
docker compose -f compose.yaml -f compose.management-full.yaml --profile full up -d --build gateway
```

Bez `CASSINI_AUTH_ENABLED=true` Gateway nie wymaga logowania, nawet gdy kontener Keycloak działa.

## Usługi i CQRS

Gateway dodaje prefiks `/api/{management|situation|routing|dispatch}` oraz `/api/sheltering` z nakładką `compose.sheltering.yaml`. Zapytania to `GET /queries/…`, komendy to `POST /commands/…`.

| Serwis | Zapytania | Komendy |
| --- | --- | --- |
| Management | `incident` (z licznikami `counters`), `cases/{caseId}`, `ewss-alerts`; z `Grouping__EvidenceFile`: `evacuation-groups/{id}`, `travel-party-proposals/{id}`, `approved-travel-plans/{id}` | `alert`, `progress`, `ewss-alert`; z grupowaniem: `register-evacuation-group`, `update-group-composition`, `record-care-handover`, `approve-travel-plan`, `flag-travel-plan-for-review` |
| Situation | `snapshot`, `closed-roads`, `integration-copernicus`, `integration-flypix`, `city-data`, `city-map`; z `--sheltering`: `assembly-points` | `closure`, `import-copernicus`, `import-flypix`, `review-decision`; z `--sheltering`: `set-assembly-point` |
| Routing | `network`, `city-route?from&to&mode`, `city-node/{id}`, `city-edge/{id}`, `validate-route?segments&at`, `distribution`, `draws/{commandId}`; tylko `sample`: `assistance-route?from&pickup`, `assistance-restrictions?since`, z `--sheltering`: `destinations`; tylko Walencja: `evacuation-plan` | `draw` (w Walencji model task 03: `node` = ID węzła OSM, `mode` = profil) |
| Dispatch | `assignments`, `unit-candidates?requestId` | `request`, `assign`, `replan`, `progress` |
| Sheltering (opcjonalnie) | `shelter-availability?at&eligibleOnly` | `configure-shelter` |

Management i Dispatch nie rozróżniają scenariusza i zawsze mają dane ćwiczenia `sample`. W Walencji panel ich nie odczytuje. Gateway serwuje też `GET /auth-config.json` (konfiguracja logowania), `GET /health`, `GET /features` (`{sheltering}`) oraz lokalne pliki mapy `/maps/*` z katalogu miasta. Symulator łącza to `GET /api/link/queries/link-status` i `POST /api/link/commands/link-profile` w Gateway. Komenda z nagłówkiem `X-Cassini-Link: field` idzie przez symulowane łącze i kolejkę ([kontrakt](../contracts/gateway/README.md)), a bez nagłówka trafia bezpośrednio do właściciela.

Handlery zapytań i komend są oddzielne. Usługi przekazują sobie dane w JSON po lokalnym HTTP i nie zapisują danych w plikach innej usługi. Komendy obsługują ponowienia z tym samym `commandId` i sprawdzają wersję lub rewizję, jeśli zmieniają istniejący stan. Każda usługa zapisuje stan atomowo do własnego pliku JSON i zakłada jedną uruchomioną instancję.

Routing w scenariuszu `sample` wyznacza najkrótszą dopuszczalną trasę do każdego dostępnego wyjścia. Przy obliczaniu rozkładu bierze odwrotność sumy oczekiwania do początku okna i czasu przejścia, a potem normalizuje wagi do sumy 1. Trasy przez zamknięcia, bez istotnej cechy dostępności lub poza oknem czasowym otrzymują zero szans. `POST /commands/draw` przyjmuje `commandId`, `node`, `revision`, opcjonalnie `at`, `subjectId` i testowy `seed`. W Walencji Routing liczy trasę miejską po grafie OSM z pominięciem zamkniętych odcinków bieżącej rewizji, a rozkład i losowanie pochodzą z modelu ewakuacji taska 03 (`logit-sue`), przeliczanego w tle po każdej rewizji (do tego czasu `503 PlanCalculating`). Panel odświeża zapytania co 7 sekund i od razu po własnych komendach.

## Źródła satelitarne i zakres offline

- **Copernicus EMS.** Walencja używa produktu [EMSR773](https://mapping.emergency.copernicus.eu/activations/EMSR773/) AOI 01, delineacja v1, z obserwacjami 30–31 października 2024 r. (najpóźniejsza 31.10.2024 18:02 UTC). Warstwa ma w legendzie identyfikator produktu i czas obserwacji.
- **Sentinel‑1.** Para obrazów VV z 19 października (przed zdarzeniem) i 31 października 2024 r. (po zdarzeniu) obejmuje wspólny fragment aglomeracji. Contains modified Copernicus Sentinel data 2024.
- **OpenStreetMap.** Graf i mapa dróg pochodzą z wyciągu OSM (© OpenStreetMap contributors, ODbL). Data wyciągu jest widoczna w legendzie.
- **Galileo** jest odczytywany na kompatybilnym odbiorniku Androida. Aplikacja przyjmuje świeżą pozycję GNSS do zgłoszenia wyjścia. Udział Galileo zapisuje, gdy potwierdza go status odbiornika z czasu pomiaru. [Galileo Open Service](https://www.euspa.europa.eu/galileo-os), [Android GnssStatus](https://developer.android.com/reference/android/location/GnssStatus).
- **EGNOS** wymaga sprzętu, który potwierdza faktyczne zastosowanie korekt. **GOVSATCOM** i **IRIS²** przewidziano jako możliwe łącza dla wyposażonego punktu koordynacji. Demo nie wymaga terminala ani aktywnego połączenia. [EGNOS](https://www.euspa.europa.eu/eu-space-programme/egnos/services), [GOVSATCOM](https://www.euspa.europa.eu/govsatcom-hub), [IRIS²](https://defence-industry-space.ec.europa.eu/eu-space/iris2-secure-connectivity_en).

Bez sieci między urządzeniami Android używa własnych danych ćwiczeniowych. Zmiany w panelu i w telefonie nie są wtedy rozsyłane na drugie urządzenie. Dostęp do panelu poza komputerem koordynacji, rzeczywista dystrybucja poleceń po satelicie i operacyjne dane terenowe są poza zakresem tego demo. Przed decyzjami w terenie potrzebna jest walidacja danych i przepustowości na rzeczywistej sieci.

## Weryfikacja lokalna

```sh
python scripts/offline_demo.py --test
python scripts/offline_demo.py --test --scenario valencia
python scripts/offline_demo.py --sheltering --test
python tests/grouping_integration.py
dotnet run --project src/Routing/tests/Cassini.Routing.Tests.csproj -p:NuGetAudit=false
dotnet run --project src/Gateway/tests/Gateway.Tests.csproj -p:NuGetAudit=false
dotnet run --project src/Management/tests/Cassini.Management.Tests.csproj -p:NuGetAudit=false
dotnet run --project src/Shared/Messaging/tests/Cassini.Messaging.Tests.csproj -p:NuGetAudit=false
dotnet run --project apps/offline-android/tests/Offline.Tests.csproj -p:NuGetAudit=false
docker compose config --quiet
docker compose -f compose.yaml -f compose.valencia.yaml config --quiet
docker compose -f compose.yaml -f compose.sheltering.yaml config --quiet
docker compose --profile full config --quiet
RABBITMQ_PASSWORD=x MANAGEMENT_DB_PASSWORD=x docker compose -f compose.yaml -f compose.management-full.yaml --profile full config --quiet
```

`--test` w scenariuszu `sample` uruchamia też `tests/connectivity_integration.py` (łącze, kolejka, liczniki, EWSS). W konsoli Windows z kodowaniem cp1250 ustaw `PYTHONIOENCODING=utf-8`, bo końcowy komunikat tego testu zawiera znak `²`.

`--test` uruchamia scenariusz `sample` (CI). `--test --scenario valencia` wymaga przygotowanego `artifacts/valencia-city/`. Test uruchamia pięć procesów na kolejnych portach i używa tymczasowego stanu. `--port 5180` pozwala testować obok działającego Compose. Kod testów można też uruchomić na świeżym środowisku Compose, np. `python tests/offline_integration.py http://127.0.0.1:5080`. Test zmienia stan scenariusza, dlatego powinien działać na osobnym środowisku.

### Test end-to-end (Playwright)

Test przeglądarkowy jest w `tests/e2e/`. Wymaga działającego stacka i jednorazowego pobrania zależności npm i przeglądarki:

```sh
docker compose up -d --wait
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test
```

`E2E_BASE_URL` wskazuje panel (domyślnie `http://127.0.0.1:5080`). Test blokuje żądania poza `localhost` i kończy się błędem przy błędach konsoli, błędach strony, odpowiedziach 4xx/5xx lub naruszeniach CSP, bez wyjątków. Oba testy oczekują wykonanych kroków łącza i EWSS. W `sample` zgłoszenie z terenu czeka w kolejce i jest dostarczone raz przez `iris2` (drugie zgłoszenie w Management), a ponowienie kroków `link` i `ewss` nie zmienia stanu. W Walencji krok łącza nie tworzy wpisów kolejki, a strefa EWSS leży w granicach EMSR773. Test Walencji najpierw czeka, aż `evacuation-plan` zwróci 200.

Specyfikacje w `tests/e2e/specs/`:

- `demo.spec.js` — scenariusz `sample` (CI). Przechodzi cały tryb prezentacji, sprawdza trzy stany (trasa dostępna, trasa wycofana po zamknięciu mostu, brak trasy), legendę z provenance, łącze z kolejką, EWSS, wyjście z pozycją SYMULACJA, panel metryk z licznikami serwisu i brak duplikatów przy powtórzeniu kroku.
- `valencia.spec.js` — uruchamia się tylko z `E2E_SCENARIO=valencia`, np. na stacku z `python scripts/offline_demo.py --scenario valencia --port 5880`, a potem `E2E_SCENARIO=valencia E2E_BASE_URL=http://127.0.0.1:5880 npx playwright test`.

Test wymaga świeżego stanu (rewizja 0): przed ponownym uruchomieniem wykonaj `docker compose down -v`. Raport: `npx playwright show-report playwright-report`. W CI działa job `e2e` w `.github/workflows/offline-ci.yml`.

Wersję Android 11+ buduje się niezależnie od Dockera według [instrukcji aplikacji](../apps/offline-android/README.md). Kompilacja i test rozkładu nie zastępują próby Galileo w trybie samolotowym na fizycznym urządzeniu.
