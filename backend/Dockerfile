FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/SmartBuy.Domain/SmartBuy.Domain.csproj src/SmartBuy.Domain/
COPY src/SmartBuy.Infrastructure/SmartBuy.Infrastructure.csproj src/SmartBuy.Infrastructure/
COPY src/SmartBuy.Api/SmartBuy.Api.csproj src/SmartBuy.Api/
RUN dotnet restore src/SmartBuy.Api/SmartBuy.Api.csproj
COPY src/ src/
RUN dotnet publish src/SmartBuy.Api/SmartBuy.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SmartBuy.Api.dll"]
