FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY global.json AssignmentFinder.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore AssignmentFinder.slnx --locked-mode
RUN dotnet publish src/AssignmentFinder.App -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD curl --fail --silent http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "AssignmentFinder.App.dll"]
