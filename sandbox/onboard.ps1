# Starts Nagi for the first time, adds C:\Music through the folder picker and skips the rest of setup.
$fam = (Get-AppxPackage 48743Nagi.Nagi).PackageFamilyName
Start-Process "shell:AppsFolder\$fam!App"
$w = Get-AppWindow
Invoke-El (Find-El $w -Name 'Get Started' -Timeout 60)
Invoke-El (Find-El $w -Name 'Add Music Folder' -Type Button)
$dlg = Find-El $w -Type Window
Click-El (Find-El $dlg -Id 1152)
Send-Keys 'C:\Music'
Invoke-El (Find-El $dlg -Id 1)
Find-El $w -Name 'Your library is ready.' -Timeout 120 | Out-Null
Invoke-El (Find-El $w -Name 'Skip Setup')
Invoke-El (Find-El $w -Name 'Start Listening')
Find-El $w -Id SongsListView -Timeout 30 | Out-Null
