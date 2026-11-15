# The sandbox's logon command: installs Nagi, captures the screenshots, then shuts the sandbox down.
$out = 'C:\Harness\.work\out'
Start-Transcript "$out\logon.log"
try {
    . C:\Harness\setup.ps1
    . C:\Harness\ui.ps1
    . C:\Harness\capture.ps1
} catch {
    "FAILED: $_`n$($_.ScriptStackTrace)"
    Save-Screen "$out\failure.png"
}
Copy-Item "$env:LOCALAPPDATA\Packages\$((Get-AppxPackage 48743Nagi.Nagi).PackageFamilyName)\LocalState\Logs" "$out\logs" -Recurse -ErrorAction SilentlyContinue
Stop-Transcript
'done' | Set-Content "$out\done"
shutdown /s /t 0
