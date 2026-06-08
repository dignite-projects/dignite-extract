$currentFolder = $PSScriptRoot

$hostFolder = Join-Path $currentFolder "../../"
$angularFolder = Join-Path $currentFolder "../../../angular"
$certsFolder = Join-Path $currentFolder "certs"

# 1. 本地开发证书（Kestrel HTTPS）。密码需与 docker-compose.yml 中的
#    Kestrel__Certificates__Default__Password 保持一致。
If(!(Test-Path -Path $certsFolder))
{
    New-Item -ItemType Directory -Force -Path $certsFolder
    if(!(Test-Path -Path (Join-Path $certsFolder "localhost.pfx") -PathType Leaf)){
        Set-Location $certsFolder
        dotnet dev-certs https -v -ep localhost.pfx -p 91f91912-5ab0-49df-8166-23377efaf3cc -t
    }
}

# 2. 预构建后端产物 → host/src/bin/Release/net10.0/publish/
#    src/Dockerfile.local 只做轻量打包（COPY 预 publish 产物），不在容器内构建。
Set-Location $hostFolder
dotnet publish "src/Dignite.Paperbase.Host.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 3. 预构建前端产物 → angular/dist/host/browser/
#    apps/host/Dockerfile.local 只做 nginx 打包。
Set-Location $angularFolder
npx nx build host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. 重新打镜像并启动（--build 确保用上面刚产出的最新产物，而非旧镜像层）。
Set-Location $currentFolder
docker-compose up -d --build
exit $LASTEXITCODE
