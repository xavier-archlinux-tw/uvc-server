# 多階段建置 Dockerfile (遵循 rule-docker，整合建置期編譯檢查)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 拷貝核心庫與服務專案檔
COPY uvc-core/uvc-core.csproj uvc-core/
COPY uvc-server/web-api/uvc-server.csproj uvc-server/web-api/

# 還原依賴
RUN dotnet restore uvc-server/web-api/uvc-server.csproj

# 拷貝完整源碼並發布
COPY uvc-core/ uvc-core/
COPY uvc-server/web-api/ uvc-server/web-api/

WORKDIR /src/uvc-server/web-api
RUN dotnet publish -c Release -o /app/publish

# 運行映像檔
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# 注入 Workstation GC 記憶體限制環境變數，守護 Linode 1GB VPS 防線
ENV DOTNET_gcServer=0
ENV DOTNET_GCConserveMemory=9
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .

EXPOSE 5000

ENTRYPOINT ["dotnet", "Uvc.Server.dll"]
