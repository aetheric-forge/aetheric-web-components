FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
COPY docker/certs/*.crt /usr/local/share/ca-certificates/
RUN update-ca-certificates
WORKDIR /src
COPY . .
# The repository's local SDK pin has no published MCR image. Pin the container SDK independently.
RUN printf '%s\n' '{"sdk":{"version":"10.0.401","rollForward":"disable"}}' > global.json
RUN dotnet restore samples/Aetheric.Provisioning.Web/Aetheric.Provisioning.Web.csproj --locked-mode
RUN dotnet publish samples/Aetheric.Provisioning.Web/Aetheric.Provisioning.Web.csproj \
    --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
COPY docker/certs/*.crt /usr/local/share/ca-certificates/
RUN update-ca-certificates
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys /home/app/.local/share/aetheric/bootstrap /home/app/.local/share/aetheric/root-credentials /home/app/.local/share/aetheric/root-key \
    && chown -R app:app /home/app/.aspnet /home/app/.local
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Aetheric.Provisioning.Web.dll"]
