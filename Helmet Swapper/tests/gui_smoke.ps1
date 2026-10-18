# GUI smoke test: drives the real exe through UI Automation and screenshots the result.
param(
    [string]$Target = "Charles Leclerc",
    [string]$Donor = "Max Verstappen",
    [string]$OtherShell = "Lance Stroll",          # another make: must be pickable
    [string]$Out = (Join-Path $PSScriptRoot "out\gui_test.serps"),
    [string]$Shot = (Join-Path $PSScriptRoot "out\gui_test.png"),
    [string]$Exe = (Join-Path $PSScriptRoot "..\dist\Helmet Swapper.exe")
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
$exe = $Exe
Remove-Item $Out -ErrorAction SilentlyContinue
# the app saves its settings (game folder, drivers, output folder); keep the user's copy and put it back at the end
$Settings = Join-Path $env:APPDATA "HelmetSwapper\settings.json"
$SettingsBackup = if (Test-Path $Settings) { Get-Content $Settings -Raw } else { $null }
Remove-Item $Settings -ErrorAction SilentlyContinue
$env:HELMETSWAP_NO_BROWSER = '1'   # the preview page is written but not opened
$exportParent = Join-Path (Split-Path $Out) "export_test"
Remove-Item $exportParent -Recurse -Force -ErrorAction SilentlyContinue
$env:HELMETSWAP_EXPORT_DIR = $exportParent   # Export 3D model saves here (no folder dialog)
$p = Start-Process $exe -PassThru
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$ct = [System.Windows.Automation.ControlType]
$win = $null
for ($i = 0; $i -lt 60 -and -not $win; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { $win = $A::FromHandle($p.MainWindowHandle) } }
function Items($name) {
    @($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::ListItem))) |
      Where-Object { $_.Current.Name -like "$name,*" })
}
function Pick($el) { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
# wait for the drivers to load: each driver is a chip in both lists (driver to change, helmet from)
$tgt = @()
for ($i = 0; $i -lt 120 -and $tgt.Count -lt 2; $i++) { Start-Sleep -Milliseconds 500; $tgt = Items $Target }
Pick $tgt[0]
Start-Sleep -Milliseconds 300
$other = Items $OtherShell
$greyed = $other[1].Current.IsEnabled
$self = -not (Items $Target)[1].Current.IsEnabled
Pick (Items $Donor)[1]
$edits = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Edit)))
$suggested = $edits[$edits.Count - 1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
$outBox = $edits[$edits.Count - 1]; $outBox.SetFocus()
$outBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Out)
$build = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Button))) |
         Where-Object { $_.Current.Name -like "*Build*" } | Select-Object -First 1
$build.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$done = $false
for ($i = 0; $i -lt 240 -and -not $done; $i++) { Start-Sleep -Milliseconds 500
    $texts = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Text))) | ForEach-Object { $_.Current.Name }
    $done = [bool]($texts | Where-Object { $_ -like "Done.*" -or $_ -like "Failed:*" }) }
Start-Sleep -Milliseconds 700
$r = New-Object Win+R; [Win]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot)
$texts | Select-Object -Last 8
"suggested output: $suggested"
"different shell pickable: $greyed   same driver greyed: $self"
"output exists: $(Test-Path $Out)  size: $([math]::Round((Get-Item $Out -ErrorAction SilentlyContinue).Length/1MB,1)) MB"
# preview: the 3D page for the current picks (Leclerc with Verstappen's helmet), written to %TEMP%\HelmetSwapper
$pvDir = Join-Path $env:TEMP "HelmetSwapper"; $pvStart = Get-Date
($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Button))) |
 Where-Object { $_.Current.Name -eq "Open preview in browser" } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$pvDone = $false
for ($i = 0; $i -lt 120 -and -not $pvDone; $i++) { Start-Sleep -Milliseconds 250
    $pvTexts = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Text))) | ForEach-Object { $_.Current.Name }
    $pvDone = [bool]($pvTexts | Where-Object { $_ -like "Preview opened*" -or $_ -like "Preview failed*" }) }
