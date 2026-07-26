# syntax=docker/dockerfile:1
#
# BlinkMark API.
#
# Note what is absent: no secrets, no build args carrying credentials, and no baked
# configuration. Everything the container needs arrives as environment variables from
# container-apps.bicep, and every one of them is an endpoint or an identifier (Principle VII).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY backend/src/BlinkMark.Core/BlinkMark.Core.csproj backend/src/BlinkMark.Core/
COPY backend/src/BlinkMark.Infrastructure/BlinkMark.Infrastructure.csproj backend/src/BlinkMark.Infrastructure/
COPY backend/src/BlinkMark.Api/BlinkMark.Api.csproj backend/src/BlinkMark.Api/

RUN dotnet restore backend/src/BlinkMark.Api/BlinkMark.Api.csproj

COPY backend/src/ backend/src/
RUN dotnet publish backend/src/BlinkMark.Api/BlinkMark.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1

# Non-root. The API reads uploaded content, and a container that runs as root turns any
# escape into a much larger problem than it needs to be.
RUN adduser --disabled-password --gecos '' --uid 10001 blinkmark
USER 10001

COPY --from=build /app .

EXPOSE 8080
ENTRYPOINT ["dotnet", "BlinkMark.Api.dll"]
