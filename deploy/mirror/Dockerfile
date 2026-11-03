# Bekommt die fertig gebaute App aus deploy/update-mirror.ps1.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN mkdir -p /data && chown "$APP_UID" /data
COPY app/ /app/
WORKDIR /app
USER $APP_UID
ENTRYPOINT ["dotnet", "SKx.Solar.dll"]
