param([Parameter(Mandatory=$true)][string]$Root,[Parameter(Mandatory=$true)][string]$Token,[ValidateSet('Build','Cleanup')][string]$Mode='Build')
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $Token -notmatch '^[a-f0-9]{32}$'){throw 'MSIX certificate fixture is restricted to isolated CI.'}
$taskIdentity=[Security.Principal.WindowsIdentity]::GetCurrent()
$taskPrincipal=New-Object Security.Principal.WindowsPrincipal($taskIdentity)
if(-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Isolated MSIX fixture requires an existing administrator token.'}
Import-Module Microsoft.PowerShell.Security -ErrorAction Stop
[void](Get-PSDrive -Name Cert -ErrorAction Stop)
$taskRoot=[IO.Path]::GetFullPath($Root)
if((Split-Path -Leaf $taskRoot) -ne $Token -or $taskRoot -notmatch '\\read-only-fixtures\\msix\\'){throw 'Invalid MSIX fixture directory.'}
$taskName='AiFixture.'+$Token
$taskSubject='CN='+$taskName
$taskDescriptor=Join-Path $taskRoot 'msix-fixture.json'
if($Mode -eq 'Cleanup'){
    if(Test-Path -LiteralPath $taskDescriptor){
        $taskRecord=Get-Content -LiteralPath $taskDescriptor -Raw -Encoding UTF8 | ConvertFrom-Json
        if($taskRecord.token -ne $Token -or $taskRecord.thumbprint -notmatch '^[A-Fa-f0-9]{40}$'){throw 'Fixture certificate identity mismatch.'}
        foreach($taskStore in @('Cert:\LocalMachine\TrustedPeople','Cert:\CurrentUser\My')){
            $taskCertificatePath=Join-Path $taskStore $taskRecord.thumbprint
            if(Test-Path -LiteralPath $taskCertificatePath){
                $taskCertificate=Get-Item -LiteralPath $taskCertificatePath
                if($taskCertificate.Subject -ne $taskSubject){throw 'Refusing to remove an unrelated certificate.'}
                if($taskStore -eq 'Cert:\CurrentUser\My'){Remove-Item -LiteralPath $taskCertificatePath -DeleteKey}
                else {Remove-Item -LiteralPath $taskCertificatePath}
            }
        }
    }
    exit 0
}
if(Test-Path -LiteralPath $taskDescriptor){throw 'Fixture descriptor already exists.'}
$taskSdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$taskMake=Get-ChildItem -Path (Join-Path $taskSdk '*\x64\makeappx.exe') -File | Sort-Object FullName -Descending | Select-Object -First 1
if(-not $taskMake){throw 'Windows SDK MakeAppx is not available in this CI image.'}
$taskSign=Join-Path $taskMake.DirectoryName 'signtool.exe'
if(-not (Test-Path -LiteralPath $taskSign)){throw 'Windows SDK SignTool is not available.'}
$taskPayload=Join-Path $taskRoot 'payload'
if(-not (Test-Path -LiteralPath (Join-Path $taskPayload 'TestApplication.exe'))){throw 'Dedicated package executable is missing.'}
$taskAssets=Join-Path $taskPayload 'Assets'
New-Item -ItemType Directory -Path $taskAssets | Out-Null
Add-Type -AssemblyName System.Drawing
foreach($taskSize in @(44,150)){
    $taskBitmap=New-Object Drawing.Bitmap($taskSize,$taskSize)
    $taskGraphics=[Drawing.Graphics]::FromImage($taskBitmap)
    try {$taskGraphics.Clear([Drawing.Color]::ForestGreen); $taskBitmap.Save((Join-Path $taskAssets ('Logo'+$taskSize+'.png')),[Drawing.Imaging.ImageFormat]::Png)}
    finally {$taskGraphics.Dispose(); $taskBitmap.Dispose()}
}
$taskManifest=@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap rescap">
  <Identity Name="$taskName" Publisher="$taskSubject" Version="1.0.0.0" ProcessorArchitecture="x64"/>
  <Properties><DisplayName>AppInventory Dedicated MSIX Fixture</DisplayName><PublisherDisplayName>AppInventory contributors</PublisherDisplayName><Logo>Assets\Logo150.png</Logo></Properties>
  <Resources><Resource Language="en-us"/></Resources>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0"/></Dependencies>
  <Applications><Application Id="Fixture" Executable="TestApplication.exe" EntryPoint="Windows.FullTrustApplication"><uap:VisualElements DisplayName="AppInventory Dedicated MSIX Fixture" Description="Dedicated isolated uninstall fixture" BackgroundColor="transparent" Square150x150Logo="Assets\Logo150.png" Square44x44Logo="Assets\Logo44.png"/></Application></Applications>
  <Capabilities><rescap:Capability Name="runFullTrust"/></Capabilities>
</Package>
"@
[IO.File]::WriteAllText((Join-Path $taskPayload 'AppxManifest.xml'),$taskManifest,(New-Object Text.UTF8Encoding($false)))
$taskPackage=Join-Path $taskRoot 'fixture.msix'
& $taskMake.FullName pack /d $taskPayload /p $taskPackage *> (Join-Path $taskRoot 'makeappx.log')
if($LASTEXITCODE -ne 0){throw ('MakeAppx failed: '+$LASTEXITCODE)}
$taskCertificate=New-SelfSignedCertificate -Type Custom -KeyUsage DigitalSignature -Subject $taskSubject -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}') -KeyExportPolicy Exportable -HashAlgorithm SHA256 -KeyLength 2048 -NotAfter (Get-Date).AddDays(2)
[ordered]@{token=$Token;name=$taskName;publisher=$taskSubject;thumbprint=$taskCertificate.Thumbprint;package=$taskPackage} | ConvertTo-Json -Depth 3 | Out-File -LiteralPath $taskDescriptor -Encoding utf8
$taskSecret=[Guid]::NewGuid().ToString('N')
$taskPassword=ConvertTo-SecureString -String $taskSecret -AsPlainText -Force
$taskPfx=Join-Path $taskRoot 'fixture-private.pfx'
Export-PfxCertificate -Cert $taskCertificate -FilePath $taskPfx -Password $taskPassword | Out-Null
$taskCer=Join-Path $taskRoot 'fixture-public.cer'
Export-Certificate -Cert $taskCertificate -FilePath $taskCer | Out-Null
Import-Certificate -FilePath $taskCer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
& $taskSign sign /q /fd SHA256 /f $taskPfx /p $taskSecret $taskPackage *> (Join-Path $taskRoot 'signtool.log')
if($LASTEXITCODE -ne 0){throw ('SignTool failed: '+$LASTEXITCODE)}
Write-Output '[OK] Dedicated MSIX fixture created in isolated CI.'
