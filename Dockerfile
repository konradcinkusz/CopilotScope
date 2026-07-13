# CopilotScope — kolektor OTLP + dashboard w jednym kontenerze.
# Build:  docker build -t copilotscope .
# Run:    docker run -p 4318:4318 -e CopilotScope__Ingest__ApiKey=<sekret> copilotscope

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config .
COPY src/CopilotScope.Collector/ src/CopilotScope.Collector/
RUN dotnet publish src/CopilotScope.Collector -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:4318
EXPOSE 4318

# Healthcheck na wbudowany endpoint
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD wget -qO- http://localhost:4318/api/health || exit 1

ENTRYPOINT ["dotnet", "CopilotScope.Collector.dll"]
