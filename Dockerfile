FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src
COPY . .
RUN dotnet restore Monkeysphere.slnx --locked-mode -p:RuntimeFrameworkVersion=10.0.8
RUN dotnet publish src/Monkeysphere.Web/Monkeysphere.Web.csproj --configuration Release --no-restore --output /app/publish -p:RuntimeFrameworkVersion=10.0.8

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data && chown -R 1654:1654 /data
USER 1654
ENV ASPNETCORE_URLS=http://+:8080
ENV MONKEYSPHERE_DATA_ROOT=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Monkeysphere.Web.dll"]
