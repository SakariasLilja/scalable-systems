FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY services/shared/Trading.Shared.csproj services/shared/
COPY services/worker/Trading.Worker.csproj services/worker/
RUN dotnet restore services/worker/Trading.Worker.csproj

COPY services/shared/ services/shared/
COPY services/worker/ services/worker/
RUN dotnet publish services/worker/Trading.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Trading.Worker.dll"]
