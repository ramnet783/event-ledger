FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# stylecop.json is referenced via "../../stylecop.json" in the .csproj
COPY stylecop.json ./
COPY src/AccountService/ src/AccountService/

RUN dotnet restore src/AccountService/AccountService.csproj
RUN dotnet publish src/AccountService/AccountService.csproj \
        -c Release -o /app/publish --no-restore -p:RunAnalyzers=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AccountService.dll"]
