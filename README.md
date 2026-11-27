# CASSINI-Guardians-of-the-Galaxy

An MVP for coordinating civilian evacuation during a flood. The demo follows a hazard alert, routes a pedestrian out of the affected area, dispatches assistance, and revises affected instructions after a bridge closure.

## Uruchom demo offline

```sh
docker compose build
docker compose up -d --no-build --pull never
```

Otwórz [panel CASSINI](http://127.0.0.1:5080). Po zbudowaniu obrazów panel, wszystkie serwisy i mapa startują bez dostępu do internetu. Alternatywa z .NET 10 SDK i Pythonem 3 to `python scripts/offline_demo.py`.

Zobacz [instrukcję demo i transportu obrazów](docs/offline-demo.md), [aplikację Android](apps/offline-android/README.md), [pola na dane zespołu](data/local/README.md) i [instrukcje dla agentów](AGENTS.md).

Starszy [plan pełnej architektury](docs/mvp-implementation-guide.md) opisuje następną warstwę wdrożenia, w tym logowanie, bazy relacyjne i dystrybucję zdarzeń.
