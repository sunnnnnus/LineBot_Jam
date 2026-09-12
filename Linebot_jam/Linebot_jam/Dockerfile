FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Linebot_jam.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# 容器預設跑 UTC，但任務時間欄位是不帶時區的 TIMESTAMP、程式一律用 DateTime.Now，
# 若不對齊使用者所在時區，「明天下午6點」會被算成 UTC 的明天，提醒也會晚 8 小時發出。
# .NET 在 Linux 需要 tzdata 才能解析時區，base image 不保證內建，因此明確安裝。
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*
ENV TZ=Asia/Taipei

# Render's free-tier containers have a low inotify instance limit; ASP.NET Core's
# default appsettings.json hot-reload watcher hits it and crashes CreateBuilder()
# before any of our code runs. We don't need hot-reload in a container, so disable it.
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false

ENTRYPOINT ["dotnet", "Linebot_jam.dll"]
