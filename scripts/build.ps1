param([string]$Output = 'work/publish')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$localDotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnet = if(Test-Path -LiteralPath $localDotnet){$localDotnet}else{'dotnet'}
& $dotnet publish src/DatabaseResultExporter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $Output
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
