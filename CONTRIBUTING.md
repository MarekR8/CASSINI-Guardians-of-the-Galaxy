# Contributing

## Current workflow

Coordinate edits to shared files, check Git status and keep commits focused. Start individual branches from the current `develop`, using names such as `feature/road-graph` or `fix/shelter-capacity`. Open pull requests from those branches to `develop`. Integrate tested changes from `develop` to `main` through a separate pull request.

```text
feature/* or fix/*  →  develop  →  main
```

Include the relevant checks and any change to shared data in the pull request. Coordinate changes to the [sample data format](data/sample/README.md) with contributors on both sides of the integration.
