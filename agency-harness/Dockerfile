# syntax=docker/dockerfile:1
# Multi-stage build: the SDK image compiles, the small ASP.NET runtime image runs.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/AgencyHarness/AgencyHarness.csproj src/AgencyHarness/
RUN dotnet restore src/AgencyHarness/AgencyHarness.csproj
COPY src/ src/
RUN dotnet publish src/AgencyHarness/AgencyHarness.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# SQLite lives on a mounted volume so data survives restarts and redeploys.
RUN mkdir -p /data && chown app:app /data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Harness="Data Source=/data/harness.db" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
VOLUME ["/data"]
# Platform health checks: GET /health/live (liveness) and /health/ready (database + model circuit).
ENTRYPOINT ["dotnet", "AgencyHarness.dll"]
