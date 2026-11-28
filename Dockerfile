FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
ARG ENABLE_DISPATCH_POSTGRES=false
WORKDIR /source
COPY src/ src/
COPY apps/web/ apps/web/
COPY data/demo/population/estimate.json data/demo/population/estimate.json
COPY data/demo/ors/ data/demo/ors/
COPY data/demo/sheltering/sample-sites.json data/demo/sheltering/sample-sites.json
RUN dotnet publish "$PROJECT" -c Release -o /output -p:NuGetAudit=false -p:EnableDispatchPostgres="$ENABLE_DISPATCH_POSTGRES"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
RUN mkdir /state && chown "$APP_UID" /state
COPY --from=build /output/ ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet"]
