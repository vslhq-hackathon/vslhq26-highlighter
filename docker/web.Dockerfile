# Highlighter studio web app (Blazor Server).
# Build context is the repo root: docker build -f docker/web.Dockerfile .

# Tailwind CSS build — regenerates wwwroot/css/app.css from the sources so the
# image never ships a stale committed stylesheet.
FROM node:22-alpine AS css
WORKDIR /src/apps/web
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci
COPY apps/web/ .
RUN npm run css:build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY pipeline-dotnet/src/Highlighter.Pipeline/Highlighter.Pipeline.csproj pipeline-dotnet/src/Highlighter.Pipeline/
COPY apps/web/Highlighter.Web.csproj apps/web/
RUN dotnet restore apps/web
COPY pipeline-dotnet/src/Highlighter.Pipeline/ pipeline-dotnet/src/Highlighter.Pipeline/
COPY apps/web/ apps/web/
COPY --from=css /src/apps/web/wwwroot/css/app.css apps/web/wwwroot/css/app.css
RUN dotnet publish apps/web -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# Blazor Server auth state lives in DataProtection-encrypted browser storage;
# mount a volume at DataProtection:KeysDir (see Program.cs) to keep users
# signed in across container restarts.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Highlighter.Web.dll"]
