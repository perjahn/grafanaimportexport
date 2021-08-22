FROM mcr.microsoft.com/dotnet/nightly/sdk:latest AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app

COPY . .

RUN dotnet publish importdashboards -c Release
RUN dotnet publish exportdashboards -c Release
