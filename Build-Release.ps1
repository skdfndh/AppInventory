param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$releaseStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$releaseRoot = Join-Path $PSScriptRoot ('artifacts\release\' + $releaseStamp)
$projectPath = Join-Path $PSScriptRoot 'app-inventory\AppInventory.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
$version = $projectXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d+\.\d+\.\d+([-.][A-Za-z0-9.-]+)?$') { throw 'Invalid application version.' }
$packageName = if ($FrameworkDependent) { 'AppInventory-' + $version + '-win-x64-framework-dependent' } else { 'AppInventory-' + $version + '-win-x64' }
$publishRoot = Join-Path $releaseRoot $packageName
$logRoot = Join-Path $PSScriptRoot 'logs'
$publishLog = Join-Path $logRoot ('publish-' + $releaseStamp + '.log')
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& dotnet publish $projectPath -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $publishRoot --nologo -v minimal *> $publishLog
$publishExit = $LASTEXITCODE
Get-Content -LiteralPath $publishLog
if ($publishExit -ne 0) { throw ('Publish failed: ' + $publishExit) }
$assetsPath = Join-Path $PSScriptRoot 'app-inventory\obj\project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
$packagePaths = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Value.path })
if (-not $FrameworkDependent) {
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishRoot 'AppInventory.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $runtimeId = $framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version
        $packagePaths += $runtimeId
    }
}
foreach ($packageRelativePath in ($packagePaths | Select-Object -Unique)) {
    $sourceRoot = $null
    foreach ($packageFolder in $packageFolders) {
        $candidateRoot = Join-Path $packageFolder $packageRelativePath
        if (Test-Path -LiteralPath $candidateRoot) { $sourceRoot = $candidateRoot; break }
    }
    if (-not $sourceRoot) { throw ('Package notices source not found: ' + $packageRelativePath) }
    $noticeRoot = Join-Path $publishRoot ('third-party\' + $packageRelativePath.Replace('/', '\'))
    New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object { $_.Name -match '(?i)^(license|licence|notice|third.party)' -or $_.Extension -eq '.nuspec' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $noticeRoot $_.Name) }
}
$packagePath = Join-Path $releaseRoot ($packageName + '.zip')
Compress-Archive -LiteralPath $publishRoot -DestinationPath $packagePath -CompressionLevel Optimal
$packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($packagePath + '.sha256'), ($packageHash + '  ' + [IO.Path]::GetFileName($packagePath) + [Environment]::NewLine))
Write-Output ('[OK] Package: ' + $packagePath)
Write-Output ('[OK] App: ' + (Join-Path $publishRoot 'AppInventory.exe'))
Write-Output ('[OK] SHA256: ' + $packageHash)
