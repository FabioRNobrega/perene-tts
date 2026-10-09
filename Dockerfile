FROM mcr.microsoft.com/dotnet/sdk:10.0 AS development
WORKDIR /workspace/WebApp
ENV ASPNETCORE_ENVIRONMENT=Development \
    DOTNET_USE_POLLING_FILE_WATCHER=1 \
    DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
EXPOSE 8080
CMD ["dotnet", "watch", "--non-interactive", "run", "--no-launch-profile", "--urls", "http://0.0.0.0:8080"]

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY WebApp/WebApp.csproj WebApp/
RUN dotnet restore WebApp/WebApp.csproj
COPY WebApp/ WebApp/
RUN dotnet publish WebApp/WebApp.csproj -c Release -o /out --no-restore

# xUnit rules/client tests; test packages stay out of the published runtime image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS web-test
WORKDIR /src
COPY WebApp/WebApp.csproj WebApp/
COPY WebApp.Tests/WebApp.Tests.csproj WebApp.Tests/
RUN dotnet restore WebApp.Tests/WebApp.Tests.csproj
COPY WebApp/ WebApp/
COPY WebApp.Tests/ WebApp.Tests/
RUN dotnet test WebApp.Tests/WebApp.Tests.csproj --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "WebApp.dll"]
