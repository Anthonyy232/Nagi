# Makes the self-signed test certificate the sandbox package is signed with, as files only: no certificate store is touched.
# The subject is the manifest's Publisher, so the package builds unchanged.
param([Parameter(Mandatory)] [string] $Manifest, [Parameter(Mandatory)] [string] $OutDir)

$publisher = ([xml](Get-Content $Manifest -Raw)).Package.Identity.Publisher
$pfx = Join-Path $OutDir 'nagi-test.pfx'
if ((Test-Path $pfx) -and ((Get-Content (Join-Path $OutDir 'nagi-test.subject') -ErrorAction SilentlyContinue) -eq $publisher)) { return }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$rsa = [System.Security.Cryptography.RSA]::Create(2048)
$req = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new($publisher, $rsa,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$eku = [System.Security.Cryptography.OidCollection]::new()
$eku.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
$req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
$req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$cert = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddYears(2))

[IO.File]::WriteAllBytes($pfx, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx))
[IO.File]::WriteAllBytes((Join-Path $OutDir 'nagi-test.cer'), $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
Set-Content (Join-Path $OutDir 'nagi-test.subject') $publisher
