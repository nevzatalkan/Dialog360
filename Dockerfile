FROM node:22-alpine AS web-build
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build -- --outDir dist-container

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY api/Diyalog360.Api.csproj api/
RUN dotnet restore api/Diyalog360.Api.csproj
COPY api/ api/
COPY --from=web-build /src/web/dist-container api/wwwroot/
RUN dotnet publish api/Diyalog360.Api.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "Diyalog360.Api.dll"]
