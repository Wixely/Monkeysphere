FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Monkeysphere.slnx --locked-mode
RUN dotnet publish src/Monkeysphere.Web/Monkeysphere.Web.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data && chown -R 1654:1654 /data
USER 1654
ENV ASPNETCORE_URLS=http://+:8080
ENV MONKEYSPHERE_DATA_ROOT=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Monkeysphere.Web.dll"]
