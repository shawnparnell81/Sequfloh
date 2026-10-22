# Stage 1: build the web app
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# Stage 2: publish the API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/Sequfloh.Api/Sequfloh.Api.csproj src/Sequfloh.Api/
RUN dotnet restore src/Sequfloh.Api/Sequfloh.Api.csproj
COPY src/ src/
RUN dotnet publish src/Sequfloh.Api/Sequfloh.Api.csproj -c Release -o /app --no-restore
COPY --from=web /web/dist /app/wwwroot

# Stage 3: runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_TieredPGO=1 \
    DOTNET_gcServer=0 \
    STORAGE_LOCAL_PATH=/app/data/files \
    PORT=8080
RUN mkdir -p /app/data/files && chown -R app:app /app/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sequfloh.Api.dll"]
