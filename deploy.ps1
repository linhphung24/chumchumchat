param(
    [string]$VpsIp = "103.163.214.101",
    [string]$VpsUser = "root",
    [string]$TargetDir = "/var/www/chumchat"
)

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "      CHUMCHAT DEPLOYMENT SCRIPT          " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "App Target: ${VpsUser}@${VpsIp}:${TargetDir}"

Write-Host "`n[1/4] Building and publishing .NET App for Linux (Self-contained)..." -ForegroundColor Yellow
dotnet publish src/ChumChat.Web/ChumChat.Web.csproj -c Release -r linux-x64 --self-contained -o ./publish
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed. Deployment aborted." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n[2/4] Removing config files to prevent overwriting VPS data..." -ForegroundColor Yellow
Remove-Item -Path .\publish\appsettings.json -ErrorAction SilentlyContinue
Remove-Item -Path .\publish\appsettings.Development.json -ErrorAction SilentlyContinue
Remove-Item -Path .\publish\chumchat.db -ErrorAction SilentlyContinue
Remove-Item -Path .\publish\chumchat.db-shm -ErrorAction SilentlyContinue
Remove-Item -Path .\publish\chumchat.db-wal -ErrorAction SilentlyContinue

Write-Host "`n[3/4] Uploading files to VPS via SCP..." -ForegroundColor Yellow
Write-Host ">>> Uploading .NET App... (Yêu cầu nhập password lần 1: Dừng service)" -ForegroundColor Yellow
ssh "${VpsUser}@${VpsIp}" "systemctl stop chumchat"

Write-Host ">>> Uploading .NET App... (Yêu cầu nhập password lần 2: Copy file)" -ForegroundColor Yellow
scp -r .\publish\* "${VpsUser}@${VpsIp}:${TargetDir}/"

Write-Host ">>> Uploading Sidecars (Zalo & Messenger)... (Yêu cầu nhập password lần 2)" -ForegroundColor Yellow
ssh "${VpsUser}@${VpsIp}" "mkdir -p ${TargetDir}/sidecars"
scp -r .\sidecars\* "${VpsUser}@${VpsIp}:${TargetDir}/sidecars/"

Write-Host "`n[4/4] Setting permissions and restarting services on VPS... (Yêu cầu nhập password lần 3)" -ForegroundColor Yellow
ssh "${VpsUser}@${VpsIp}" "chown -R chumchat:chumchat ${TargetDir} && chmod +x ${TargetDir}/ChumChat.Web && systemctl daemon-reload && systemctl restart chumchat"

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host " Deployment completed successfully!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
