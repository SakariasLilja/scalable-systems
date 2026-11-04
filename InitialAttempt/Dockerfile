FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app
COPY . .
RUN dotnet build
EXPOSE 5032
CMD ["dotnet", "run", "--no-build", "--no-launch-profile"]