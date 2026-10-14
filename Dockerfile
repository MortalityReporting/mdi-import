FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY MortalityReporting.sln .
COPY src ./src
COPY tests ./tests
RUN dotnet restore MortalityReporting.sln
RUN dotnet publish src/MortalityReporting.ImportApi/MortalityReporting.ImportApi.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "MortalityReporting.ImportApi.dll"]
