FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY S3NetTest.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Listen on all interfaces inside the container (appsettings.json uses localhost for local runs).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "S3NetTest.dll"]