$pvFile = Get-ChildItem $pvDir -Filter "Preview - *.html" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $pvStart } | Select-Object -First 1
$previewOk = [bool]$pvFile -and -not ($pvTexts | Where-Object { $_ -like "Preview failed*" }) -and ((Get-Content $pvFile.FullName -Raw) -like "*pv-canvas*")
"preview: $(if ($pvFile) { "$($pvFile.Name)  $([math]::Round($pvFile.Length/1MB,1)) MB" } else { "none" })  ok: $previewOk"
# parts: one slot per part, saying whose it is; x = back to the driver's own; click slots, then a helmet = those parts from it
function ByName($name) { $win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))) }
function Slot($n) { ByName "$n slot" }
function Aim($n) { (Slot $n).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 300 }
function ToOwn($n) { (ByName "${n}: back to own").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
function OutName { $e = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Edit))); $e[$e.Count - 1] }
# another make (Verstappen's Schuberth onto Leclerc's Bell): every part can be taken; his design alone -> the fit warning
$unlocked = (Slot "Wing").Current.IsEnabled -and (Slot "Attachments").Current.IsEnabled
foreach ($n in "Visor", "Visor strip", "Wing", "Attachments") { ToOwn $n }
$locked = [bool]($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Text))) |
                 Where-Object { $_.Current.Name -like "*different make*" })     # the warning shows
# Norris's shell only: aim the shell slot, pick him
Aim "Shell"
Pick (Items "Lando Norris")[1]
Start-Sleep -Milliseconds 500
$sugParts = (OutName).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
$Out2 = $Out.Replace(".serps", "_design.serps"); Remove-Item $Out2 -ErrorAction SilentlyContinue
(OutName).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Out2)
$build.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
for ($i = 0; $i -lt 240 -and -not (Test-Path $Out2); $i++) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Milliseconds 800
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_parts.png"))
"parts: pickable for another make $unlocked, fit warning $locked, suggested $(Split-Path $sugParts -Leaf), built $(Test-Path $Out2)"
$partsOk = $locked -and $unlocked -and ($sugParts -like "*Charles Leclerc - Shell Swap (Lando N).serps") -and (Test-Path $Out2)
Remove-Item $Out2 -ErrorAction SilentlyContinue
# parts from several helmets: Norris's design (above) + Hulkenberg's wing -> "Mixed Swap (Lando N, Nico H)", built and checked
Aim "Shell"; Aim "Wing"
Pick (Items "Nico Hulkenberg")[1]
Start-Sleep -Milliseconds 500
$sugMix = (OutName).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
$wingWho = (Slot "Wing").Current.Name
$Out3 = $Out.Replace(".serps", "_mixed.serps"); Remove-Item $Out3 -ErrorAction SilentlyContinue
(OutName).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Out3)
$build.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
for ($i = 0; $i -lt 240 -and -not (Test-Path $Out3); $i++) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Milliseconds 800
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_mixed.png"))
$mixFolders = if (Test-Path $Out3) { Add-Type -AssemblyName System.IO.Compression.FileSystem; $z = [IO.Compression.ZipFile]::OpenRead($Out3); @($z.Entries | Where-Object { $_.FullName -like "*.erp" } | ForEach-Object { $_.FullName.Split('/')[1] }); $z.Dispose() } else { @() }
$mixOk = ($sugMix -like "*Charles Leclerc - Mixed Swap (Lando N, Nico H).serps") -and (Test-Path $Out3) -and ($mixFolders -contains "01--00--Base - 26 Lando Norris") -and ($mixFolders -contains "04--00--Wing - 26 Nico Hulkenberg")
"mixed: suggested $(Split-Path $sugMix -Leaf), folders $($mixFolders -join ' | '), built $(Test-Path $Out3) -> $mixOk"
Remove-Item $Out3 -ErrorAction SilentlyContinue
Aim "Wing"          # nothing aimed again: the next helmet picked fills every part
$partsOk = $partsOk -and $mixOk
# F2 tab on "Helmet from": F2 drivers listed, the F1 pick (driver to change) kept
$f2tab = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::RadioButton))) |
         Where-Object { $_.Current.Name -eq "Helmet from: F2" } | Select-Object -First 1
