FROM node:20-alpine AS frontend
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY services/shared/Trading.Shared.csproj services/shared/
COPY services/server/Trading.Server.csproj services/server/
RUN dotnet restore services/server/Trading.Server.csproj
COPY services/shared/ services/shared/
COPY services/server/ services/server/
RUN dotnet publish services/server/Trading.Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=frontend /frontend/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Trading.Server.dll"]
