# Stage 0: Docker CLI (зафиксированная точная версия 28.5.2, полностью совместимая с Docker Engine 28.x)
FROM docker:28.5.2-cli AS docker-cli

# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Кэширование слоёв restore NuGet: копируем только проектные файлы
COPY ["src/Backend/Api/Api.csproj", "src/Backend/Api/"]
COPY ["src/Backend/Application/Application.csproj", "src/Backend/Application/"]
COPY ["src/Backend/Domain/Domain.csproj", "src/Backend/Domain/"]
COPY ["src/Backend/Infrastructure/Infrastructure.csproj", "src/Backend/Infrastructure/"]
COPY ["src/Analyzers/CSharp/CSharpAnalyzer.csproj", "src/Analyzers/CSharp/"]
COPY ["src/Analyzers/Database/DatabaseAnalyzer.csproj", "src/Analyzers/Database/"]
COPY ["src/Analyzers/Security/SecurityAnalyzer.csproj", "src/Analyzers/Security/"]
COPY ["src/Analyzers/Sql/SqlAnalyzer.csproj", "src/Analyzers/Sql/"]

# Восстановление зависимостей
RUN dotnet restore "src/Backend/Api/Api.csproj"

# Копирование исходного кода
COPY src/ src/

# Сборка и публикация в конфигурации Release
WORKDIR "/src/src/Backend/Api"
RUN dotnet publish "Api.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Установка curl для выполнения healthcheck проверки контейнера
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && (getent group docker || groupadd -g 999 docker) \
    && usermod -aG docker app \
    && mkdir -p /tmp/netscope-database-analyzer \
    && chmod 1777 /tmp/netscope-database-analyzer

# Копирование легковесного статического бинарника Docker CLI (без docker daemon и containerd)
COPY --from=docker-cli /usr/local/bin/docker /usr/local/bin/docker

# Запуск от непривилегированного пользователя app
USER app

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Api.dll"]
