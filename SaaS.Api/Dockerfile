# Esta fase é usada durante a execução no VS no modo rápido (Padrão para a configuração de Depuração)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# Esta fase é usada para compilar o projeto de serviço
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["SaaS.Api/SaaS.Api.csproj", "SaaS.Api/"]
COPY ["SaaS.Application/SaaS.Application.csproj", "SaaS.Application/"]
COPY ["SaaS.Domain/SaaS.Domain.csproj", "SaaS.Domain/"]
COPY ["SaaS.Infrastructure/SaaS.Infrastructure.csproj", "SaaS.Infrastructure/"]
COPY ["SaaS.Shared/SaaS.Shared.csproj", "SaaS.Shared/"]
RUN dotnet restore "./SaaS.Api/SaaS.Api.csproj"
COPY . .
WORKDIR "/src/SaaS.Api"
RUN dotnet build "./SaaS.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Esta fase é usada para publicar o projeto de serviço a ser copiado para a fase final
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./SaaS.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Esta fase é usada na produção ou quando executada no VS no modo normal (padrão quando não está usando a configuração de Depuração)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
USER root
RUN mkdir -p wwwroot/uploads && chown -R $APP_UID:$APP_UID wwwroot/uploads
USER $APP_UID
ENTRYPOINT ["dotnet", "SaaS.Api.dll"]
