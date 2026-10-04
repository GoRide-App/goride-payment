FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/GoRide.Payment/GoRide.Payment.csproj src/GoRide.Payment/
RUN dotnet restore src/GoRide.Payment/GoRide.Payment.csproj
COPY src/GoRide.Payment/ src/GoRide.Payment/
RUN dotnet publish src/GoRide.Payment/GoRide.Payment.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "GoRide.Payment.dll"]
