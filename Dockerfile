FROM mcr.microsoft.com/dotnet/sdk:latest AS build
WORKDIR /app

COPY . .

RUN dotnet publish importdashboards -c Release
RUN dotnet publish exportdashboards -c Release
