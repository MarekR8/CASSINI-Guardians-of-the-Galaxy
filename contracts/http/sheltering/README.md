# Sheltering HTTP examples

These examples use the [Sheltering and Grouping interfaces](../../../docs/sheltering-interfaces.md) and synthetic `sample` scenario. Read the current Situation revision before submitting a command with verified evidence. The examples assume a fresh exercise with Situation revision `0`. Read availability at `2026-11-27T19:20:00Z` to evaluate the seeded exercise period.

| Example | Request or response | Scope |
| --- | --- | --- |
| [Set assembly point](set-assembly-point.request.json) | Request to `POST /api/situation/commands/set-assembly-point`. | SH-T01 |
| [Configure shelter](configure-shelter.request.json) | Request to `POST /api/sheltering/commands/configure-shelter`. | SH-T01 |
| [Shelter availability](shelter-availability.response.json) | Illustrative response to `GET /api/sheltering/queries/shelter-availability?at=2026-11-27T19:20:00Z`. | SH-T01 |
| [Routing planning input](shelters.v1.example.json) | Flat nominal-capacity projection for the `cassini.shelters.v1` planning input. It is not a live availability response. | Contract alignment with Routing PR #11 |
| [Reserve a place](reserve-shelter-places.draft.json) | Proposed request to `POST /api/sheltering/commands/reserve-shelter-places`. | Draft for SH-T04 |

The command examples use separate IDs from the seeded catalog so they can create entries with `expectedVersion: 0`. Repeating either command with the same payload returns its original result. For a subsequent change, use a new command ID and the site's returned version.
