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

Panel ma tryb prezentacji z krokami scenariusza Walencji, legendę z pochodzeniem warstw i panel metryk. Ten sam scenariusz w Dockerze uruchamia `docker compose -f compose.yaml -f compose.valencia.yaml up -d`. [Przebieg prezentacji, logowanie Keycloak i testy](docs/offline-demo.md). `sample` (Kłodzko) zostaje jako scenariusz testów i CI: `python scripts/offline_demo.py --test`.

Starszy [plan pełnej architektury](docs/mvp-implementation-guide.md) opisuje następną warstwę wdrożenia, w tym logowanie, bazy relacyjne i dystrybucję zdarzeń.

## Aplikacja mieszkańca

Uruchom `python scripts/offline_demo.py --scenario sample` lub wybierz `--scenario warsaw` i otwórz
[aplikację mieszkańca](http://127.0.0.1:5080/resident/). Panel koordynatora jest dostępny pod adresem głównym.
W panelu dodaj alert ćwiczeniowy, aby mieszkaniec zobaczył komunikat. Widok mieszkańca pokazuje status pomocy
i punkt odbioru, gdy system je udostępnia, oraz pozwala przekazać oświadczenie o opuszczeniu strefy.
Automatyczne przypisanie punktu odbioru wymaga integracji z systemem wyznaczającym punkty.
Gateway demonstratora działa na localhost. Publiczne udostępnienie wymaga
HTTPS, uwierzytelnienia mieszkańca i interfejsu ograniczonego do jego własnej sprawy.
