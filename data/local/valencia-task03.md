# Walencja · graf dla etapu 03

Paczka obejmuje cały prostokąt wspólny dla scen Sentinel‑1 z 19 i 31 października 2024 r. Drogi i identyfikatory pochodzą z migawki OpenStreetMap. Klasyfikacja wynika z archiwalnej obserwacji Copernicus EMSR773. Dane są wejściem do symulacji.

- `valencia-graph.json` zawiera węzły i ważone krawędzie dla algorytmu. Na górze pliku są granice `bounds` w kolejności zachód, południe, wschód, północ, wersja OSM, źródło obserwacji i rewizja zamknięć w Situation.
- `nodes.geojson` i `roads.geojson` to warstwy do podglądu np. w QGIS. Ustaw kolor według właściwości `zone`. Dla dróg można dodatkowo użyć pola `closed`. W podglądzie GeoJSON identyfikator węzła jest tekstem. W grafie jest liczbą.
- `boundaryNodeIds` zawiera węzły z połączeniami wykraczającymi poza prostokąt. Pakiet zachowuje oryginalne identyfikatory OSM i zawiera odcinki leżące w całości wewnątrz prostokąta. Jeśli testujesz wyjście poza obszar zdjęcia, użyj większego grafu `artifacts/valencia-city/city-network.json.gz` i odpowiadającego mu stanu Situation.

## Kontrakt między etapami 01, 02 i 03

Obowiązuje format `cassini.evacuation-input.v1`. [Schemat JSON](../demo/routing/evacuation-input.v1.schema.json) i [syntetyczny przykład](../demo/routing/evacuation-input.v1.example.json) są też w paczce. Przykład ma trzy węzły, zamknięty most i piesze połączenie zastępcze.

| Właściciel | Dane przekazywane do etapu 03 |
| --- | --- |
| Etap 01 · Situation | `hazardObservation` z identyfikatorem produktu, czasami obserwacji UTC, zasięgiem oceny (`coverage`) i poligonami zagrożenia (`geoJson`). |
| Etap 02 · Routing i Situation | `nodes`, `roads`, `bounds`, `osmTimestamp` i `boundaryNodeIds`. Zatwierdzone ograniczenia mają `closed: true` przy krawędzi oraz `situationRevision` w nagłówku. Dostępne źródła decyzji są w `closureEvidence`. Klasy stref odnoszą się do produktu z etapu 01. |
| Etap 03 · Routing | Wczytuje jeden kompletny dokument, dopina syntetyczne osoby do `nodes[].id` i liczy przepływy na tej wersji danych. Ludzie, cele i ustawienia symulacji są osobnym wejściem modelu. |

Najpierw etap 03 może czytać dokument z pliku. Przy integracji HTTP odpowiedź endpointu zasilającego model powinna używać tego samego JSON-u z `inputVersion`. Algorytm i jego dane testowe zachowują ten sam interfejs. Aktualizacja obserwacji etapu 01 wymaga przeliczenia klas węzłów i krawędzi przed opublikowaniem kolejnej paczki. Eksporter Walencji odrzuca zapis Situation z inną geometrią zagrożenia, żeby nie zestawić go ze starymi klasami.

## Jak przypisać osoby i zbudować przepływ

1. Wczytaj `nodes` według `id`. Każdej wirtualnej osobie przypisz `nodeId`. Jeżeli masz pozycję, znajdź pobliski węzeł połączony z siecią właściwego trybu i zachowaj jego identyfikator. Do pierwszego testu używaj syntetycznych osób i wybierz ich pozycje w grafie.
2. Z każdej drogi utwórz skierowany łuk dla każdej niepustej wartości w `walk` albo `drive`. Indeks 0 prowadzi `from → to`, a indeks 1 `to → from`. Wartości to sekundy, a `null` oznacza niedostępny kierunek lub tryb. W analizie zamknięć pomijaj odcinki z `closed: true`.
3. `zone: hazard` oznacza przecięcie ze strefą EMS, `safe` położenie poza wykrytą strefą w obszarze obserwacji, a `unknown` brak pełnego pokrycia obserwacją. To dotyczy zarówno punktów, jak i geometrii całych krawędzi. `zone` i `closed` opisują różne dane. Odcinek może przecinać zalanie przy dwóch suchych końcach. Stosuj jawne reguły kwalifikacji odcinków przy opuszczaniu strefy i przyjmuj tylko osiągalne węzły `safe` jako kandydatów na cele ćwiczenia. Brak kwalifikującego się celu modeluj jako `NoEligibleExit`.
4. `pedestriansPerHour` oraz `vehiclesPerHour` to nominalne szacunki **dla całego odcinka**, współdzielone między kierunkami. Dla przedziału czasu przelicz limit godzinowy na długość przedziału. Status `safe` dotyczy historycznego obrazu powodzi. Do symulacji schronień trzeba osobno podać cele, ich pojemności i czas dostępności.

## Jak odświeżyć zamknięcia z FlyPix

Graf i klasy stref są już gotowe do pierwszej symulacji. W FlyPix dodawaj tylko małe poligony wokół konkretnych odcinków, na warstwie `valencia-road-review`. Eksportuj tę warstwę przez **⋯ → Export Layer → GeoJSON → EPSG:4326**. Uzupełnij manifest obrazem i czasem rzeczywiście użytej obserwacji. Zaimportuj plik przez `python experiments/network/import_valencia_review.py`. Dopasowane identyfikatory OSM są w `reviewFeatures[].segmentIds` w zapytaniu Situation `GET /api/situation/queries/snapshot`. Podgląd Walencji służy do odczytu mapy i strefy EMS. Do wyeksportowanego grafu trafiają jako zamknięte odcinki z aktualnego zapisu Situation.

Po aktualizacji przygotuj paczkę ponownie z katalogu repozytorium. Skrypt czyta stan `artifacts/valencia-demo/situation.json` lub inny wskazany przez `--state`. Można go uruchomić przy wyłączonym panelu.

```powershell
.venv/Scripts/python.exe experiments/network/export_valencia_graph.py
```

Wynik to `data/demo/routing/valencia-task03.zip`. Paczka i oba GeoTIFF-y w `data/demo/valencia/imagery/` są przeznaczone do współdzielenia w repozytorium. Do prób lokalnych użyj opcji `--output artifacts/valencia-task03.zip`.

Źródła danych. [© OpenStreetMap contributors, ODbL](https://www.openstreetmap.org/copyright) · [Copernicus Emergency Management Service, © European Union 2024, EMSR773 AOI01 Delineation](https://mapping.emergency.copernicus.eu/activations/EMSR773/)
