param([switch]$Development)
$ErrorActionPreference = 'Stop'
$appProject = Join-Path $PSScriptRoot 'app-inventory\AppInventory.csproj'
$appExecutable = Join-Path $PSScriptRoot 'artifacts\inventory\AppInventory.exe'
$releaseRoot = Join-Path $PSScriptRoot 'artifacts\release'
if ((-not $Development) -and (Test-Path -LiteralPath $releaseRoot)) {
    $releaseExecutable = Get-ChildItem -LiteralPath $releaseRoot -Directory | Sort-Object Name -Descending | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Directory | Where-Object { $_.Name -like 'AppInventory-*-win-x64' } | ForEach-Object {
            $candidateExecutable = Join-Path $_.FullName 'AppInventory.exe'
            if ((Test-Path -LiteralPath $candidateExecutable) -and (Test-Path -LiteralPath ($_.FullName + '.zip.sha256'))) { $candidateExecutable }
        }
    } | Select-Object -First 1
    if ($releaseExecutable) { $appExecutable = $releaseExecutable }
}
if (-not (Test-Path -LiteralPath $appExecutable)) {
    & dotnet build $appProject -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
}
Start-Process -FilePath $appExecutable -WindowStyle Normal
