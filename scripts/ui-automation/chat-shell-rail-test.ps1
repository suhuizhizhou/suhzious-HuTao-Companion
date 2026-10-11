# Real UIAutomation probe for the "+" sidebar rail in the QQNT chat window.
#
# ASCII-ONLY on purpose: Windows PowerShell 5.1 decodes this file as GBK, so CJK
# literals inside regexes/quotes get mangled. We match on AutomationId, and read
# CJK names back as data instead of pattern-matching them.
#
# Two hard-won measurement rules (both cost real debugging time):
#   1. Never name a variable $pid -- read-only in PowerShell, assignment fails
#      silently and the probe ends up pointed at the wrong process.
#   2. Do not use IsOffscreen / BoundingRectangle on a bare container Border
#      (RailDrawer). WPF does not expose those reliably for a panel that paints
#      no background of its own, so it reads "not found" even when the drawer is
#      plainly on screen. Assert on the *contents* (the tool list and its items)
#      -- those are real controls with real automation peers.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe  = 'E:\tomorrow\AIGC\hutao-companion\hosts\HuTao.Pet\bin\Debug\net9.0-windows\HuTao.Pet.exe'
$auto = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]
$root = $auto::RootElement
$LI = [System.Windows.Automation.ControlType]::ListItem

$script:pass = 0; $script:fail = 0
function Check($name, $ok, $detail = '') {
    if ($ok) { $script:pass++; Write-Host "PASS $name  [$detail]" }
    else     { $script:fail++; Write-Host "FAIL $name  [$detail]" }
}
function Find-InProc($procId, $id) {
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $procId)),
        (New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, $id)))
    return $root.FindFirst($scope::Descendants, $c)
}
function Get-TopWindows($procId) {
    $all = $root.FindAll($scope::Children,
        (New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $procId)))
    return @($all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window })
}
function Get-RailItems($procId) {
    $list = Find-InProc $procId 'RailToolList'
    if (-not $list) { return @() }
    return @($list.FindAll($scope::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, $LI))))
}
function Invoke-Element($el) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return }
    throw "element supports neither Invoke nor Select"
}

Get-Process -Name 'HuTao.Pet' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Start-Process -FilePath $exe -WorkingDirectory 'E:\AIGC-realtest' | Out-Null
Start-Sleep -Seconds 14

$procId = (Get-Process -Name 'HuTao.Pet' -ErrorAction SilentlyContinue | Select-Object -First 1).Id
if (-not $procId) { Write-Host 'FATAL: process did not start'; exit 1 }
Write-Host "procId=$procId"
Write-Host ''

# ---- A. chat window up; rail strip present; drawer starts empty ----------
Check 'A1_chat_window_started' ($null -ne (Find-InProc $procId 'SessionList')) 'SessionList'
$toggle = Find-InProc $procId 'RailToggleButton'
Check 'A2_rail_toggle_exists' ($null -ne $toggle) 'RailToggleButton'
Check 'A3_rail_toggle_is_clickable' ($null -ne $toggle -and -not $toggle.Current.IsOffscreen) "off=$($toggle.Current.IsOffscreen)"
$beforeItems = Get-RailItems $procId
Check 'A4_drawer_has_no_items_before_open' ($beforeItems.Count -eq 0) "items=$($beforeItems.Count)"

# ---- B. click + reveals the tool list ----------------------------------
Invoke-Element $toggle
Start-Sleep -Seconds 2
$items = Get-RailItems $procId
Check 'B1_click_reveals_tool_list' ($items.Count -ge 2) "items=$($items.Count)"
$names = @($items | ForEach-Object { $_.Current.Name })
foreach ($n in $names) { Write-Host "   tool: $n" }

# Accessibility: item names must not be the record dump (which leaked
# "Invoke = System.Func`1[...]"). RailTool.ToString() is overridden for this.
$dumpLeak = @($names | Where-Object { $_ -match 'System\.Func|Invoke\s*=' })
Check 'B2_tool_names_not_record_dump' ($dumpLeak.Count -eq 0) "leaked=$($dumpLeak.Count)"
Check 'B3_tool_names_have_label_and_detail' (@($names | Where-Object { $_ -match '^..\s' }).Count -ge 2) "sample=$($names[0])"
Check 'B4_toggle_reflects_open_state' ($toggle.Current.Name -ne '+') "name='$($toggle.Current.Name)'"

# ---- C. opening the pet panel creates a second window, sharing runtime --
$first = $items | Select-Object -First 1
Invoke-Element $first
Start-Sleep -Seconds 12
$wins = Get-TopWindows $procId
Check 'C1_second_window_created' ($wins.Count -ge 2) "windows=$($wins.Count)"
foreach ($w in $wins) {
    $r = $w.Current.BoundingRectangle
    Write-Host ("   window: '{0}' rect=({1},{2},{3}x{4})" -f $w.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
}
Check 'C2_both_windows_on_screen' (@($wins | Where-Object { -not $_.Current.IsOffscreen }).Count -ge 2) 'both on screen'
# Panel mode must NOT expose the pet's own chat entry: the chat client hosts it,
# and a second entry would recreate exactly the old chat window we retired.
Check 'C3_old_chat_entry_removed_in_panel_mode' ($null -eq (Find-InProc $procId 'ChatRoomButton')) 'ChatRoomButton absent'
$items2 = Get-RailItems $procId
Check 'C4_rail_refreshed_with_pet_tools' ($items2.Count -gt $items.Count) "before=$($items.Count) after=$($items2.Count)"
foreach ($i in $items2) { Write-Host "   tool2: $($i.Current.Name)" }

# ---- D. clicking + again closes the drawer ------------------------------
$toggle2 = Find-InProc $procId 'RailToggleButton'
Invoke-Element $toggle2
Start-Sleep -Seconds 2
$items3 = Get-RailItems $procId
Check 'D1_click_again_closes_drawer' ($items3.Count -eq 0) "items=$($items3.Count)"

Write-Host ''
Write-Host "RAIL RESULT PASSED=$script:pass FAILED=$script:fail"
Get-Process -Name 'HuTao.Pet' -ErrorAction SilentlyContinue | Stop-Process -Force
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
