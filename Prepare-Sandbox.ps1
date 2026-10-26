param([Parameter(Mandatory = $true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$taskPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$taskChecksum = $taskPackage + '.sha256'
if (-not (Test-Path -LiteralPath $taskChecksum)) { throw 'Package checksum file is missing.' }
$taskExpected = (Get-Content -LiteralPath $taskChecksum -Raw).Split(' ')[0].Trim()
if ($taskExpected -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $taskPackage -Algorithm SHA256).Hash -ne $taskExpected) { throw 'Package checksum mismatch.' }
$taskSandboxRoot = Join-Path $PSScriptRoot ('artifacts\sandbox-check\' + [Guid]::NewGuid().ToString('N'))
$taskInput = Join-Path $taskSandboxRoot 'input'
$taskOutput = Join-Path $taskSandboxRoot 'results'
New-Item -ItemType Directory -Path $taskInput, $taskOutput | Out-Null
Copy-Item -LiteralPath $taskPackage -Destination (Join-Path $taskInput 'package.zip')
[IO.File]::WriteAllText((Join-Path $taskInput 'expected-sha256.txt'), $taskExpected)
$taskValidation = @'
$ErrorActionPreference = 'Stop'
$resultRoot = 'C:\AppInventoryResults'
try {
    $expected = (Get-Content -LiteralPath 'C:\AppInventoryInput\expected-sha256.txt' -Raw).Trim()
    $actual = (Get-FileHash -LiteralPath 'C:\AppInventoryInput\package.zip' -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw 'Package checksum mismatch inside Sandbox.' }
    $expanded = 'C:\AppInventoryValidation'
    Expand-Archive -LiteralPath 'C:\AppInventoryInput\package.zip' -DestinationPath $expanded
    $folders = @(Get-ChildItem -LiteralPath $expanded -Directory)
    if ($folders.Count -ne 1) { throw 'Expected one application directory.' }
    $executable = Join-Path $folders[0].FullName 'AppInventory.exe'
    if (-not (Test-Path -LiteralPath $executable)) { throw 'Application executable is missing.' }
    $reportPath = Join-Path $resultRoot 'self-test.json'
    $process = Start-Process -FilePath $executable -ArgumentList @('--self-test', ('"' + $reportPath + '"')) -WindowStyle Hidden -Wait -PassThru
    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $report.error) { throw ('Sandbox self-test failed: exit=' + $process.ExitCode) }
    [ordered]@{ completed = $true; passed = $report.passed; packageSha256 = $actual; checkedAt = [DateTimeOffset]::UtcNow;
        scope = 'Dedicated EXE/MSI fixtures only; interactive UAC, Store, reboot and registry residue writes remain separate checks.' } |
        ConvertTo-Json -Depth 4 | Out-File -LiteralPath (Join-Path $resultRoot 'sandbox-summary.json') -Encoding utf8
} catch {
    $_ | Out-String | Out-File -LiteralPath (Join-Path $resultRoot 'sandbox-error.txt') -Encoding utf8
    exit 1
}
'@
[IO.File]::WriteAllText((Join-Path $taskInput 'Validate-InSandbox.ps1'), $taskValidation)
$taskInputXml = [Security.SecurityElement]::Escape($taskInput)
$taskOutputXml = [Security.SecurityElement]::Escape($taskOutput)
$taskConfiguration = @"
<Configuration>
  <vGPU>Disable</vGPU><Networking>Disable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection><AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$taskInputXml</HostFolder><SandboxFolder>C:\AppInventoryInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$taskOutputXml</HostFolder><SandboxFolder>C:\AppInventoryResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\AppInventoryInput\Validate-InSandbox.ps1</Command></LogonCommand>
</Configuration>
"@
$taskWsb = Join-Path $taskSandboxRoot 'AppInventory-Validation.wsb'
[IO.File]::WriteAllText($taskWsb, $taskConfiguration)
Write-Output ('[OK] Prepared, not launched: ' + $taskWsb)
Write-Output ('[OK] Results directory: ' + $taskOutput)
Write-Output '[INFO] Requires a Windows edition with Windows Sandbox enabled. No host features were changed.'
