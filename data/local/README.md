# Dane dostarczane przez zespół

Skopiuj `product.example.json` do lokalnego katalogu roboczego spoza Git i wypełnij pola dla wybranego produktu Copernicus EMS. W panelu użyj ID produktu, czasu **obserwacji** UTC oraz lokalnego pliku GeoJSON. Akceptowane są wielokąty `Polygon` i `MultiPolygon` w `FeatureCollection`, w WGS 84 i kolejności długość, szerokość.

Szablon zostawia też miejsce na operacyjny graf i inwentaryzację wyjść oraz na odbiornik i terminal. Te pola mają pomóc skompletować dane. Domyślne ćwiczenie używa grafu syntetycznego. Scenariusz Warszawy używa lokalnego wycinka OpenStreetMap. Nie umieszczaj danych mieszkańców, identyfikatorów odbiorników ani poświadczeń w publicznym repozytorium.

## Warszawa · paczka od osoby odpowiedzialnej za task 01

Wspólny AOI to `[20.995, 52.225, 21.065, 52.260]` w kolejności `[west, south, east, north]`, WGS 84. Najważniejszy fragment obejmuje Wisłę, most Świętokrzyski, jego dojazdy i wyjście w głąb Pragi. Przekaż paczkę z niżej opisanymi plikami we wspólnym katalogu roboczym, np. `artifacts/warsaw/`. Skopiuj szablon jako `manifest.json`. Ścieżki plików w manifeście są względne wobec jego katalogu. [Scenariusz i punkty](../demo/scenario.json) · [Szablon manifestu](warsaw-evidence.example.json).

