param([switch]$RunFixtureChecks)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$logRoot = Join-Path $taskRoot ('logs\verify-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $logRoot | Out-Null
Push-Location $taskRoot
try {
    & dotnet build 'upstream\source\UninstallTools\UninstallTools.csproj' -c Release --nologo -v minimal '-p:OutputPath=..\..\..\artifacts\core\' *> (Join-Path $logRoot 'core-build.log')
    $buildExit = $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $logRoot 'core-build.log')
    if ($buildExit -ne 0) { throw ('Core build failed: ' + $buildExit) }

    $coreOutput = Join-Path $taskRoot 'artifacts\core'
    if (Test-Path -LiteralPath (Join-Path $coreOutput 'ObjectListView.dll')) {
        throw 'Unexpected ObjectListView.dll in core output.'
    }

    if ($RunFixtureChecks) {
        & dotnet build 'probe\CoreProbe.csproj' -c Release --nologo -v minimal *> (Join-Path $logRoot 'probe-build.log')
        $probeBuildExit = $LASTEXITCODE
        Get-Content -LiteralPath (Join-Path $logRoot 'probe-build.log')
        if ($probeBuildExit -ne 0) { throw ('Probe build failed: ' + $probeBuildExit) }

        & (Join-Path $taskRoot 'artifacts\probe\CoreProbe.exe') *> (Join-Path $logRoot 'probe-run.log')
        $probeExit = $LASTEXITCODE
        Get-Content -LiteralPath (Join-Path $logRoot 'probe-run.log')
        if ($probeExit -ne 0) { throw ('Fixture checks failed: ' + $probeExit) }
    }

    & git -C (Join-Path $taskRoot 'upstream') -c core.whitespace=blank-at-eol,blank-at-eof,space-before-tab,cr-at-eol diff --check *> (Join-Path $logRoot 'diff-check.log')
    $diffExit = $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $logRoot 'diff-check.log')
    if ($diffExit -ne 0) { throw ('Diff check failed: ' + $diffExit) }
    Write-Output ('[OK] Logs: ' + $logRoot)
}
finally {
    Pop-Location
}
