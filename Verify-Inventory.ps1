$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Full verification is restricted to an isolated CI environment. Do not run installation or registry tests on a daily-use computer.' }
$taskRoot = $PSScriptRoot
$logRoot = Join-Path $taskRoot ('logs\inventory-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $logRoot | Out-Null
$appProject = Join-Path $taskRoot 'app-inventory\AppInventory.csproj'
$appExecutable = Join-Path $taskRoot 'artifacts\inventory\AppInventory.exe'
$buildLog = Join-Path $logRoot 'build.log'
& dotnet build $appProject -c Release --nologo -v minimal *> $buildLog
$buildExit = $LASTEXITCODE
Get-Content -LiteralPath $buildLog
if ($buildExit -ne 0) { throw ('Application build failed: ' + $buildExit) }

foreach ($checkMode in @('--self-test', '--smoke-read')) {
    $resultPath = Join-Path $logRoot ($checkMode.Substring(2) + '.json')
    $testProcess = Start-Process -FilePath $appExecutable -ArgumentList @($checkMode, ('"' + $resultPath + '"')) -WindowStyle Hidden -Wait -PassThru
    $report = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($report.checks) { $report.checks | ForEach-Object { Write-Output ('[OK] ' + $_) } }
    else { Get-Content -LiteralPath $resultPath -Raw }
    if ($testProcess.ExitCode -ne 0) {
        $progressPath = $resultPath + '.progress.jsonl'
        if (Test-Path -LiteralPath $progressPath) { Get-Content -LiteralPath $progressPath -Tail 12 }
        Get-Content -LiteralPath $resultPath -Raw
        throw ('Check failed: ' + $checkMode + ', exit=' + $testProcess.ExitCode)
    }
}
Write-Output ('[OK] Logs: ' + $logRoot)
