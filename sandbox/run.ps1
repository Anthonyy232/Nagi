# On the host: builds Nagi from a checkout, runs it in Windows Sandbox on the demo library and collects the screenshots.
#   powershell -ExecutionPolicy Bypass -File sandbox\run.ps1 <nagi-checkout> [-Inputs]
# -Inputs copies the screenshots over readme\inputs. Nothing is installed on the host.
param([Parameter(Mandatory)] [string] $Nagi, [switch] $Inputs, [int] $TimeoutMinutes = 25)
$ErrorActionPreference = 'Stop'
$Nagi = (Resolve-Path $Nagi).Path
$here = $PSScriptRoot
$work = Join-Path $here '.work'
$out = Join-Path $work 'out'

if (Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) { throw 'Windows Sandbox is already running; only one can run at a time.' }

& "$here\cert.ps1" -Manifest "$Nagi\src\Nagi.WinUI\Package.appxmanifest" -OutDir $work
if (-not (Test-Path "$work\library")) { node "$here\library.cjs" "$work\library"; if ($LASTEXITCODE) { throw 'library.cjs failed' } }

# x64 Release package signed with the test certificate; no appsettings.json, so no online keys go in.
$pkg = Join-Path $work 'pkg'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
dotnet publish "$Nagi\src\Nagi.WinUI\Nagi.WinUI.csproj" -c Release -r win-x64 -p:Platform=x64 -nologo -v:q `
    -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:AppxPackageSigningEnabled=true -p:PackageCertificateKeyFile="$work\nagi-test.pfx" -p:PackageCertificatePassword= `
    -p:AppxPackageDir="$pkg\\"
if ($LASTEXITCODE) { throw 'Build failed' }
Copy-Item (Get-ChildItem $pkg -Recurse -Filter *.msix | Select-Object -First 1).FullName "$work\Nagi.msix" -Force

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null
@"
<Configuration>
  <vGPU>Enable</vGPU>
  <Networking>Disable</Networking>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$here</HostFolder><SandboxFolder>C:\Harness</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -ExecutionPolicy Bypass -File C:\Harness\logon.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content "$work\nagi.wsb" -Encoding UTF8
Start-Process WindowsSandbox.exe "$work\nagi.wsb"

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class SandboxWindow {
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int ht, uint f);
  // The sandbox stops drawing while its window is minimized, which stalls the app; bring it back unfocused and behind the rest.
  public static void Unminimize(IntPtr h) { if (IsIconic(h)) { ShowWindow(h, 4); SetWindowPos(h, (IntPtr)1, 0, 0, 0, 0, 0x13); } }
}
'@
$end = (Get-Date).AddMinutes($TimeoutMinutes)
while (-not (Test-Path "$out\done")) {
    if ((Get-Date) -gt $end) { throw "No result after $TimeoutMinutes minutes; see $out\logon.log" }
    Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | ForEach-Object { [SandboxWindow]::Unminimize($_.MainWindowHandle) }
    Start-Sleep 2
}
# the sandbox shuts itself down, but its client window can linger
Start-Sleep 20
Get-Process WindowsSandboxRemoteSession, WindowsSandboxServer -ErrorAction SilentlyContinue | Stop-Process -Force
while (Get-Process vmmemWindowsSandbox -ErrorAction SilentlyContinue) { Start-Sleep 2 }

if (Select-String -Path "$out\logon.log" -Pattern '^FAILED' -Quiet) { throw "Capture failed; see $out\logon.log and $out\failure.png" }
Write-Host "Screenshots in $out\shots"
if ($Inputs) { Copy-Item "$out\shots\*.png" (Join-Path $here '..\readme\inputs') -Force; Write-Host 'Copied over readme\inputs' }
