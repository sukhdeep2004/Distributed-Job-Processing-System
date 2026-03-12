FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENV DOTNET_NOLOGO=1
COPY DistributedJobSystem.sln ./
COPY worker/DistributedJobSystem.Worker/DistributedJobSystem.Worker.csproj worker/DistributedJobSystem.Worker/
RUN dotnet restore worker/DistributedJobSystem.Worker/DistributedJobSystem.Worker.csproj
COPY worker/DistributedJobSystem.Worker/ worker/DistributedJobSystem.Worker/
RUN dotnet publish worker/DistributedJobSystem.Worker/DistributedJobSystem.Worker.csproj -c Release -o /app/publish --no-restore /p:UseSharedCompilation=false /p:RunAnalyzers=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DistributedJobSystem.Worker.dll"]
