# Сборка из корня репозитория: docker build -f src/SolidBrew.Api/Dockerfile .
# global.json в образ не копируется: в образе своя версия SDK 10.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/SolidBrew.Core/SolidBrew.Core.csproj src/SolidBrew.Core/
COPY src/SolidBrew.Api/SolidBrew.Api.csproj src/SolidBrew.Api/
RUN dotnet restore src/SolidBrew.Api/SolidBrew.Api.csproj
COPY src/SolidBrew.Core/ src/SolidBrew.Core/
COPY src/SolidBrew.Api/ src/SolidBrew.Api/
RUN dotnet publish src/SolidBrew.Api/SolidBrew.Api.csproj --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/ ./
USER $APP_UID
EXPOSE 8080 8443
ENTRYPOINT ["dotnet", "SolidBrew.Api.dll"]
