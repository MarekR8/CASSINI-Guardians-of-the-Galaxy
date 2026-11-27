# Contributing

## Current workflow

The team works on `main` during initial setup. Coordinate edits to shared files, check Git status and keep commits focused.

## Once the team creates `dev`

Start individual branches from `dev`, using names such as `feature/road-graph` or `fix/shelter-capacity`. Open pull requests from those branches to `dev`. Integrate tested changes from `dev` to `main` through a separate pull request.

```text
feature/* or fix/*  →  dev  →  main
```

Include the relevant checks and any change to shared data in the pull request. Coordinate changes to the [sample data format](data/sample/README.md) with contributors on both sides of the integration.
