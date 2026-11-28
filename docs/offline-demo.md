# CASSINI Offline

Panel koordynatora i węzła ewakuacji działa na jednym komputerze z czterema niezależnymi usługami ASP.NET Core .NET 10 oraz bramą HTTP. Android ma własny scenariusz ćwiczeniowy działający bez bramy. Wszystkie lokalizacje, mieszkańcy, jednostki i czasy marszu użyte przez scenariusz są syntetyczne.

## Start z Docker Compose

Na komputerze przygotowującym obrazy uruchom:

```sh
docker compose build
docker compose up -d --no-build --pull never
```

Otwórz [http://127.0.0.1:5080](http://127.0.0.1:5080). Domyślny port można zmienić przez `CASSINI_PORT`. Docker mapuje na hosta tylko Gateway. Cztery usługi działają w izolowanej sieci Compose. Stan każdej usługi jest zapisywany w jej własnym wolumenie. Polecenie `docker compose down` zatrzymuje demo z zachowaniem stanu. Świeże ćwiczenie z odrębnym stanem można uruchomić z inną nazwą projektu Compose i wolnym portem.

Na komputerze przygotowującym paczkę zapisz pięć obrazów:

```sh
docker image save -o cassini-offline-images.tar cassini-offline/gateway:demo cassini-offline/management:demo cassini-offline/situation:demo cassini-offline/routing:demo cassini-offline/dispatch:demo
```

Przenieś archiwum i `compose.yaml` na zgodną architekturę z uruchomionym Dockerem. Po skopiowaniu wykonaj poniższe polecenia. Obrazy mogą też zostać załadowane z paczki dostarczonej z demo.

```sh
docker image load -i cassini-offline-images.tar
docker compose up -d --no-build --pull never
```

Do pierwszej budowy potrzeba obrazów bazowych .NET dostępnych w lokalnym cache lub w oficjalnym rejestrze Microsoft. Załadowana paczka zawiera wszystkie warstwy używane podczas uruchomienia. [Docker Compose networking](https://docs.docker.com/compose/how-tos/networking/).

## Przebieg prezentacji

### Przełączanie między Kłodzkiem a Warszawą

Uruchom dwa lokalne projekty Compose na osobnych portach, aby każde ćwiczenie zachowało swój stan.
W PowerShellu uruchom z katalogu repozytorium:

```powershell
$env:CASSINI_SCENARIO = 'sample'
$env:CASSINI_PORT = '5080'
$env:CASSINI_COMPANION_PORT = '5180'
docker compose up -d --build

$env:CASSINI_SCENARIO = 'warsaw'
$env:CASSINI_PORT = '5180'
$env:CASSINI_COMPANION_PORT = '5080'
docker compose -p cassini-warsaw-satellite up -d --no-build --pull never
```

Otwórz [Kłodzko](http://127.0.0.1:5080) i wybierz **Przełącz na Warszawę** w nagłówku. Panel
Warszawy pokazuje w tym samym miejscu odnośnik do Kłodzka.

### Ćwiczenie

Powiększaj mapę kółkiem myszy, gestem dwóch palców albo przyciskami **+** i **−**. Przeciągnij mapę,
aby przesunąć widok. **Resetuj widok** wraca do początkowego położenia i skali.

1. Wybierz **Zgłoś zalanie**. Licznik zgłoszeń wzrośnie. Lokalna mapa pokazuje zasięg ćwiczenia i dostępne odcinki.
2. Wybierz **Pokaż rozkład** obok mieszkańca 14 lub wybierz węzeł startowy. Przy starcie o 19:20 UTC wschodnia trasa ma około 61% szansy, północna około 39%. Rozkład zawiera planowane godziny startu i przybycia.
3. Wybierz **Wylosuj trasę w węźle**. System losuje z rozkładu obowiązującego w danej rewizji. Wynik widać na mapie. W pierwszym losowaniu może wypaść dowolna z dwóch opcji.
4. W **Pomocy terenowej** wybierz **Przydziel**, aby zarezerwować jednostkę dla jednego zgłoszenia. Rezerwacja jest możliwa tylko po wyznaczeniu dojazdu i wyjazdu jednostki. Test integracyjny próbuje dwóch równoległych rezerwacji.
5. Wybierz **Zamknij most**. Poprzednia trasa jest wycofywana, jeśli prowadziła przez most. W rozkładzie zostaje 100% dla wyjścia północnego. W razie potrzeby wylosuj nową trasę i przelicz trasę jednostki. Po zamknięciu północnego dojścia węzeł wyświetli **Brak dostępnej trasy**.
6. Po przywróceniu dostępności i przydzieleniu jednostki przejdź przez potwierdzenie zadania, przyjazd i odbiór. Potwierdzenie wyjścia mieszkańca jest odrębną operacją.
7. Na głównej mapie widać dołączone zobrazowanie Sentinel‑2 oraz datę obserwacji. Rozwiń **Źródła danych**, aby sprawdzić, z czego pochodzi poligon zalania i szacunek ludności.

## Usługi i CQRS

| Serwis | Zapytania | Komendy |
| --- | --- | --- |
| Management | `GET /queries/incident`, `GET /queries/cases/{caseId}` | `POST /commands/alert`, `POST /commands/progress` |
| Situation | `GET /queries/snapshot` | `POST /commands/closure`, `POST /commands/import-copernicus` |
| Routing | `GET /queries/network`, `GET /queries/distribution?node=walk-start`, `GET /queries/draws/{commandId}`, `GET /queries/assistance-route` | `POST /commands/draw` |
| Dispatch | `GET /queries/assignments` | `POST /commands/request`, `POST /commands/assign`, `POST /commands/replan`, `POST /commands/progress` |

Gateway dodaje prefiks `/api/{management|situation|routing|dispatch}`. Handlery zapytań i komend są oddzielne. Usługi przekazują sobie dane w JSON po lokalnym HTTP i nie zapisują danych w plikach innej usługi. Komendy obsługują ponowienia z tym samym `commandId` i sprawdzają wersję lub rewizję, jeśli zmieniają istniejący stan. Na potrzeby demonstratora każda usługa zapisuje stan atomowo do własnego pliku JSON i zakłada jedną uruchomioną instancję.

Routing wyznacza najkrótszą dopuszczalną trasę do każdego dostępnego wyjścia. Przy obliczaniu rozkładu bierze odwrotność sumy oczekiwania do początku okna i czasu przejścia. Normalizuje wagi do sumy 1. Trasy przez zamknięcia, bez istotnej cechy dostępności lub poza oknem czasowym otrzymują zero szans. `POST /commands/draw` przyjmuje `commandId`, `node`, `revision`, opcjonalnie `at`, `subjectId` i testowy `seed`. Wynik przechodzi ponowną weryfikację na podstawie aktualnego obrazu sytuacji. Panel odświeża zapytania co 7 sekund i od razu po własnych komendach.

## Źródła satelitarne i zakres offline

- **Podkład mapy.** Widok całej Warszawy pokazuje Sentinel‑2. Kłodzko i zbliżenie mostu Świętokrzyskiego domyślnie korzystają ze szczegółowej [ortofotomapy GUGiK](https://www.geoportal.gov.pl/pl/dane/ortofotomapa-orto/). Przycisk **Pokaż Sentinel-2** pod mapą przełącza podkład zbliżenia. Obrazy i [metadane](../apps/web/maps/satellite.json) są dołączone do Gateway i działają offline. Contains modified Copernicus Sentinel data 2026.
- **Copernicus EMS** dostarcza dołączoną warstwę archiwalną [EMSR756](https://mapping.emergency.copernicus.eu/activations/EMSR756/) w scenariuszu Kłodzka. Zasięg zalania z obserwacji Sentinel‑1 z 15 września 2024 r. jest przycięty do widoku, oznaczony w legendzie i służy jako kontekst historyczny. Rozkład tras uwzględnia zamknięcia z ćwiczenia. Warszawa pokazuje zobrazowanie Sentinel‑2 i symulowany zasięg zalania.
- **Galileo** jest odczytywany na kompatybilnym odbiorniku Androida. Widok pokazuje pozycję po skorelowaniu świeżego pomiaru ze statusem wskazującym użycie wyłącznie Galileo. Aplikacja nie wykorzystuje pomiaru sprzętowego do prowadzenia po syntetycznym grafie. [Galileo Open Service](https://www.euspa.europa.eu/galileo-os), [Android GnssStatus](https://developer.android.com/reference/android/location/GnssStatus).
- **EGNOS** wymaga sprzętu, który potwierdza faktyczne zastosowanie korekt. **GOVSATCOM** i **IRIS²** przewidziano jako możliwe łącza dla wyposażonego punktu koordynacji. Demo nie wymaga terminala ani aktywnego połączenia. [EGNOS](https://www.euspa.europa.eu/eu-space-programme/egnos/services), [GOVSATCOM](https://www.euspa.europa.eu/govsatcom-hub), [IRIS²](https://defence-industry-space.ec.europa.eu/eu-space/iris2-secure-connectivity_en).

Bez sieci między urządzeniami Android używa własnych danych ćwiczeniowych. Zmiany w panelu i w telefonie nie są wtedy rozsyłane na drugie urządzenie. Dostęp do panelu poza komputerem koordynacji, rzeczywista dystrybucja poleceń po satelicie, logowanie operatorów i operacyjne dane terenowe są poza zakresem tego demo. Przed decyzjami w terenie potrzebna jest walidacja danych i przepustowości na rzeczywistej sieci.

## Weryfikacja lokalna

```sh
python scripts/offline_demo.py --test
dotnet run --project src/Routing/tests/Cassini.Routing.Tests.csproj -p:NuGetAudit=false
dotnet run --project apps/offline-android/tests/Offline.Tests.csproj -p:NuGetAudit=false
docker compose config --quiet
```

Test integracyjny uruchamia pięć procesów na kolejnych portach i używa tymczasowego stanu. `--port 5180` pozwala testować obok działającego Compose. Kod testów można też uruchomić na świeżym środowisku Compose przez `python tests/offline_integration.py http://127.0.0.1:5080`. Test zmienia stan scenariusza, dlatego powinien działać na osobnym środowisku.

Wersję Android 11+ buduje się niezależnie od Dockera według [instrukcji aplikacji](../apps/offline-android/README.md). Kompilacja i test rozkładu nie zastępują próby Galileo w trybie samolotowym na fizycznym urządzeniu.
