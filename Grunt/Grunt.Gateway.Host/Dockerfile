# Halo gateway host. Build from the repository root:
#   docker build -f Grunt/Grunt.Gateway.Host/Dockerfile -t halo-gateway .
# The 9.0 SDK builds the source generator (Roslyn 4.14); the app targets net8.0 and runs on the chiseled (distroless, non-root)
# ASP.NET 8 runtime. Runs with a read-only root filesystem: tokens live on /data (mount a volume), nothing else is written.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Grunt/ Grunt/
COPY media/ media/
COPY README.md LICENSE ./
RUN dotnet restore Grunt/Grunt.Gateway.Host/Surprenant.Grunt.Gateway.Host.csproj -a $TARGETARCH
RUN dotnet publish Grunt/Grunt.Gateway.Host/Surprenant.Grunt.Gateway.Host.csproj -c Release -a $TARGETARCH --no-restore \
    -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    Gateway__Auth__TokenPath=/data/tokens.json
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Surprenant.Grunt.Gateway.Host.dll"]
