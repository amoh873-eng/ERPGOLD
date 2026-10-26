# syntax=docker/dockerfile:1
# ---------------------------------------------------------------
# ERPSystem.Api  (REST API consumed by the mobile/MAUI client)
# Multi-stage build: SDK -> runtime.
# The runtime stage creates the legacy "D:" fallback directory used
# by SavePhotoFile() / WriteReviewFile() (Windows-style paths such as
# "D:/erp_inv_*.jpg"), so those writes keep working on Linux.
# ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY nuget.config ./
COPY ERPSystem.slnx ./
COPY src/Domain/ERPSystem.Domain.csproj src/Domain/
COPY src/Application/ERPSystem.Application.csproj src/Application/
COPY src/Infrastructure/ERPSystem.Infrastructure.csproj src/Infrastructure/
COPY src/Api/ERPSystem.Api.csproj src/Api/
RUN dotnet restore src/Api/ERPSystem.Api.csproj

COPY src/ src/
RUN dotnet publish src/Api/ERPSystem.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/D \
    && ln -s /app/D '/app/D:' \
    && chown -R app:app /app

COPY --from=build /app/publish .

USER app
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "ERPSystem.Api.dll"]