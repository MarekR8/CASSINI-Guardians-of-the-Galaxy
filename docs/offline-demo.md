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

- **Sentinel‑2** dostarcza tło głównej mapy. Obrazy Kłodzka i Warszawy są dołączone do Gateway i wyświetlają się automatycznie offline. Daty, zasięgi i identyfikatory produktów są w [metadanych obrazów](../apps/web/maps/satellite.json). Contains modified Copernicus Sentinel data 2026.
- **Copernicus EMS** dostarcza archiwalne warstwy, które zespół może przygotować przed uruchomieniem pokazu. Warstwy poligonowe przekazane przez [skrypt przygotowania danych](../data/local/README.md) pojawiają się na głównej mapie ze źródłem, datą i identyfikatorem produktu.
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
