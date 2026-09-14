# 遵循 rule-docker：測試專用 Dockerfile，主機本地不直跑編譯與測試
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS testrunner
WORKDIR /src

# 複製專案檔還原依賴
COPY uvc-core/uvc-core.csproj uvc-core/
COPY uvc-server/web-api/uvc-server.csproj uvc-server/web-api/
COPY uvc-server/tests/uvc-server-tests.csproj uvc-server/tests/

RUN dotnet restore uvc-server/tests/uvc-server-tests.csproj

# 複製完整代碼
COPY uvc-core/ uvc-core/
COPY uvc-server/web-api/ uvc-server/web-api/
COPY uvc-server/tests/ uvc-server/tests/

WORKDIR /src/uvc-server/tests

# 於 Docker 內部執行 xUnit 契約測試
ENTRYPOINT ["dotnet", "test", "-c", "Release", "--logger", "console;verbosity=normal"]
