FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/BillingService.Domain/BillingService.Domain.csproj src/BillingService.Domain/
COPY src/BillingService.Application/BillingService.Application.csproj src/BillingService.Application/
COPY src/BillingService.Infrastructure/BillingService.Infrastructure.csproj src/BillingService.Infrastructure/
COPY src/BillingService.Api/BillingService.Api.csproj src/BillingService.Api/

RUN dotnet restore src/BillingService.Api/BillingService.Api.csproj

COPY src/ src/
RUN dotnet publish src/BillingService.Api/BillingService.Api.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "BillingService.Api.dll"]