$f2tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 600
$f2chips = @(Items "Kush Maini").Count
$f1gone = @(Items $Donor).Count -eq 1          # Verstappen now only in "Driver to change"
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_f2.png"))
"F2 tab: Kush Maini chips $f2chips, F1 donors hidden $f1gone"
# 2025 + F1 on "Helmet from": older helmets listed (Verstappen 2025 and his Japan special)
foreach ($n in "Helmet from: 2025", "Helmet from: F1") {
    ($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::RadioButton))) |
     Where-Object { $_.Current.Name -eq $n } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400 }
$japan = @($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::ListItem))) |
           Where-Object { $_.Current.Name -like "$Donor*Japan,*" }).Count
$y25 = @(Items $Donor).Count + $japan   # 2026 Verstappen (driver to change list), 2025, Japan 2025
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_2025.png"))
"2025 tab: Verstappen chips $y25 (expect 3)"
$f1gone = $f1gone -and $y25 -eq 3
# 2025 tab on "Driver to change": pick 2025 Leclerc (regular livery); the suggested file is marked 2025
($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::RadioButton))) |
 Where-Object { $_.Current.Name -eq "Driver to change: 2025" } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
$t25 = Items $Target | Where-Object { $_.Current.Name -like "*, 2025, *" } | Select-Object -First 1
Pick $t25
Start-Sleep -Milliseconds 400
$edits = $win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Edit)))
$sug25 = $edits[$edits.Count - 1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_target2025.png"))
"2025 target: $(Split-Path $sug25 -Leaf)"
$f1gone = $f1gone -and ($sug25 -like "*Charles Leclerc 2025 - Schuberth Swap (Max V Japan 2025)*" -or $sug25 -like "*Charles Leclerc 2025 - *")
# filters: a search lists matches from every season and series, newest first, each chip with its year
function DonorChips { @($win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, "DonorBox"))).FindAll($T::Children,
    (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::ListItem))) | ForEach-Object { $_.Current.Name }) }
# "All" seasons and series: a search spans every helmet in the game
foreach ($n in "Helmet from: all seasons", "Helmet from: all series") {
    ($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::RadioButton))) |
     Where-Object { $_.Current.Name -eq $n } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400 }
$search = $null
for ($i = 0; $i -lt 40 -and -not $search; $i++) { $search = $win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, "DonorSearch"))); if (-not $search) { Start-Sleep -Milliseconds 250 } }
$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("arai")
$arai = @()
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $arai = DonorChips; if ($arai.Count -ge 5 -and -not ($arai | Where-Object { $_ -notlike "*Arai*" })) { break } }
$araiYears = @($arai | ForEach-Object { ($_ -split ", ")[2] } | Sort-Object -Unique)
$araiOk = $arai.Count -ge 5 -and -not ($arai | Where-Object { $_ -notlike "*Arai*" }) -and $araiYears.Count -ge 2
"search 'arai': $($arai.Count) chips, seasons $($araiYears -join '/'), all arai $araiOk"
$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("")
# team filter: Red Bull, every season
$team = $null
for ($i = 0; $i -lt 40 -and -not $team; $i++) { $team = $win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, "DonorTeam"))); if (-not $team) { Start-Sleep -Milliseconds 250 } }
$team.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
$rbItem = $null
for ($i = 0; $i -lt 40 -and -not $rbItem; $i++) { Start-Sleep -Milliseconds 250
    $rbItem = $team.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::ListItem))) | Where-Object { $_.Current.Name -eq "Red Bull" } | Select-Object -First 1 }
$rbItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$team.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
$rb = @()
for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $rb = DonorChips; if ($rb.Count -ge 4 -and -not ($rb | Where-Object { $_ -notlike "*, Red Bull, *" })) { break } }
$rbYears = @($rb | ForEach-Object { ($_ -split ", ")[2] } | Sort-Object -Unique)
$rbOk = $rb.Count -ge 4 -and -not ($rb | Where-Object { $_ -notlike "*, Red Bull, *" }) -and $rbYears.Count -ge 2
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_filter.png"))
"team 'Red Bull': $($rb.Count) chips, seasons $($rbYears -join '/'), all red bull $rbOk"
# the preview column: the page for the current picks loads and says so
$status = $null
for ($i = 0; $i -lt 40 -and -not $status; $i++) { $status = $win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, "PreviewStatus"))); if (-not $status) { Start-Sleep -Milliseconds 250 } }
$pvText = ""
for ($i = 0; $i -lt 80; $i++) { Start-Sleep -Milliseconds 250; $pvText = $status.Current.Name; if ($pvText -and $pvText -notlike "loading*") { break } }
$columnOk = [bool]$pvText -and $pvText -notlike "loading*"
"preview column: '$pvText' ok $columnOk"
# pop the preview out into its own window, then close it: the column comes back
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public class W3 { public delegate bool P(IntPtr h, IntPtr l);
[DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l);
[DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
[DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
public static IntPtr Find(int pid, string title) { IntPtr found = IntPtr.Zero; EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p);
    var s = new StringBuilder(256); GetWindowText(h, s, 256); if (p == pid && IsWindowVisible(h) && s.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero); return found; } }
"@
$pop = $null
for ($i = 0; $i -lt 40 -and -not $pop; $i++) { $pop = $win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, "PopButton"))); if (-not $pop) { Start-Sleep -Milliseconds 250 } }
$pop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$ph = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $ph -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $ph = [W3]::Find($p.Id, "Helmet Preview") }
$popOk = $ph -ne [IntPtr]::Zero
$popStatus = ""
if ($popOk) {
    $pw = $A::FromHandle($ph)
    for ($i = 0; $i -lt 80; $i++) { Start-Sleep -Milliseconds 250
        $popStatus = ($pw.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::Text))) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like "*helmet*" } | Select-Object -First 1)
        if ($popStatus -and $popStatus -notlike "loading*") { break } }
    $columnHidden = $status.Current.IsOffscreen -or $status.Current.BoundingRectangle.IsEmpty
    $pr = New-Object Win+R; [Win]::GetWindowRect($ph, [ref]$pr) | Out-Null
    $pb = New-Object System.Drawing.Bitmap ($pr.Rt - $pr.L), ($pr.B - $pr.T); $pg = [System.Drawing.Graphics]::FromImage($pb)
    $pdc = $pg.GetHdc(); [Win]::PrintWindow($ph, $pdc, 2) | Out-Null; $pg.ReleaseHdc($pdc); $pb.Save($Shot.Replace(".png", "_popout.png"))
    $pw.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Milliseconds 1500
}
$docked = ($pop.Current.Name -eq "Pop out preview")
$popOk = $popOk -and $popStatus -and $popStatus -notlike "loading*" -and $columnHidden -and $docked
"pop out: window '$popStatus', column hidden $columnHidden, docked again $docked -> $popOk"
# the season tab narrows a search: 2026 only
($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::RadioButton))) |
 Where-Object { $_.Current.Name -eq "Helmet from: 2026" } | Select-Object -First 1).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$rb26 = @()
for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; $rb26 = DonorChips; if ($rb26.Count -ge 1 -and -not ($rb26 | Where-Object { $_ -notlike "*, Red Bull, 2026,*" })) { break } }
$yearOk = $rb26.Count -ge 1 -and $rb26.Count -lt $rb.Count -and -not ($rb26 | Where-Object { $_ -notlike "*, Red Bull, 2026,*" })
"team 'Red Bull' + 2026: $($rb26.Count) chips, only 2026 $yearOk"
# Career on "Driver to change", reached the way a user did: F1 tab, then a career team in the team box (the series tab
# follows the team, so the list isn't empty); a design sharing the My Team parts locks its visor strip, wing and attachments
function Tab($n) { ($win.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::RadioButton))) |
    Where-Object { $_.Current.Name -eq $n } | Select-Object -First 1) }
