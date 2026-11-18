$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$hostProject = Join-Path $repository 'Grunt/Grunt.Gateway.Host/Surprenant.Grunt.Gateway.Host.csproj'
$hostAssembly = Join-Path $repository 'Grunt/Grunt.Gateway.Host/bin/Release/net10.0/Surprenant.Grunt.Gateway.Host.dll'
$output = Join-Path $repository 'Grunt/Grunt.Gateway.TypeScript/openapi.json'
$tokenPath = Join-Path ([System.IO.Path]::GetTempPath()) 'grunt-openapi-tokens.json'

Push-Location $repository
try {
    dotnet tool restore
    dotnet build $hostProject --configuration Release --no-restore
    $env:Gateway__Auth__TokenPath = $tokenPath
    $env:Gateway__Auth__UseInstanceLock = 'false'
    # The served document has no servers, so Swagger UI calls whichever host serves it. The published contract names the
    # hosted gateway, which becomes the generated TypeScript client's default base URL.
    dotnet swagger tofile --host https://grunt.grifball.xyz --output $output $hostAssembly v1

    # XML documentation preserves the build host's newline convention inside JSON string values. Canonicalize those
    # escaped newlines so the checked-in contract and generated TypeScript are identical on Windows and Linux.
    $openApi = [System.IO.File]::ReadAllText($output)
    [System.IO.File]::WriteAllText(
        $output,
        $openApi.Replace('\r\n', '\n'),
        [System.Text.UTF8Encoding]::new($false))
}
finally {
    Pop-Location
}
