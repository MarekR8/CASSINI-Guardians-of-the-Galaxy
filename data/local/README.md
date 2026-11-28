# Dane dostarczane przez zespół

Poniżej opisano demonstrator Warszawy. Do starszego, lokalnego ćwiczenia nadal służy [szablon produktu](product.example.json). Surowe dane, stan synchronizacji i poświadczenia trzymaj lokalnie w ignorowanych katalogach.

## Task 01 · punkt podłączenia Copernicusa

Paczka trafia do `artifacts/warsaw-evidence/`. Punktem wejścia jest `artifacts/warsaw-evidence/manifest.json` utworzony z [szablonu](warsaw-sync.example.json). Zakres to **[20.830, 52.080, 21.300, 52.390]** w kolejności zachód, południe, wschód, północ (WGS 84). To prostokąt scenariusza obejmujący Warszawę i okolice. Granice i punkty ćwiczenia są w [scenario.json](../demo/scenario.json).

- **Obraz do FlyPix** dodaj w `imagery.images[]`. Przekaż GeoTIFF z zapisanym CRS i położeniem pikseli. Można dodać kilka plików o udokumentowanym pokryciu. Każdy wpis potrzebuje `geoTiff`, `imageProductId`, `observedAt` z UTC i `sourceUrl`. Ścieżki są względne do manifestu. Skrypt sprawdza wspólny zasięg, przycina obraz do AOI i dzieli duże rastry na georeferencjonowane kafle do 2048 × 2048 px. Pełne pokrycie AOI jest domyślne. Na próbę częściową ustaw `imagery.requireFullCoverage: false`. Mapa drogowa nadal pokazuje cały AOI, a detekcje ograniczają się do dostarczonych scen.
- **Warstwę zalania** dodaj w `hazards[]`. Dla EMS użyj `source: "ems"`, a dla maski opartej na Sentinel‑1 użyj `source: "sentinel-1"`. Dołącz `productId`, `observedAt`, `sourceUrl` i `geoJson` wskazujący `FeatureCollection` z poligonami przyciętymi do AOI, w WGS 84 i kolejności długość, szerokość. Dla sklasyfikowanego GeoTIFF-u zamiast `geoJson` podaj `geoTiff` i `floodValues` z kodami pikseli zalania z legendy produktu. Opcjonalne `band` wskazuje pasmo, domyślnie 1. Skrypt wektoryzuje wskazane kody i pomija piksele bez danych. Gdy brak warstwy udokumentowanego zalania, pozostaw `"hazards": []`. [Produkty Copernicus EMS](https://mapping.emergency.copernicus.eu/about/rapid-mapping/) · [Global Flood Monitoring](https://global-flood.emergency.copernicus.eu/).
- **Model FlyPix** powinien rozpoznawać klasę odpowiadającą zalaniu lub przeszkodzie na drodze. W `flypix` podaj `tenantId`, `projectId`, `modelId` i opcjonalnie `classIds` oraz `minConfidence`. Identyfikatory mogą też zostać ustawione w lokalnym `.env` jako `FLYPIX_TENANT_ID`, `FLYPIX_PROJECT_ID` i `FLYPIX_MODEL_ID`. Klucz ustaw w `FLYPIX_API_KEY`. Użycie innego modelu zmienia to, co system przedstawia do weryfikacji. [FlyPix API](https://docs.flypix.ai/docs/api) · [Klucze i uprawnienia](https://docs.flypix.ai/docs/api/authentication).

Jeżeli kolega ma bezpośrednio pobierać kadry z Copernicus Data Space, może ustawić `imagery` na przykład tak (daty należy dobrać do zdarzenia).

```json
{
  "source": "sentinel-2",
  "datetime": "2026-11-01T00:00:00Z/2026-11-25T23:59:59Z",
  "maxCloudCover": 25
}
```

Tryb wybiera sceny Sentinel‑2 L2A z jednego przelotu, których wspólny zasięg pokrywa cały AOI, i przygotowuje cztery GeoTIFF-y RGB w Sentinel Hub. Zapisuje identyfikatory produktów obok kafli w `provenance.json`. Potrzebuje `CDSE_CLIENT_ID` i `CDSE_CLIENT_SECRET` w `.env`. Przekazanie gotowego GeoTIFF-u działa bez klucza CDSE. Scena optyczna pokazuje stan powierzchni, a informację o zalaniu można dostarczyć oddzielnie w `hazards[]`. [Katalog STAC CDSE](https://documentation.dataspace.copernicus.eu/APIs/STAC.html) · [Uwierzytelnienie i pobieranie Sentinel Hub](https://documentation.dataspace.copernicus.eu/APIs/SentinelHub/UserGuides/BeginnersGuide.html).

## Uruchomienie

Z katalogu repozytorium przygotuj lokalną mapę. Import pobiera publiczny plik OSM PBF z Geofabrik i zapisuje graf i SVG w `artifacts/warsaw-city/`. Wymaga połączenia przy pierwszym pobraniu. Opcja `--refresh` pobiera nowy wyciąg. Aktualizację wykonaj przed uruchomieniem serwisów. Po przygotowaniu mapa i routing działają bez internetu, klucza OSM ani OpenRouteService. [Wyciąg OSM dla Mazowsza](https://download.geofabrik.de/europe/poland/mazowieckie.html) · [Atrybucja ODbL](https://www.openstreetmap.org/copyright).

```sh
python -m pip install -r experiments/network/requirements.txt
python experiments/network/prepare_warsaw_osm.py
python scripts/offline_demo.py --scenario warsaw
```

Pod `http://127.0.0.1:5080` wybierz całą Warszawę albo widok mostu. W trybie miejskim możesz wskazać start i cel i sprawdzić trasę pieszą lub samochodową. Punkty są przyciągane do węzłów OSM. Czasy są szacunkami ruchu swobodnego, a routing ćwiczeniowy nie uwzględnia wszystkich relacji skrętów ani tymczasowych ograniczeń terenowych. Przełączenie scenariusza mostu zachowuje dotychczasowy rozkład, losowanie i ćwiczeniowe przyciski. Zatwierdzenie detekcji z danych miejskich zamyka dopasowane odcinki mapy i aktualizuje rewizję routingu.

W drugim terminalu sprawdź klucz i uruchom synchronizację. `--prepare-imagery` przygotowuje same GeoTIFF-y. `--hazards-only` importuje same warstwy zalania. Pełna synchronizacja przesyła GeoTIFF-y, czeka na przetworzenie rastra, uruchamia model, pobiera warstwy wynikowe i importuje detekcje do sprawdzenia przez operatora. Inferencja zużywa kredyty. Powtórne uruchomienie przechodzi przez zapisane identyfikatory w `flypix-state.json`. Po niejednoznacznym wyniku wysyłki skrypt zatrzymuje się przed powtórzeniem operacji. Sprawdź zadanie w FlyPix i uzupełnij ID wskazane w komunikacie.

```sh
python experiments/network/sync_warsaw.py --check-api
python experiments/network/sync_warsaw.py artifacts/warsaw-evidence/manifest.json
```

Alternatywny import istniejącego wyniku FlyPix przyjmuje [starszy manifest eksportu](warsaw-evidence.example.json) przez `experiments/network/import_warsaw_evidence.py`. Zapisane wektory są w lokalnym `artifacts/warsaw-evidence/flypix/`. W Compose użyj oddzielnego projektu i dołącz konfigurację miejską.

```sh
docker compose -p cassini-warsaw -f compose.yaml -f compose.warsaw.yaml up --build
python scripts/offline_demo.py --scenario warsaw --test --city-directory artifacts/warsaw-city
```
