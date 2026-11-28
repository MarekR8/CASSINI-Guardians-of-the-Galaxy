# Graf ćwiczeniowy Warszawy

`graph.json` zawiera wybrane korytarze i tło drogowe z OpenStreetMap. Dane pobrano 28 listopada 2026 r. (UTC) przez publiczne API OSM. Pole `osmTimestamp` oznacza zakończenie pobierania. Tablice `*OsmWays` podają identyfikatory OSM użyte do budowy każdego korytarza. Punkty zbiórki i wyjścia wybrano do scenariusza. Czasy to szacunki ruchu swobodnego. Korytarze piesze i samochodowe mają osobne geometrie. Wariant samochodowy uwzględnia kierunkowość poszczególnych ulic, a objazd omija most Świętokrzyski.

Dane mapy © OpenStreetMap contributors, licencja Open Database License 1.0 (ODbL). [Atrybucja i licencja](https://www.openstreetmap.org/copyright) · [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/).

```sh
python experiments/network/osm_to_graph.py --check data/demo/network/graph.json
python experiments/network/osm_to_graph.py --download --output artifacts/warsaw-graph.json
```

Można też użyć lokalnego eksportu Overpass JSON z geometrią (`out body geom`) albo plików OSM XML. Pełne wycinki są danymi wejściowymi poza Git. Podczas zmiany korytarzy uruchom test scenariusza Warszawy. [Import i uruchomienie](../../local/README.md).
