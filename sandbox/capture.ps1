# Inside the sandbox, after setup.ps1: sets Nagi up on the demo library and saves the README screenshots to C:\Harness\.work\out\shots.
# Run from logon.ps1; ui.ps1 must be dot-sourced first.
$shots = 'C:\Harness\.work\out\shots'
New-Item -ItemType Directory -Force $shots | Out-Null

function Go([string] $Page) { Invoke-El (Find-El $w -Name $Page -Type ListItem); Start-Sleep 2 }
function Toggle([string] $Name, [bool] $On) {
    $t = (Find-El $w -Name $Name -Type Button).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (($t.Current.ToggleState -eq 'On') -ne $On) { $t.Toggle() }
}
function Seek([int] $Seconds) { (Find-El $w -Id MediaSeekerSlider).GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue($Seconds) }
# real clicks: a button invoked through UI Automation takes keyboard focus and shows its tooltip
function Press([string] $Name) { Click-El (Find-El $w -Name $Name -Type Button); Start-Sleep 1 }
function Pause { Press 'Pause' }
function Open-Album([string] $Album) { Go 'Albums'; Invoke-El (Find-Prefix $w $Album); Start-Sleep 2 }
function Play-Song([string] $Album, [string] $Song) { Open-Album $Album; Click-El (Find-El $w -Name $Song -Id SongTitle) -Double; Start-Sleep 2 }

# Frame sizes are physical pixels at 125%, as the inputs table in the README gives them.
function Size([int] $Width, [int] $Height) { Set-WindowFrame $w 20 20 $Width $Height; Start-Sleep 1 }
function Shot([string] $Name, [int] $X, [int] $Y, [int] $Width, [int] $Height) {
    Park-Pointer
    # artwork decodes after a resize or page change
    Start-Sleep 4
    $f = Get-Frame $w
    Save-Shot "$shots\$Name.png" ($f.L + $X) ($f.T + $Y) $Width $Height
}
# Right of the navigation pane and short of the window border, below the title bar, down to the top of the player bar's seek line.
function Shot-Page([string] $Name) { Shot $Name 321 41 1023 751 }
function Shot-Player([string] $Name) {
    $top = [int](Find-El $w -Name 'Go to Album' -Type Button).Current.BoundingRectangle.Top - (Get-Frame $w).T
    Shot $Name 321 ($top - 21) 1023 157
}

# First run: add the library and give it a month of listening. Seeded while Nagi runs: stopping it this early
# leaves the song list without artwork on the next start.
. C:\Harness\onboard.ps1
$w = Get-AppWindow
. C:\Harness\seed.ps1
Go 'Settings'
Toggle 'Insights' $true
Toggle 'Collapsible Player' $false

foreach ($theme in 'Light', 'Dark') {
    $t = $theme.ToLower()
    Go 'Settings'
    $combo = Find-El $w -Name 'Theme' -Type ComboBox
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    (Find-El $w -Name $theme -Type ListItem).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep 2
    Size 1345 827

    Open-Album 'Still Water Sessions'
    Press 'Play all'
    Start-Sleep 2
    Seek 60
    Pause
    Go 'Library'
    Press 'Up Next'
    Size 1440 906
    Shot "library-$t" 0 0 1440 876
    Press 'Up Next'
    Size 1345 827
    Shot-Player "player-1-$t"
    Go 'Albums'
    Shot-Page "albums-$t"
    Go 'Insights'
    Shot-Page "insights-$t"

    # the current line only lights up while playing
    Play-Song 'Marigold Static' 'Marigold Static'
    Press 'Lyrics'
    Seek 23
    Start-Sleep 5
    Pause
    Shot-Page "lyrics-$t"
    Shot-Player "player-3-$t"

    Play-Song 'Token Machine' 'Token Machine'
    Seek 95
    Pause
    Shot-Player "player-2-$t"
}

# Not README inputs: the mini-player, for a look after dependency updates.
Go 'Settings'
Toggle 'Minimize to Mini-Player' $true
Invoke-El (Find-El $w -Name 'Minimize' -Type Button)
Park-Pointer
Start-Sleep 3
Save-Screen "$shots\..\mini-player.png"
