# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Stage 1: build the SvelteKit static frontend.
# Output lands in /src/src/App.Api/wwwroot (svelte.config.js targets
# ../src/App.Api/wwwroot relative to the frontend directory).
# ---------------------------------------------------------------------------
FROM node:22-bookworm-slim AS frontend
WORKDIR /src
# Ensure the relative wwwroot output path exists.
RUN mkdir -p src/App.Api
COPY frontend ./frontend
WORKDIR /src/frontend
RUN npm ci
RUN npm run build

# ---------------------------------------------------------------------------
# Stage 2: restore, build and publish the .NET API (with frontend in wwwroot).
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/App.Api ./src/App.Api
# Bring in the built static assets so they are included in the publish output.
COPY --from=frontend /src/src/App.Api/wwwroot ./src/App.Api/wwwroot
RUN dotnet restore src/App.Api/App.Api.csproj
RUN dotnet publish src/App.Api/App.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------------------------------------------------------------------------
# Stage 3: minimal ASP.NET runtime image. Only HTTP/8080 is exposed;
# Orleans silo/gateway ports stay internal to the container.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "App.Api.dll"]
