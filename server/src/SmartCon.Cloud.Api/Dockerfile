# SPDX-License-Identifier: Business Source License 1.1 (see server/LICENSE); converts to Apache-2.0 after Change Date
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# server/global.json пинит SDK 10.0.101 (host); в контейнере образ sdk:10.0
# даёт свой (другой feature band) — пин несовместим, убираем.
RUN rm -f global.json
RUN dotnet restore src/SmartCon.Cloud.Api/SmartCon.Cloud.Api.csproj
RUN dotnet publish src/SmartCon.Cloud.Api/SmartCon.Cloud.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartCon.Cloud.Api.dll"]
