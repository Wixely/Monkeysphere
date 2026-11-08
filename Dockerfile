FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
ARG MONKEYSPHERE_VERSION=0.1.0-alpha.2
ARG MONKEYSPHERE_REVISION=unknown
WORKDIR /src
COPY . .
RUN dotnet restore Monkeysphere.slnx --locked-mode -p:RuntimeFrameworkVersion=10.0.14
RUN dotnet publish src/Monkeysphere.Web/Monkeysphere.Web.csproj --configuration Release --runtime linux-x64 --self-contained false --no-restore --output /app/publish -p:RuntimeFrameworkVersion=10.0.14 -p:Version=$MONKEYSPHERE_VERSION -p:SourceRevisionId=$MONKEYSPHERE_REVISION \
    && find /app/publish -type f -name '*.pdb' -delete

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c AS runtime
ARG MONKEYSPHERE_VERSION=0.1.0-alpha.2
ARG MONKEYSPHERE_REVISION=unknown
LABEL org.opencontainers.image.title="Monkeysphere" \
      org.opencontainers.image.description="Private, self-hosted relationship memory" \
      org.opencontainers.image.source="https://github.com/Wixely/Monkeysphere" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="$MONKEYSPHERE_VERSION" \
      org.opencontainers.image.revision="$MONKEYSPHERE_REVISION"
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data && chown -R 1654:1654 /data
USER 1654
ENV ASPNETCORE_URLS=http://+:8080
ENV MONKEYSPHERE_DATA_ROOT=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Monkeysphere.Web.dll"]
