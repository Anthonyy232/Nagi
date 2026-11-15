# Inside the sandbox: trusts the test certificate, allows sideloading, installs the package and copies in the demo library.
$work = 'C:\Harness\.work'
reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1 /f | Out-Null
Import-Certificate -FilePath "$work\nagi-test.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
# installs from a local copy; the mapped folder is a network path
Copy-Item "$work\Nagi.msix" C:\Nagi.msix
Add-AppxPackage C:\Nagi.msix
Copy-Item "$work\library" C:\Music -Recurse
