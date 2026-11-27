# CASSINI Guardians of the Galaxy

A prototype for coordinating civilian evacuation during a flood. The offline demo follows a hazard alert, draws a timed pedestrian route, dispatches assistance, and revises instructions after a bridge closure. Capacity planning and shelter allocation are part of the wider project design.

- [Pitch and flood demo](docs/pitch/README.md) and [project one-pager](docs/pitch/project-one-pager.md)
- [Business processes](docs/domain/business-processes.md) and [domain invariants](docs/domain/domain-invariants.md)
- [Planned source layout and stack](src/README.md) and [sample data](data/sample/README.md)
- [Space technologies and tools](docs/tools/README.md)
- [Git workflow](CONTRIBUTING.md) and [agent guide](docs/agents/README.md)

## Uruchom demo offline

```sh
docker compose build
docker compose up -d --no-build --pull never
```

Otwórz [panel CASSINI](http://127.0.0.1:5080). Po zbudowaniu obrazów panel, wszystkie serwisy i mapa startują bez dostępu do internetu. Alternatywa z .NET 10 SDK i Pythonem 3 to `python scripts/offline_demo.py`.

Zobacz [instrukcję demo i transportu obrazów](docs/offline-demo.md), [aplikację Android](apps/offline-android/README.md), [pola na dane zespołu](data/local/README.md) i [instrukcje dla agentów](AGENTS.md).

Starszy [plan pełnej architektury](docs/mvp-implementation-guide.md) opisuje następną warstwę wdrożenia, w tym logowanie, bazy relacyjne i dystrybucję zdarzeń.
