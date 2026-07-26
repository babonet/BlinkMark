# syntax=docker/dockerfile:1
#
# BlinkMark retention reconciliation. Same image as the notification dispatcher; the role is
# selected by BlinkMark__Jobs__Role at run time.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY backend/src/BlinkMark.Core/BlinkMark.Core.csproj backend/src/BlinkMark.Core/
COPY backend/src/BlinkMark.Infrastructure/BlinkMark.Infrastructure.csproj backend/src/BlinkMark.Infrastructure/
COPY backend/src/BlinkMark.Jobs/BlinkMark.Jobs.csproj backend/src/BlinkMark.Jobs/

RUN dotnet restore backend/src/BlinkMark.Jobs/BlinkMark.Jobs.csproj

COPY backend/src/ backend/src/
RUN dotnet publish backend/src/BlinkMark.Jobs/BlinkMark.Jobs.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    BlinkMark__Jobs__Role=reconciliation

RUN adduser --disabled-password --gecos '' --uid 10001 blinkmark
USER 10001

COPY --from=build /app .

ENTRYPOINT ["dotnet", "BlinkMark.Jobs.dll"]
