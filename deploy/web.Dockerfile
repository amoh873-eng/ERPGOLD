# syntax=docker/dockerfile:1
# ---------------------------------------------------------------
# ERPSystem.Web  (Blazor Server ERP site)
# Multi-stage build: SDK -> runtime.
# The runtime stage additionally installs the PostgreSQL client
# (pg_dump) so the application's built-in backup feature works.
# ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src

# Restore with package sources only (nuget.org + .NET public feeds)
COPY nuget.config ./
COPY ERPSystem.slnx ./
COPY src/Domain/ERPSystem.Domain.csproj src/Domain/
COPY src/Application/ERPSystem.Application.csproj src/Application/
COPY src/Infrastructure/ERPSystem.Infrastructure.csproj src/Infrastructure/
COPY src/Web/ERPSystem.Web.csproj src/Web/
RUN dotnet restore src/Web/ERPSystem.Web.csproj

COPY src/ src/
RUN dotnet publish src/Web/ERPSystem.Web.csproj -c Release -o /app/publish
RUN test -f /app/publish/wwwroot/_framework/blazor.web.js || (echo "ERROR: blazor.web.js was not generated during Docker publish"; exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Runtime tooling: curl (health checks) + postgresql-client (pg_dump for backups)
# + Fonts for Arabic PDF export: fonts-noto-core (NotoNaskhArabic) and fonts-dejavu-core.
#   The base aspnet image ships NO fonts at all, which made Arabic glyphs in exported PDFs
#   appear missing/disconnected. The build fails if no Arabic-capable font ends up installed.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl postgresql-client fonts-noto-core fonts-dejavu-core \
    && (find /usr/share/fonts -iname '*Arabic-Regular.ttf' -print -quit | grep -q . \
        || (echo "ERROR: no Arabic-capable font found after installing fonts-noto-core" && exit 1)) \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/wwwroot/uploads/items /app/wwwroot/uploads/branding /app/backups \
    && chown -R app:app /app

COPY --from=build /app/publish .

USER app
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "ERPSystem.Web.dll"]