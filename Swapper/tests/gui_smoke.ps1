# GUI smoke test: drives the real exe through UI Automation and captures the window (PrintWindow). The window opens
# behind every other window and never takes the focus (SWAPPER_TEST_BEHIND), so it can run while you work.
param(
    [string]$Template = $(throw "pass -Template <file.serps>"),
    [string]$Wheel = "Red Bull, 2026",
    [string]$Chassis = "Racing Bulls · Suzuka 26, 2026",
    [string]$Out = (Join-Path $PSScriptRoot "out\gui_test.serps"),
    [string]$Shot = (Join-Path $PSScriptRoot "out\gui_test.png"),
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\Swapper.App\bin\Release\net10.0-windows\win-x64\Swapper.exe")
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Win { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
public struct R { public int L,T,Rt,B; } }
"@
[Win]::SetProcessDPIAware() | Out-Null
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
Remove-Item $Out -ErrorAction SilentlyContinue
# the app's settings for this run live in a file of the test's own (the user's are never read or written)
$Settings = Join-Path (Split-Path $Out) "test_settings.json"
Remove-Item $Settings -ErrorAction SilentlyContinue
$env:SWAPPER_SETTINGS = $Settings
$env:SWAPPER_TEST_BEHIND = '1'
$p = Start-Process $Exe -ArgumentList "`"$Template`"" -PassThru
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$ct = [System.Windows.Automation.ControlType]
$win = $null
for ($i = 0; $i -lt 60 -and -not $win; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { $win = $A::FromHandle($p.MainWindowHandle) } }
function Cond($prop, $v) { New-Object System.Windows.Automation.PropertyCondition($prop, $v) }
function ById($id) { $win.FindFirst($T::Descendants, (Cond $A::AutomationIdProperty $id)) }
function ByName($name) { $win.FindFirst($T::Descendants, (Cond $A::NameProperty $name)) }
# chips are list items of the donor list only (WebView2's tree is not searched)
function Items($name) { @((ById "DonorBox").FindAll($T::Children, (Cond $A::ControlTypeProperty $ct::ListItem)) | Where-Object { $_.Current.Name -like "$name*" }) }
function Pick($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
function Aim($n) { (ByName "$n slot").GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 400 }
function Status { (ById "PreviewStatus").Current.Name }
function WaitPreview($what) {
    # the page says it is ready once drawn; its textures stream in for a few seconds after that
    for ($i = 0; $i -lt 240; $i++) { Start-Sleep -Milliseconds 500; $s = Status; if ($s -and $s -notlike "loading*") { Start-Sleep -Seconds 5; return $s } }
    "TIMEOUT ($what): $(Status)" }
function Shot($file) {
    $r = New-Object Win+R; [Win]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($file) }
$res = [ordered]@{}
try {
    # the game loads (the lists fill) and the template opens: its car in the preview
    for ($i = 0; $i -lt 120 -and (Items "Ferrari, 2026").Count -lt 1; $i++) { Start-Sleep -Milliseconds 500 }
    $res.template = WaitPreview "template"
    Shot $Shot
    # the app opens on the Chassis slot with nothing picked
    $res.opensOn = @("Chassis","Steering wheel","Rims","Suits") | Where-Object { (ByName "$_ slot").GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq "On" }
    # a wheel: the preview shows it built in (before / after)
    Aim "Steering wheel"
    Pick (Items $Wheel)[0]
    Start-Sleep -Milliseconds 300
    $res.wheel = WaitPreview "wheel"
    Shot $Shot.Replace(".png", "_wheel.png")
    # a chassis: the list switches to cars; the game car joins the preview
    Aim "Chassis"
    $res.ownCarGreyed = -not (Items "Ferrari, 2026")[0].Current.IsEnabled
    # a 2026 template takes 2026 cars only: under All, the 2025 cars are greyed
    (ByName "Donors: all seasons").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    $res.otherSeasonGreyed = -not (Items "Red Bull, 2025")[0].Current.IsEnabled
    (ByName "Donors: 2026").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    Pick (Items $Chassis)[0]
    Start-Sleep -Milliseconds 300
    $res.chassis = WaitPreview "chassis"
    Shot $Shot.Replace(".png", "_chassis.png")
    # suits: all seasons, F2 too
    Aim "Suits"
    (ByName "Donors: all seasons").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    $res.suitChips = @((ById "DonorBox").FindAll($T::Children, (Cond $A::ControlTypeProperty $ct::ListItem))).Count
    # a suit set: the figure in the preview, the template's own suits before, these after
    Pick (Items "McLaren, 2026")[0]
    Start-Sleep -Milliseconds 300
    $res.suits = WaitPreview "suits"
    Shot $Shot.Replace(".png", "_suits.png")
    # (the first builds are the wheel alone: the suits go back to the template's own for now)
    (ByName "Suits: back to the template's own").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
    # the wheel on the template's own options first
    (ByName "Chassis: back to the template's own").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
    $edits = $win.FindAll($T::Descendants, (Cond $A::ControlTypeProperty $ct::Edit))
    $outBox = $edits | Where-Object { $_.Current.AutomationId -eq "OutBox" } | Select-Object -First 1
    if (-not $outBox) { $outBox = $edits[$edits.Count - 1] }
    $res.suggested = $outBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    $outBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Out)
    (ById "BuildButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i = 0; $i -lt 360 -and -not (Test-Path $Out); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 1500
    $texts = $win.FindAll($T::Descendants, (Cond $A::ControlTypeProperty $ct::Text)) | ForEach-Object { $_.Current.Name }
    $res.built = [bool]($texts | Where-Object { $_ -like "Done.*" })
    $res.output = if (Test-Path $Out) { "$([math]::Round((Get-Item $Out).Length/1MB,1)) MB" } else { "none" }
    Shot $Shot.Replace(".png", "_built.png")
    # a .simplified name is saved as a .serps (Swapper writes no Simplified Studio projects): the button keeps saying .serps
    $OutTyped = $Out.Replace(".serps", " typed.simplified"); $OutSerps = $OutTyped.Replace(".simplified", ".serps")
    Remove-Item $OutTyped, $OutSerps -ErrorAction SilentlyContinue
    $outBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($OutTyped)
    Start-Sleep -Milliseconds 300
    $res.button = (ById "BuildButton").Current.Name
    (ById "BuildButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i = 0; $i -lt 360 -and -not (Test-Path $OutSerps); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 1500
    $res.noProject = (Test-Path $OutSerps) -and -not (Test-Path $OutTyped)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # all three: the game car with the wheel built onto it as a chassis option of its own, and the suits as a driverwear
    # section of their own
    Aim "Chassis"
    Pick (Items $Chassis)[0]
    Start-Sleep -Milliseconds 300
    Aim "Suits"
    (ByName "Donors: all seasons").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
    Pick (Items "McLaren, 2026")[0]
    Start-Sleep -Milliseconds 300
    # and rims: the preview shows the new chassis with them
    Aim "Rims"
    $res.rimChips = @((ById "DonorBox").FindAll($T::Children, (Cond $A::ControlTypeProperty $ct::ListItem))).Count
    Pick (Items "Williams, 2026")[0]
    Start-Sleep -Milliseconds 300
    $res.rims = WaitPreview "rims"
    Shot $Shot.Replace(".png", "_rims.png")
    $OutChassis = $Out.Replace(".serps", " chassis.serps"); Remove-Item $OutChassis -ErrorAction SilentlyContinue
    $outBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($OutChassis)
    (ById "BuildButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i = 0; $i -lt 360 -and -not (Test-Path $OutChassis); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Milliseconds 1500
    Shot $Shot.Replace(".png", "_chassis_built.png")
    $res.chassisBuilt = if (Test-Path $OutChassis) { $z = [System.IO.Compression.ZipFile]::OpenRead($OutChassis)
        $opt = @($z.Entries | Where-Object { $_.FullName -like "ERPs/??--01--Livery - * Chassis*/01--00--*_car_swapped_livery-and-chassis.erp" -or $_.FullName -like "ERPs/??--02--Driverwear - */01--00--*_driverwear_swapped.erp" } | ForEach-Object { $_.FullName.Split('/')[1] }); $z.Dispose()
        "$($opt -join ', '), $([math]::Round((Get-Item $OutChassis).Length/1MB,1)) MB" } else { "none" }
    # About: the credits and licence notice
    (ById "AboutButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $about = $null
    for ($i = 0; $i -lt 40 -and -not $about; $i++) { Start-Sleep -Milliseconds 300
        $about = $win.FindFirst($T::Children, (Cond $A::NameProperty "About Swapper")) }   # an owned window sits under its owner
    $res.about = [bool]$about
    if ($about) {
        $h = [IntPtr]$about.Current.NativeWindowHandle; Start-Sleep -Milliseconds 500
        $r = New-Object Win+R; [Win]::GetWindowRect($h, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
        $dc = $g.GetHdc(); [Win]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_about.png"))
        $res.credit = [bool]($about.FindAll($T::Descendants, (Cond $A::ControlTypeProperty $ct::Text)) | Where-Object { $_.Current.Name -like "*Based on Simplified Suite by Team Simplified*" })
    }
}
finally {
    Stop-Process -Id $p.Id -ErrorAction SilentlyContinue
}
$res.GetEnumerator() | ForEach-Object { "{0,-14} {1}" -f $_.Key, $_.Value }
