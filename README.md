# CASSINI Guardians of the Galaxy

A prototype for coordinating civilian evacuation during a flood. The offline demo follows a hazard alert, draws a timed pedestrian route, dispatches assistance, and revises instructions after a bridge closure. Capacity planning and shelter allocation are part of the wider project design.

- [Pitch and flood demo](docs/pitch/README.md) and [project one-pager](docs/pitch/project-one-pager.md)
- [Business processes](docs/domain/business-processes.md) and [domain invariants](docs/domain/domain-invariants.md)
- [Planned source layout and stack](src/README.md) and [sample data](data/sample/README.md)
- [Space technologies and tools](docs/tools/README.md)
- [Git workflow](CONTRIBUTING.md) and [agent guide](docs/agents/README.md)

## Uruchom Walencję offline

```sh
python -m pip install -r experiments/network/requirements.txt
python experiments/network/prepare_valencia.py
python scripts/offline_demo.py
```

Otwórz [panel CASSINI](http://127.0.0.1:5080). Po przygotowaniu plików mapa OSM, archiwalne zalanie EMSR773 i para Sentinel‑1 działają lokalnie.
Do uruchomienia potrzebny jest .NET 10 SDK i Python 3. [Dane, FlyPix w przeglądarce, Docker i kontrakt grafu](data/local/valencia.md).

Zobacz [instrukcję demo i transportu obrazów](docs/offline-demo.md), [aplikację Android](apps/offline-android/README.md), [pola na dane zespołu](data/local/README.md) i [instrukcje dla agentów](AGENTS.md).

Opcjonalną mapę poprzedniego ćwiczenia przygotowuje `python experiments/network/prepare_warsaw_osm.py`. Scenariusz uruchamia `python scripts/offline_demo.py --scenario warsaw`. [Przygotowanie mapy i podłączenie Copernicusa i FlyPix](data/local/README.md).

Starszy [plan pełnej architektury](docs/mvp-implementation-guide.md) opisuje następną warstwę wdrożenia, w tym logowanie, bazy relacyjne i dystrybucję zdarzeń.
