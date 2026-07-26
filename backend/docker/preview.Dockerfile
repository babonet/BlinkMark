# syntax=docker/dockerfile:1
#
# BlinkMark preview origin.
#
# Deployed to its own hostname so that a sanitizer bypass lands in an origin with no access to
# the application's session, tokens, or cookies (Principle IV). It holds no Entra configuration
# because it never validates an Entra token — its only credential is a preview token.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY backend/src/BlinkMark.Core/BlinkMark.Core.csproj backend/src/BlinkMark.Core/
COPY backend/src/BlinkMark.Infrastructure/BlinkMark.Infrastructure.csproj backend/src/BlinkMark.Infrastructure/
COPY backend/src/BlinkMark.Preview/BlinkMark.Preview.csproj backend/src/BlinkMark.Preview/

RUN dotnet restore backend/src/BlinkMark.Preview/BlinkMark.Preview.csproj

COPY backend/src/ backend/src/
RUN dotnet publish backend/src/BlinkMark.Preview/BlinkMark.Preview.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1

RUN adduser --disabled-password --gecos '' --uid 10001 blinkmark
USER 10001

COPY --from=build /app .

EXPOSE 8080
ENTRYPOINT ["dotnet", "BlinkMark.Preview.dll"]
