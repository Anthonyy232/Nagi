# UI Automation, input and screen-capture helpers for driving Nagi inside the sandbox. Dot-source it.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint f);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static RECT Bounds(IntPtr h) { RECT r; DwmGetWindowAttribute(h, 9, out r, 16); return r; }
}
'@
[Win]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null

$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function Get-AppWindow([string] $Process = 'Nagi', [int] $Timeout = 60) {
    $end = (Get-Date).AddSeconds($Timeout)
    while ((Get-Date) -lt $end) {
        $p = Get-Process $Process -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
        if ($p) { return $A::FromHandle($p.MainWindowHandle) }
        Start-Sleep -Milliseconds 250
    }
    throw "No $Process window"
}

function Find-El($Root, [string] $Name, [string] $Id, [string] $Type, [int] $Timeout = 15, [switch] $All) {
    $conds = @()
    if ($Name) { $conds += [System.Windows.Automation.PropertyCondition]::new($A::NameProperty, $Name) }
    if ($Id) { $conds += [System.Windows.Automation.PropertyCondition]::new($A::AutomationIdProperty, $Id) }
    if ($Type) { $conds += [System.Windows.Automation.PropertyCondition]::new($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::$Type) }
    $cond = if ($conds.Count -eq 1) { $conds[0] } else { [System.Windows.Automation.AndCondition]::new([System.Windows.Automation.Condition[]]$conds) }
    $end = (Get-Date).AddSeconds($Timeout)
    do {
        $r = if ($All) { $Root.FindAll($Scope::Descendants, $cond) } else { $Root.FindFirst($Scope::Descendants, $cond) }
        if ($r -and (-not $All -or $r.Count)) { return $r }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $end)
    if (-not $All) { throw "Not found: name '$Name' id '$Id' type '$Type'" }
}

function Show-Tree($El, [int] $Depth = 6, [string] $Indent = '') {
    $c = $El.Current
    $r = $c.BoundingRectangle
    $at = if ($r.IsEmpty) { '-' } else { "$([math]::Round($r.X)),$([math]::Round($r.Y)) $([math]::Round($r.Width))x$([math]::Round($r.Height))" }
    "$Indent$($c.ControlType.ProgrammaticName -replace 'ControlType\.') '$($c.Name)' id=$($c.AutomationId) cls=$($c.ClassName) [$at]"
    if ($Depth -le 0) { return }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($El)
    while ($child) { Show-Tree $child ($Depth - 1) "$Indent  "; $child = $walker.GetNextSibling($child) }
}

function Invoke-El($El) {
    $pat = $null
    if ($El.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke() }
    elseif ($El.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() }
    elseif ($El.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { $pat.Toggle() }
    elseif ($El.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pat)) { $pat.Expand() }
    else { Click-El $El }
}

function Click-At([int] $X, [int] $Y, [switch] $Double) {
    [Win]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 80
    1..($(if ($Double) { 2 } else { 1 })) | ForEach-Object { [Win]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); [Win]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero) }
    Start-Sleep -Milliseconds 150
    Park-Pointer
}

# Rests the pointer on the empty middle of Nagi's title bar, so nothing under it shows a hover state or tooltip.
# Moving it off the window instead leaves the last hovered item highlighted.
function Park-Pointer {
    $p = Get-Process Nagi -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
    if (-not $p) { return }
    $b = [Win]::Bounds($p.MainWindowHandle)
    [Win]::SetCursorPos($b.L + 700, $b.T + 15) | Out-Null
    [Win]::mouse_event(1, 1, 0, 0, [IntPtr]::Zero)
    [Win]::mouse_event(1, -1, 0, 0, [IntPtr]::Zero)
}

function Click-El($El, [switch] $Double) {
    $r = $El.Current.BoundingRectangle
    Click-At ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2)) -Double:$Double
}

function Send-Keys([string] $Keys) { [System.Windows.Forms.SendKeys]::SendWait($Keys) }

# Moves and sizes the window so its visible frame (without the invisible resize border) is exactly WxH at X,Y.
function Set-WindowFrame($Window, [int] $X, [int] $Y, [int] $W, [int] $H) {
    $hwnd = [IntPtr]$Window.Current.NativeWindowHandle
    [Win]::SetWindowPos($hwnd, [IntPtr]::Zero, $X, $Y, $W, $H, 0x14) | Out-Null
    Start-Sleep -Milliseconds 500
    $b = [Win]::Bounds($hwnd)
    [Win]::SetWindowPos($hwnd, [IntPtr]::Zero, 2 * $X - $b.L, 2 * $Y - $b.T, 2 * $W - ($b.R - $b.L), 2 * $H - ($b.B - $b.T), 0x14) | Out-Null
    Start-Sleep -Milliseconds 500
    [Win]::SetForegroundWindow($hwnd) | Out-Null
}

function Get-Frame($Window) { [Win]::Bounds([IntPtr]$Window.Current.NativeWindowHandle) }

function Save-Shot([string] $Path, [int] $X, [int] $Y, [int] $W, [int] $H) {
    $bmp = New-Object Drawing.Bitmap $W, $H
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($X, $Y, 0, 0, $bmp.Size)
    $bmp.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Save-Screen([string] $Path) {
    $s = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Save-Shot $Path 0 0 $s.Width $s.Height
}

# First element whose name starts with $Prefix.
function Find-Prefix($Root, [string] $Prefix, [string] $Type = 'ListItem', [int] $Timeout = 15) {
    $end = (Get-Date).AddSeconds($Timeout)
    do {
        $hit = Find-El $Root -Type $Type -All -Timeout 1 | Where-Object { $_.Current.Name.StartsWith($Prefix) } | Select-Object -First 1
        if ($hit) { return $hit }
    } while ((Get-Date) -lt $end)
    throw "Not found: '$Prefix*'"
}
