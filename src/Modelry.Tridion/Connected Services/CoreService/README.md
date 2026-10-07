# Core Service proxy

`Reference.cs` is generated, not committed. Run `generate-proxy.ps1` (or the bash equivalent below) once.

```bash
dotnet tool install --global dotnet-svcutil
dotnet-svcutil "https://{cms-host}/webservices/CoreService201701.svc?singleWsdl" \
  --outputDir . --outputFile Reference.cs \
  --namespace "*,Tridion.ContentManager.CoreService.Client" --targetFramework net8.0
```

If the WSDL requires authentication, download it in a browser (`?singleWsdl`) and pass the local file path instead.
When `Reference.cs` exists, the project defines `CORESERVICE_PROXY` and compiles `CoreServiceTridionGateway`.
