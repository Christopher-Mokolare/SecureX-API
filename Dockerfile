FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SecureX.Api/SecureX.Api.csproj SecureX.Api/
RUN dotnet restore SecureX.Api/SecureX.Api.csproj
COPY SecureX.Api/ SecureX.Api/
RUN dotnet publish SecureX.Api/SecureX.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
EXPOSE 8080
ENTRYPOINT ["dotnet", "SecureX.Api.dll"]
