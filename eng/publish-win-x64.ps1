param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts\publish\win-x64"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repositoryRoot $OutputRoot
$zipPath = Join-Path (Split-Path -Parent $publishDirectory) "AgentKick75-win-x64.zip"

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

$commonArguments = @(
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:NuGetAudit=false",
    "-m:1",
    "-o", $publishDirectory
)

& dotnet publish (Join-Path $repositoryRoot "src\windows\AgentKick75.App\AgentKick75.App.csproj") @commonArguments
if ($LASTEXITCODE -ne 0) {
    throw "AgentKick75.App publish failed with exit code $LASTEXITCODE."
}

& dotnet publish (Join-Path $repositoryRoot "src\windows\AgentKick75.Hook\AgentKick75.Hook.csproj") @commonArguments
if ($LASTEXITCODE -ne 0) {
    throw "AgentKick75.Hook publish failed with exit code $LASTEXITCODE."
}

foreach ($requiredFile in @("AgentKick75.exe", "AgentKick75.Hook.exe")) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $requiredFile))) {
        throw "Publish output is missing $requiredFile."
    }
}

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $zipPath -Force
Write-Host "Published: $publishDirectory"
Write-Host "Archive:   $zipPath"
