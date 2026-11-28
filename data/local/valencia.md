# Walencja · powódź z 29 października 2024 r.

Panel domyślnie pokazuje aglomerację Walencji, w tym Picanyę, Paiportę, Catarroję i Albuferę. Mapa dróg obejmuje prostokąt `[-0.62, 39.24, -0.25, 39.61]` w kolejności zachód, południe, wschód, północ. Dwa rastry Sentinel‑1 pokrywają wspólny, mniejszy fragment aglomeracji.

## Przygotowanie i uruchomienie

1. Umieść oba oryginalne GeoTIFF-y w `artifacts/valencia-evidence/`. Nazwy plików mają zaczynać się odpowiednio od `2024-10-19` i `2024-10-31`.
2. W `artifacts/valencia-evidence/emsr773-aoi01/` umieść zawartość pakietu wektorowego **EMSR773 → AOI 01 → Delineation → wersja 1**. Skrypt czyta pliki `observedEventA_v1.json`, `areaOfInterestA_v1.json` i `imageFootprintA_v1.json`, każdy z prefiksem `EMSR773_AOI01_DEL_PRODUCT_`. Warstwa zagrożenia pochodzi z `observedEventA` i obejmuje zarówno powierzchnie zalane (`Flooded area`), jak i zaobserwowane ślady zalania (`Flood trace`). [Copernicus EMSR773](https://mapping.emergency.copernicus.eu/activations/EMSR773/) · [Pakiet produktów](https://rapidmapping.emergency.copernicus.eu/EMSR773/download)
3. Z katalogu repozytorium uruchom poniższe polecenia. Pierwsze przygotowanie pobiera wyciąg OSM z Geofabrik. Kolejne używają lokalnego `artifacts/valencia-city/valencia.osm.pbf`. Opcja `--refresh` odświeża wyciąg. Po przygotowaniu danych i zależności mapa działa offline. [Wyciąg OSM Walencji](https://download.geofabrik.de/europe/spain/valencia/)
   
   ```powershell
   python -m pip install -r experiments/network/requirements.txt
   python experiments/network/prepare_valencia.py
   python scripts/offline_demo.py
   ```

Panel działa pod [127.0.0.1:5080](http://127.0.0.1:5080). Wybierz datę w **Radar Sentinel‑1**, żeby zobaczyć ten sam fragment przed zdarzeniem i po nim. Włącz **Pokaż strefy węzłów**, żeby zobaczyć klasyfikację. Pomarańczowy oznacza węzeł w obszarze zalania lub jego śladu, zielony węzeł poza oboma obszarami w zasięgu obserwacji, a szary brak oceny. Niebieski obszar pokazuje archiwalny zasięg EMS. **Trasa testowa OSM** służy do sprawdzenia połączeń i reakcji na zatwierdzone zamknięcia.

Dla Dockera, po przygotowaniu danych i pobraniu obrazów bazowych, użyj `docker compose -p cassini-valencia -f compose.yaml -f compose.valencia.yaml up --build`. Przy kolejnym uruchomieniu bez internetu użyj `up --no-build --pull never`. Pliki lokalne pozostają w ignorowanym przez Git katalogu `artifacts/`.

## FlyPix w przeglądarce

1. Zaloguj się do [FlyPix](https://app.flypix.ai/), utwórz projekt **Valencia · EMSR773** i w zakładce **Files** wrzuć osobno oba oryginalne `.tiff` z katalogu dowodów. Powinny pojawić się jako **Georaster**. Po otwarciu rastra z 19 października w **Workspace** rozwiń **Scenes → +**, wybierz raster z 31 października i kliknij **Create**. Porównuj oba obrazy przy tym samym położeniu mapy. Przycisk **Changes → Refresh** porównuje adnotacje wektorowe scen. [Wgrywanie rastrów](https://docs.flypix.ai/docs/guides/data-and-setup/upload-and-manage-imagery) · [Porównywanie scen](https://docs.flypix.ai/docs/guides/workspace-and-analysis/work-with-time-series-and-change-analysis)
2. **Granica zalania.** Opcjonalnie zaimportuj `artifacts/valencia-city/flood.geojson` przez **Import Vector Layer → Parse Content → Import Objects**. To przycięta do mapy, oficjalna obserwacja EMSR773. Używaj jej jako osobnej warstwy odniesienia. Panel wczytuje ją automatycznie z lokalnych danych i pamięta datę najpóźniejszej obserwacji produktu. Produkt składa się z trzech obserwacji wymienionych w `city-imagery.json`. [Import warstwy do FlyPix](https://docs.flypix.ai/docs/guides/data-and-setup/define-vector-layers-and-classes)
3. **Drogi do weryfikacji.** Na scenie 31 października utwórz **Data → Vector Layers → Create New Layer**, nazwij ją `valencia-road-review` i dodaj klasę `suspected_road_impact`. Wybierz **Create Polygon** i narysuj mały poligon wokół fragmentu drogi, którego dotyczy obserwacja. Osobny poligon dla każdej nieciągłej obserwacji pozwoli zatwierdzać odcinki osobno. Sprawdź sąsiednie jezdnie, kładki i drogi na innej wysokości. Dopasowanie w kodzie jest dwuwymiarowe. Jeżeli masz model odpowiedni dla zobrazowania radarowego, możesz użyć **Apply AI Model**, a potem sprawdzić jego wyniki. [Ręczne adnotacje](https://docs.flypix.ai/docs/guides/annotation/perform-manual-annotation-workflow) · [Inferencja w przeglądarce](https://docs.flypix.ai/docs/guides/ai/apply-ai-model-workflow)
4. **Eksport.** Użyj menu **⋯ → Export Layer** na warstwie drogowej. Wybierz **GeoJSON**, **EPSG:4326** i tylko poligony drogowe ze sceny zdarzenia. Odznacz **Model confidence** dla rysunków ręcznych. Dla wyniku modelu możesz zapisać liczbę z przedziału 0–1 w polu `confidence`. Plik ma być `FeatureCollection` z `Polygon` lub `MultiPolygon`; współrzędne mają kolejność `[długość, szerokość]`. Zapisz go jako `artifacts/valencia-evidence/flypix-road-review.geojson`. [Ustawienia eksportu](https://docs.flypix.ai/docs/guides/data-and-setup/export-vector-layers)
5. **Import do Situation.** Skopiuj [szablon manifestu](valencia-evidence.example.json) do `artifacts/valencia-evidence/manifest.json`. Wpisz w `imageProductId` identyfikator obrazu z 31 października i w `observedAt` jego czas rejestracji w UTC. Dla ręcznej pracy zostaw `mode: "manual"`. Wybierz `recorded` dla wyniku modelu i `synthetic` dla danych wymyślonych do scenariusza.

   ```powershell
   python experiments/network/import_valencia_review.py
   ```

   W panelu **Weryfikacja detekcji** rozwiń **Dopasowane odcinki OSM**. **Zatwierdź zamknięcie** zamyka wszystkie odcinki dopasowane do tej jednej adnotacji. Odrzucenie pozostawia odcinki otwarte. Zatwierdzone geometrie są wyświetlane na czerwono, a Routing pobiera bieżącą rewizję zamknięć z Situation.

## Kontrakt dla taska 03

`artifacts/valencia-city/city-network.json.gz` zawiera jedną wersję grafu wraz z `osmTimestamp`, `bounds` i `scenario`. Węzeł to wspólny węzeł OSM na skrzyżowaniu lub koniec odcinka. Drogi krzyżujące się na rysunku łączą się wtedy, gdy współdzielą węzeł OSM. Każda krawędź zachowuje przebieg między węzłami, typ `highway` i kierunkowe czasy dla pieszych oraz samochodów.

| Dane | Znaczenie |
| --- | --- |
| `nodes[].id, longitude, latitude` | Identyfikator węzła OSM i pozycja WGS 84 |
| `nodes[].zone` | `hazard` w poligonie EMS lub na jego granicy, `safe` poza wykrytym zalaniem we wspólnym zasięgu AOI i zobrazowań EMS, `unknown` poza tym zasięgiem |
| `roads[].id, from, to, geometry` | Identyfikator odcinka, dwa węzły OSM i geometria `[lon, lat]` |
| `roads[].walk, drive` | Sekundy `[from → to, to → from]`. `null` oznacza niedozwolony kierunek lub tryb |
| `roads[].class, lengthMetres, lanes` | Klasa OSM, długość w metrach i liczba pasów, jeżeli podano ją w OSM |
| `roads[].vehiclesPerHour, pedestriansPerHour` | Szacunkowa przepustowość godzinowa całego odcinka według klasy i, dla pojazdów, liczby pasów. Model planowania powinien współdzielić ten limit między kierunkami |
| `roads[].intersectsObservedFlood` | Przecięcie przebiegu odcinka z poligonem EMS. Możliwe również przy dwóch węzłach poza zalaniem |

Odcinki i węzły można odczytać przez `GET /api/routing/queries/city-edge/{id}` i `GET /api/routing/queries/city-node/{id}`. Zamknięcia i obserwacje dostępne są w `GET /api/situation/queries/snapshot`. Klasyfikacja węzłów dotyczy przygotowanej obserwacji historycznej. Nowa warstwa w Situation wymaga przeliczenia klasyfikacji, zanim zostanie użyta jako podstawa planowania. `safe` jest kandydatem do dalszej weryfikacji, a nie potwierdzeniem przejścia, pojemności schronienia ani aktualnego bezpieczeństwa. Zapisz wersję grafu wraz z przypisaniem mieszkańca do węzła. Planowanie do bezpiecznego wierzchołka należy do taska 03.
