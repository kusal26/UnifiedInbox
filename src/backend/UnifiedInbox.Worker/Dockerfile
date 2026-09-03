FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/backend/UnifiedInbox.Worker/UnifiedInbox.Worker.csproj -c Release -o /app --no-self-contained
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "UnifiedInbox.Worker.dll"]