function PickTeam($id, $name) {
    $cb = $win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)))
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $it = $null
    for ($i = 0; $i -lt 40 -and -not $it; $i++) { Start-Sleep -Milliseconds 250
        $it = $cb.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct::ListItem))) | Where-Object { $_.Current.Name -like $name } | Select-Object -First 1 }
    $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
}
(Tab "Driver to change: F1").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 400
PickTeam "TargetTeam" "Career replica"
$rep = @()
for ($i = 0; $i -lt 40 -and $rep.Count -lt 1; $i++) { Start-Sleep -Milliseconds 250; $rep = Items "Lewis Hamilton ? 2008" }
$tabFollows = (Tab "Driver to change: career helmets").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
PickTeam "TargetTeam" "Career design"
$des = @()
for ($i = 0; $i -lt 40 -and $des.Count -lt 1; $i++) { Start-Sleep -Milliseconds 250; $des = Items "Career Design" }
if ($des.Count -ge 1) { Pick $des[0] }
$careerLocked = $false
for ($i = 0; $i -lt 20 -and -not $careerLocked; $i++) { Start-Sleep -Milliseconds 250; $careerLocked = -not (Slot "Wing").Current.IsEnabled -and -not (Slot "Attachments").Current.IsEnabled -and (Slot "Shell").Current.IsEnabled }
# one chip for every design: the slot picker under the list; slot 52 shares the My Team parts (locked), 103 has its own (free)
$slotCb = $win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, "DesignSlot")))
$slotShown = $slotCb -and -not $slotCb.Current.IsOffscreen
$designChips = @(Items "Career Design").Count
PickTeam "DesignSlot" "Sunrise ? Tier 2"
$unlocked103 = $false
for ($i = 0; $i -lt 20 -and -not $unlocked103; $i++) { Start-Sleep -Milliseconds 250; $unlocked103 = (Slot "Wing").Current.IsEnabled -and (Slot "Attachments").Current.IsEnabled }
"design slot picker shown $slotShown, design chips $designChips, slot 103 unlocks the parts $unlocked103"
$careerOk = $rep.Count -ge 1 -and $des.Count -ge 1 -and $careerLocked -and $tabFollows -and $slotShown -and $designChips -eq 1 -and $unlocked103
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_career.png"))
"career team from the F1 tab: Career tab follows $tabFollows, Hamilton 2008 replica $($rep.Count), Career Design 52 $($des.Count), parts locked $careerLocked -> $careerOk"
# Export 3D model (the helmet in the after view: Career Design 103 with the picked parts) -> FBX + OBJ + textures
$exp = $win.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, "ExportButton")))
$exp.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$fbx = $null
for ($i = 0; $i -lt 120 -and -not $fbx; $i++) { Start-Sleep -Milliseconds 500; $fbx = Get-ChildItem $exportParent -Recurse -Filter *.fbx -ErrorAction SilentlyContinue | Select-Object -First 1 }
Start-Sleep -Milliseconds 800
$objOk = $fbx -and (Test-Path ([IO.Path]::ChangeExtension($fbx.FullName, ".obj"))) -and (Test-Path ([IO.Path]::ChangeExtension($fbx.FullName, ".mtl")))
$texCount = if ($fbx) { @(Get-ChildItem (Join-Path $fbx.DirectoryName "textures") -Filter *.png).Count } else { 0 }
$noSetup = $fbx -and -not (Test-Path (Join-Path $fbx.DirectoryName "painter_setup.json"))      # the Painter setup is gone
$exportOk = $objOk -and $texCount -ge 6 -and $noSetup
$dc = $g.GetHdc(); [Win]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save($Shot.Replace(".png", "_export.png"))
"export: $(if ($fbx) { $fbx.Directory.Name + '' + $fbx.Name } else { 'none' }), obj+mtl $objOk, textures $texCount -> $exportOk"
$ok = $exportOk -and $careerOk -and $yearOk -and $popOk -and $araiOk -and $rbOk -and $columnOk -and $previewOk -and $partsOk -and $greyed -and $self -and (Test-Path $Out) -and ($texts | Where-Object { $_ -like "Done.*" }) -and -not ($texts | Where-Object { $_ -like "Failed:*" }) -and $f2chips -eq 1 -and $f1gone
if ($ok) { "GUI SMOKE: PASS" } else { "GUI SMOKE: FAIL" }
$p.CloseMainWindow() | Out-Null
$p.WaitForExit(10000) | Out-Null
if ($null -ne $SettingsBackup) { Set-Content $Settings $SettingsBackup -NoNewline -Encoding UTF8 } else { Remove-Item $Settings -ErrorAction SilentlyContinue }
