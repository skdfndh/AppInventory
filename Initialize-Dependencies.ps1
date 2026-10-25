$ErrorActionPreference = 'Stop'
$upstreamRoot = Join-Path $PSScriptRoot 'upstream'
$patchPath = Join-Path $PSScriptRoot 'bcu-core.patch'
$revision = '30da609384c98ba6e35c6530129541ea4ff3970a'
$repository = 'https://github.com/Klocman/Bulk-Crap-Uninstaller.git'
if (-not (Test-Path -LiteralPath $patchPath)) { throw 'The checked-in BCU patch is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $upstreamRoot '.git'))) {
    if (Test-Path -LiteralPath $upstreamRoot) { throw 'The upstream directory already exists without Git metadata; refusing to overwrite it.' }
    & git init --quiet $upstreamRoot
    if ($LASTEXITCODE -ne 0) { throw 'BCU repository initialization failed.' }
    & git -C $upstreamRoot remote add origin $repository
    if ($LASTEXITCODE -ne 0) { throw 'BCU remote configuration failed.' }
    & git -C $upstreamRoot fetch --depth=1 origin $revision
    if ($LASTEXITCODE -ne 0) { throw 'BCU fetch failed. The partial directory has been retained for inspection.' }
    & git -C $upstreamRoot checkout --quiet --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'BCU checkout failed.' }
}
$headRevision = & git -C $upstreamRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $headRevision -ne $revision) { throw 'BCU revision does not match the approved baseline; existing files were not changed.' }
$expectedHash = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash
$diffPath = Join-Path ([IO.Path]::GetTempPath()) ('AppInventory-bcu-' + [Guid]::NewGuid().ToString('N') + '.patch')
try {
    & git -C $upstreamRoot diff --binary --no-ext-diff ('--output=' + $diffPath)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect BCU changes.' }
    if ((Get-FileHash -LiteralPath $diffPath -Algorithm SHA256).Hash -ne $expectedHash) {
        if ((Get-Item -LiteralPath $diffPath).Length -gt 0) { throw 'BCU contains different local changes; refusing to replace them.' }
        & git -C $upstreamRoot apply --check $patchPath
        if ($LASTEXITCODE -ne 0) { throw 'BCU patch validation failed; source files were not changed.' }
        & git -C $upstreamRoot apply --whitespace=nowarn $patchPath
        if ($LASTEXITCODE -ne 0) { throw 'BCU patch application failed.' }
        & git -C $upstreamRoot diff --binary --no-ext-diff ('--output=' + $diffPath)
        if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $diffPath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Applied BCU patch does not match the checked-in changes.' }
    }
} finally {
    if (Test-Path -LiteralPath $diffPath) { Remove-Item -LiteralPath $diffPath }
}
Write-Output ('[OK] BCU baseline and patch verified: ' + $revision)
