# ---- Build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY OficinaExecucao.Domain/OficinaExecucao.Domain.csproj OficinaExecucao.Domain/
COPY OficinaExecucao.Api/OficinaExecucao.Api.csproj OficinaExecucao.Api/
RUN dotnet restore OficinaExecucao.Api/OficinaExecucao.Api.csproj

COPY OficinaExecucao.Domain/ OficinaExecucao.Domain/
COPY OficinaExecucao.Api/ OficinaExecucao.Api/
RUN dotnet publish OficinaExecucao.Api/OficinaExecucao.Api.csproj -c Release -o /app/publish --no-restore

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Executa como usuario nao-root
RUN adduser --disabled-password --gecos "" appuser
USER appuser

WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "OficinaExecucao.Api.dll"]
