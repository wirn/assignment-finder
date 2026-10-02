FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY global.json AssignmentFinder.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore AssignmentFinder.slnx --locked-mode
RUN dotnet build AssignmentFinder.slnx -c Release --no-restore
RUN dotnet publish src/AssignmentFinder.App -c Release --no-restore -o /out

FROM build AS postgres-checks
ENTRYPOINT ["dotnet", "/src/tests/AssignmentFinder.ParserChecks/bin/Release/net10.0/AssignmentFinder.ParserChecks.dll", "--postgres"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS app-runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl --fail --silent http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "AssignmentFinder.App.dll"]

FROM app-runtime AS browser-app
USER root
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN dotnet AssignmentFinder.App.dll --install-browser
USER $APP_UID

# Default image remains the small runtime without Chromium.
FROM app-runtime AS app
