FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Linebot_jam.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Render's free-tier containers have a low inotify instance limit; ASP.NET Core's
# default appsettings.json hot-reload watcher hits it and crashes CreateBuilder()
# before any of our code runs. We don't need hot-reload in a container, so disable it.
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false

ENTRYPOINT ["dotnet", "Linebot_jam.dll"]
