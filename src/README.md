# Source layout

The planned stack is .NET and Python. Add projects and dependencies as the first concrete features need them.

| Directory | Planned responsibility |
| --- | --- |
| [`dotnet/`](dotnet/) | Application, including the coordinator interface and orchestration. Choose the app template when the UI and hosting needs are clear. |
| [`python/`](python/) | Geospatial data preparation, route and flow calculations, and simulation. Split modules as their implementations arrive. |

Start with a file-based handoff using [sample JSON](../data/sample/README.md) or GeoJSON for geometry. Agree on IDs, units and expected outputs for the first .NET–Python integration before choosing a long-lived interface. Use an HTTP service if a later use case needs independent processes or remote access.

Add a `.csproj` with the first .NET project. Add a `pyproject.toml` once the Python project needs packaging or shared tool settings.
