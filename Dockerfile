# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy central build/package configuration and project files first for restore-layer caching.
COPY Directory.Build.props Directory.Packages.props OrderService.slnx ./
COPY src/OrderService.Api/OrderService.Api.csproj src/OrderService.Api/
COPY src/OrderService.Application/OrderService.Application.csproj src/OrderService.Application/
COPY src/OrderService.Domain/OrderService.Domain.csproj src/OrderService.Domain/
COPY src/OrderService.Infrastructure/OrderService.Infrastructure.csproj src/OrderService.Infrastructure/

RUN dotnet restore "src/OrderService.Api/OrderService.Api.csproj"

COPY src/ src/
RUN dotnet publish "src/OrderService.Api/OrderService.Api.csproj" \
    --configuration "$BUILD_CONFIGURATION" \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is installed only for the container healthcheck; the application still runs as the
# non-root user supplied by the official .NET runtime image.
RUN apt-get update \
    && apt-get install --no-install-recommends --yes curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

COPY --from=build /app/publish ./

USER $APP_UID
ENTRYPOINT ["dotnet", "OrderService.Api.dll"]