1. **`flood.geojson`** · poligony tymczasowego zalania. Najpierw sprawdź, czy Copernicus EMS Rapid Mapping ma produkt *Delineation/Monitoring* z aktywacją przecinającą AOI. Pobierz warstwę wektorową i przytnij ją do AOI. Dla każdego pliku podaj ID aktywacji i produktu, czas **obserwacji** w UTC, link, zasięg i rozdzielczość. Jeżeli brak produktu EMS, przygotuj maskę zalania z Sentinel-1 lub CEMS Global Flood Monitoring. Dodaj identyfikatory scen, datę obrazu odniesienia, metodę detekcji oraz sposób odfiltrowania stałej wody. W manifeście wybierz `sentinel-1` dla tej maski. Jeżeli brak potwierdzonego zalania w AOI, zgłoś to zespołowi. EMSR756 dotyczy zdarzenia w południowo-zachodniej Polsce i może posłużyć jako osobny przykład archiwalny. [CEMS Rapid Mapping](https://mapping.emergency.copernicus.eu/about/rapid-mapping/) · [EMSR756](https://mapping.emergency.copernicus.eu/news/flood-in-south-west-poland/) · [Global Flood Monitoring](https://global-flood.emergency.copernicus.eu/).
2. **`flypix-image.tif`** · georeferencjonowany GeoTIFF obejmujący most i dojazdy, wraz z ID źródła, czasem obserwacji, linkiem, CRS, rozdzielczością i prawem do użycia. Preferowany jest obraz lotniczy lub satelitarny o rozdzielczości około metra lub lepszej, o ile jest dostępny do tego zdarzenia. Można użyć bezchmurnego Sentinel-2 L2A RGB (B04/B03/B02, 10 m) do pokazania szerszego zasięgu. Taka rozdzielczość ogranicza ocenę poszczególnych pasów i przejezdności mostu. Dla porównania przed/po dobierz dwie zbliżone sceny i zapisz osobno daty. [Sentinel-2 w CDSE](https://documentation.dataspace.copernicus.eu/Data/SentinelMissions/Sentinel2.html) · [Rozdzielczość danych CEMS](https://mapping.emergency.copernicus.eu/about/using-of-earth-observation-data/).
3. **GHSL w drugim kroku** · dla taska 01 można dostarczyć raster GHS-POP przycięty do AOI i sumę ludności w poligonie wraz z epoką, rozdzielczością i metodą obliczenia. Panel Warszawy przyjmuje obecnie warstwy zagrożenia i detekcje. Estymator ludności nadal należy do taska 01. [GHSL](https://human-settlement.emergency.copernicus.eu/).

GeoJSON powinien zawierać `FeatureCollection` z `Polygon` lub `MultiPolygon` w EPSG:4326 i w kolejności długość, szerokość. Popraw geometrie i usuń atrybuty z danymi wrażliwymi. Manifest dopuszcza kilka warstw w `hazards`. Usuń przykładowe pola warstw, których nie dostarczasz. GeoTIFF trafia do projektu FlyPix, a do aplikacji trafiają poligony detekcji.

## Uruchom i podłącz dane

```sh
python scripts/offline_demo.py --scenario warsaw
```

Panel działa pod adresem `http://127.0.0.1:5080`. Najpierw wylosuj trasę lub sprawdź oba warianty. Warstwa ćwiczeniowa jest oznaczona jako syntetyczna. W panelu możesz zatwierdzić zamknięcie mostu, zobaczyć wycofanie trasy przez most i przeliczyć rozkład dla wyjścia na Pragę. Po zamknięciu dojścia północnego panel pokaże brak dostępnych wyjść. Jeśli potrzebujesz powtórzyć pokaz z czystym stanem, użyj `--data` z nowym katalogiem. W Compose ustaw `CASSINI_SCENARIO=warsaw` i nazwę projektu `docker compose -p cassini-warsaw`, żeby stan obu scenariuszy pozostał osobny.

**Z eksportu FlyPix.** Załaduj GeoTIFF w projekcie FlyPix, uruchom model i wyeksportuj wynik dla klasy zalania/detekcji przejścia jako `flypix-detections.geojson`. Jeżeli workspace pozwala tylko wyeksportować całą warstwę, zostaw w pliku wyłącznie klasy użyte do weryfikacji. Uzupełnij `flypix.observedAt`, `imageProductId`, `layerId` i opcjonalnie `modelId`. Zastosuj `mode: "recorded"` do wyniku modelu lub `mode: "synthetic"` do ręcznie narysowanego przykładu. Jeżeli eksport udostępnia wynik ufności, podaj go jako liczbę 0–1 w `properties.confidence`. [Eksport z FlyPix](https://docs.flypix.ai/docs/guides/annotation/edit-and-manage-vectors#22-export-data-as-geojson-or-geopackage).

```sh
python experiments/network/import_warsaw_evidence.py artifacts/warsaw/manifest.json
```

**Z API FlyPix.** W workspace sprawdź, czy możesz utworzyć klucz API dla projektu. Ustaw go lokalnie w zmiennej środowiskowej `FLYPIX_API_KEY`. Wystarczy ID warstwy wynikowej do pobrania istniejącej detekcji. Opcjonalne `flypix.classIds` zawęża wynik do klas modelu użytych w ćwiczeniu. Przy poligonach WKB/WKT zainstaluj Shapely w środowisku uruchamiającym skrypt. `--save` pozwala zapisać wynik i odtworzyć go w trybie offline. [FlyPix API](https://docs.flypix.ai/docs/api) · [Klucze i uprawnienia](https://docs.flypix.ai/docs/api/authentication).

```sh
python experiments/network/import_warsaw_evidence.py artifacts/warsaw/manifest.json --vector-id ID_WARSTWY --save artifacts/warsaw/flypix-detections.geojson
```

Aby przesłać GeoTIFF z manifestu przez API, dodaj `tenantId` i `projectId`, a następnie uruchom skrypt z `--upload-raster`. Użyj wypisanego ID w `rasterId`, poczekaj na przetworzenie rastra w projekcie, dodaj `modelId` i uruchom skrypt z `--submit`. Ten tryb może zużyć kredyty. Skrypt wypisuje ID inferencji. `--inference-id ID` wznawia czekanie na istniejący wynik. Gdy odpowiedź API pomija ID warstwy, odczytaj je w projekcie i użyj `--vector-id`. Do importu samych warstw Copernicus usuń sekcję `flypix` z lokalnego manifestu.

Po imporcie Situation przecina obrysy detekcji z geometrią lokalnych korytarzy OSM. Sam import i odrzucenie detekcji pozostawiają rozkład tras bez zmian. Zatwierdzenie zamyka wskazane korytarze w jednej rewizji, zapisuje dowód i wycofuje wydane trasy. Zamknięcie w ćwiczeniu obowiązuje do ręcznego otwarcia. Routing oblicza najkrótsze ścieżki osobno dla dopuszczonych trybów ruchu i normalizuje istniejące wagi według czasu oczekiwania i przejścia. OpenRouteService można podłączyć osobno w tasku 04.
