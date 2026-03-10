FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY DistributedJobSystem.sln ./
COPY api/DistributedJobSystem.Api/DistributedJobSystem.Api.csproj api/DistributedJobSystem.Api/
RUN dotnet restore api/DistributedJobSystem.Api/DistributedJobSystem.Api.csproj
COPY api/DistributedJobSystem.Api/ api/DistributedJobSystem.Api/
RUN dotnet publish api/DistributedJobSystem.Api/DistributedJobSystem.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "DistributedJobSystem.Api.dll"]
