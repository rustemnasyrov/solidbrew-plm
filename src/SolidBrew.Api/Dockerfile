FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/SolidBrew.Api/SolidBrew.Api.csproj ./
RUN dotnet restore
COPY src/SolidBrew.Api/ ./
RUN dotnet publish --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/ ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SolidBrew.Api.dll"]
