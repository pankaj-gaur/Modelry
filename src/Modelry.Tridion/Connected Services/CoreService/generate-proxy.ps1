# Generates the Core Service client proxy (Reference.cs). Run from this folder.
# -WsdlUrl can be a URL (…/CoreService201701.svc?singleWsdl) or a saved .wsdl file.
# Tip (corporate proxy): download with the system proxy first, then generate from the file:
#   Invoke-WebRequest -Uri "<cm>/webservices/CoreService201701.svc?singleWsdl" -OutFile .\CoreService.wsdl -UseDefaultCredentials
#   .\generate-proxy.ps1 -WsdlUrl .\CoreService.wsdl
# Warnings about 'Message' security / SymmetricSecurityBindingElement / wsHttp being skipped are expected:
# .NET 8 cannot use message-security endpoints; the utility uses the basicHttp contract (ICoreService).
param([Parameter(Mandatory)][string]$WsdlUrl)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet-svcutil -ErrorAction SilentlyContinue)) {
    dotnet tool install --global dotnet-svcutil
}

dotnet-svcutil $WsdlUrl `
  --outputDir . --outputFile Reference.cs `
  --namespace "*,Tridion.ContentManager.CoreService.Client" `
  --targetFramework net8.0 --noLogo

if ($LASTEXITCODE -ne 0 -or -not (Test-Path .\Reference.cs)) {
    Write-Error "Proxy generation failed - Reference.cs was not created. See errors above."
    exit 1
}
if (-not (Select-String -Path .\Reference.cs -Pattern 'interface ICoreService\b' -Quiet)) {
    Write-Warning "ICoreService was not generated - the basicHttp endpoint may be missing from the WSDL. Share the WSDL port list with the developer."
}
Write-Host "Reference.cs generated. Rebuild the solution (CORESERVICE_PROXY is now defined)." -ForegroundColor Green
