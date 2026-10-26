param([Parameter(Mandatory = $true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Full package verification is restricted to an isolated CI environment. Do not run installation or registry tests on a daily-use computer.' }
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$packageHash = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash
$checksumPath = $resolvedPackage + '.sha256'
if (-not (Test-Path -LiteralPath $checksumPath)) { throw 'Package checksum file is missing.' }
$expectedHash = (Get-Content -LiteralPath $checksumPath -Raw).Split(' ')[0].Trim()
if ($expectedHash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Package checksum format is invalid.' }
if ($packageHash -ne $expectedHash) { throw 'Package checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($resolvedPackage)
try {
    $unexpected = @($archive.Entries | Where-Object { $_.FullName -match '(?i)(^|/)(settings\.json|snapshot\.json|task\.bin|uninstall-tasks|read-only-fixtures|portable-fixtures|logs)(/|$)' -or $_.FullName -match '(?i)(^|/)\.AppInventory-Quarantine-|\.(pfx|p12|pem|key|log|csv)$' })
    if ($unexpected.Count -gt 0) { throw 'Package contains local data or test fixtures.' }
} finally { $archive.Dispose() }
$qaRoot = Join-Path $PSScriptRoot ('artifacts\package-check\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $qaRoot
$packageFolder = [IO.Path]::GetFileNameWithoutExtension($resolvedPackage)
$appRoot = Join-Path $qaRoot $packageFolder
foreach ($required in @('AppInventory.exe', 'AppInventory.runtimeconfig.json', 'LICENSE.txt', '使用说明.md', '第三方声明\BCU-Licence.txt', '第三方声明\BCU-NOTICE.txt', '第三方声明\修改说明.md', '第三方声明\TaskScheduler-LICENSE.md', '测试组件\TestApplication.exe', '测试组件\RegisteredUninstaller.exe', '测试组件\Create-MsixFixture.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appRoot $required))) { throw ('Missing packaged file: ' + $required) }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $appRoot 'AppInventory.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.includedFrameworks) {
    if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'coreclr.dll'))) { throw 'Self-contained runtime is missing.' }
    foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
        $noticeRoot = Join-Path $appRoot ('third-party\' + $framework.name.ToLowerInvariant() + '.runtime.win-x64\' + $framework.version)
        if (-not (Get-ChildItem -LiteralPath $noticeRoot -File | Where-Object { $_.Name -match '(?i)^license' })) { throw ('Runtime license is missing: ' + $framework.name) }
    }
}
$appExecutable = Join-Path $appRoot 'AppInventory.exe'
$packageVersion = [regex]::Match($packageFolder, '^AppInventory-(.+)-win-x64(?:-framework-dependent)?$').Groups[1].Value
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $appRoot 'AppInventory.dll')).ProductVersion.Split('+')[0]
if (-not $packageVersion -or $binaryVersion -ne $packageVersion) { throw 'Packaged binary version does not match the package name.' }
$selfTestCount = 0
$installedCount = 0
$warningCount = 0
foreach ($checkMode in @('--self-test', '--smoke-read')) {
    $resultPath = Join-Path $qaRoot ($checkMode.Substring(2) + '.json')
    $checkProcess = Start-Process -FilePath $appExecutable -ArgumentList @($checkMode, ('"' + $resultPath + '"')) -WindowStyle Hidden -Wait -PassThru
    $report = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($checkProcess.ExitCode -ne 0 -or $report.error) {
        Get-Content -LiteralPath $resultPath -Raw
        throw ('Packaged check failed: ' + $checkMode + ', exit=' + $checkProcess.ExitCode)
    }
    if ($checkMode -eq '--self-test') {
        if ($report.passed -le 0 -or @($report.checks).Count -ne $report.passed) { throw 'Packaged self-test report is incomplete.' }
        $selfTestCount = $report.passed
        Write-Output ('[OK] Packaged checks passed: ' + $selfTestCount)
    } else {
        if ($report.count -le 0 -or $report.deletionHandlerConfigured -ne $false) { throw 'Read-only catalog verification failed.' }
        $installedCount = $report.count
        $warningCount = @($report.warnings).Count
        Get-Content -LiteralPath $resultPath -Raw
    }
}
$summary = [ordered]@{ schema = 1; version = $binaryVersion; packageSha256 = $packageHash; checkedAt = [DateTimeOffset]::UtcNow;
    passed = $selfTestCount; installed = $installedCount; warnings = $warningCount; deletionHandlerConfigured = $false }
$summary | ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $qaRoot 'verification.json') -Encoding utf8
Write-Output ('[OK] Package verified: ' + $resolvedPackage)
Write-Output ('[OK] SHA256: ' + $packageHash)
Write-Output ('[OK] QA directory: ' + $qaRoot)
