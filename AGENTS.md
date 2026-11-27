# Repository instructions

Read the [agent guide](docs/agents/README.md) before editing. Check the current branch and working tree and follow the [Git workflow](CONTRIBUTING.md). Read the [domain rules](docs/domain/domain-invariants.md) for changes involving evacuation behaviour. Keep work within the assigned scope and report the checks you ran.

## Cel projektu

Budujemy demo ewakuacji podczas powodzi. Scenariusz uruchamia się na jednym komputerze bez internetu. Cztery niezależne mikroserwisy .NET 10 udostępniają API po lokalnym HTTP, a Gateway serwuje panel. Android może uruchomić przygotowane ćwiczenie samodzielnie, również poza siecią lokalną.

Przed zmianą przeczytaj `README.md`, odpowiednie pliki w `docs/` i kod usługi, której dotyka zadanie. Starsze specyfikacje opisują też architekturę docelową. W razie rozbieżności z demonstratorem sprawdź aktualne kontrakty HTTP i testy. W zadaniach równoległych ustal właściciela danego katalogu i uzgodnij kontrakt przed zmianą współdzielonych plików.

## Własność i CQRS

| Katalog | Odpowiedzialność |
| --- | --- |
| `src/Management/` | Zgłoszenia, sprawy mieszkańców i potwierdzenia postępu. |
| `src/Situation/` | Wersjonowany obraz sytuacji, zamknięcia odcinków i provenance warstw zagrożeń. |
| `src/Routing/` | Graf, rozkład tras dla węzła, losowanie i weryfikacja wydanych tras, dojazd i wyjazd jednostki. |
| `src/Dispatch/` | Zgłoszenia pomocy, rezerwacja jednostek i kolejne stany zadania. |
| `src/Gateway/`, `apps/web/` | Brama i widok operacyjny. Widok odczytuje dane właścicieli i przedstawia wynik losowania. |
| `apps/offline-android/` | Samodzielne ćwiczenie i odczyt stanu kompatybilnego odbiornika GNSS. |

- Utrzymuj oddzielne handlery odczytu `GET /queries/…` i zapisu `POST /commands/…`. Zapytania nie zmieniają trwałego stanu. Komendę kieruj do usługi będącej właścicielem danych.
- Każdy serwis ma własny stan. Przekazuj między usługami jawne DTO i identyfikatory. Uzgodnij zmiany kontraktów z ich konsumentami.
- Zachowuj `commandId` przy ponowieniu tej samej operacji. Inna treść z tym samym ID to konflikt. Dla zmiany współbieżnej używaj wersji sprawy lub rewizji sytuacji.
- Oddziel odbiór polecenia, pozycję, odbiór osoby i potwierdzenie jej wyjścia. Zachowaj blokadę podwójnej rezerwacji jednostki.

## Rozkład i losowanie w węźle

1. Pobierz wersjonowany obraz sytuacji. Dla węzła i przewidywanej godziny przejścia wyznacz dopuszczalne trasy oraz ich godziny startu i przybycia.
2. Usuń trasy prowadzące przez zamknięte odcinki oraz te, których istotnych wymagań nie można potwierdzić. Dopiero wtedy oblicz znormalizowane prawdopodobieństwa. Obecne demo stosuje odwrotność czasu oczekiwania plus czas przejścia.
3. Węzeł wywołuje komendę losowania z rewizją widocznego rozkładu. Seed służy do powtarzalnych testów. Przy zmianie rewizji odśwież rozkład przed kolejną próbą.
4. Pokazuj wycofanie i brak trasy jako osobne stany. Brak połączenia z usługą lub nieaktualne dane nie mogą dać wskazówki o potwierdzonej dostępności. Znany wynik sprawdzaj po ponownym połączeniu.

## Offline i dane

- Zachowaj zimny start po przygotowaniu plików lub obrazów. Sieć lokalna między procesami jest częścią demo. Nie dodawaj zewnętrznych kafelków, CDN, routingu w chmurze ani zewnętrznego logowania do ścieżki offline.
- Galileo służy do pozycji na zgodnym odbiorniku. Oznaczaj stan sygnału zgodnie z tym, co raportuje sprzęt. Rozpoznawaj EGNOS wyłącznie na podstawie potwierdzenia zastosowania przez odbiornik.
- Importuj Copernicus EMS jako lokalną warstwę z ID produktu i czasem obserwacji. Dane przywiezione bez łączności mają ważność i zasięg produktu źródłowego. Warstwa testowa i dane zaimportowane przez operatora muszą mieć osobne etykiety.
- GOVSATCOM i IRIS² mogą zapewnić łącze wyposażonemu, uprawnionemu punktowi koordynacji. Bez łącza zgłoszenie lub zmiana w jednym urządzeniu pozostaje lokalna. Nie pokazuj fałszywego potwierdzenia dostawy.
- Dane ćwiczeń są syntetyczne. W szablonie pozostaw puste pola na dane dostarczone przez zespół. Nie dodawaj do Git poświadczeń, danych mieszkańców ani prywatnych warstw operacyjnych.

## Weryfikacja i przekazanie pracy

- Uruchom `python scripts/offline_demo.py --test` oraz testy komponentu, który zmieniasz. Przy zmianie widoku sprawdź scenariusz z dostępną trasą, zamknięciem mostu i brakiem wyjścia. Przy zmianie Docker sprawdź konfigurację, budowę i uruchomienie, jeżeli silnik jest dostępny.
- Przekaż innemu agentowi zakres plików, zmiany kontraktu, sposób uruchomienia, wyniki testów i granice weryfikacji sprzętowej. Podaj jawnie użyte dane syntetyczne.
- Jeżeli operator poprosi o regularne pushe, wypychaj małe sprawdzone checkpointy na uzgodniony branch. Sprawdź zakres staged przed commitem i zweryfikuj wynik push. Traktuj push i merge jako osobne operacje.
