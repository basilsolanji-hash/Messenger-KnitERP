FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src
COPY global.json ./
COPY server/Messenger.Api/Messenger.Api.csproj server/Messenger.Api/packages.lock.json server/Messenger.Api/
RUN dotnet restore server/Messenger.Api --locked-mode
COPY server/Messenger.Api server/Messenger.Api
RUN dotnet publish server/Messenger.Api -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /data/protection && chown $APP_UID:$APP_UID /data/protection
USER $APP_UID
ENTRYPOINT ["dotnet", "Messenger.Api.dll"]
