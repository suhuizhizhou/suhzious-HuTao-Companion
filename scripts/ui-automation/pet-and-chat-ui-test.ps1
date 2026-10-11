# Real end-to-end UI automation: HuTao desktop pet -> QQNT chat client.
# ASCII-only source (Windows PowerShell 5.1 mis-decodes UTF-8 literals); elements are
# located by AutomationId or ControlType, never by matching CJK text.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$global:Pass = 0; $global:Fail = 0
$global:Report = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $global:Pass++ } else { $global:Fail++ }
    $line = "$(if($ok){'PASS'}else{'FAIL'}) $name$(if($detail){"  [$detail]"})"
    $global:Report.Add($line); Write-Output $line
}

$petPid = (Get-Process -Name 'HuTao.Pet' -ErrorAction SilentlyContinue | Select-Object -First 1).Id
if (-not $petPid) { Write-Output 'NO-PET-PROCESS'; exit 1 }
Write-Output "pet pid=$petPid"
$root = [System.Windows.Automation.AutomationElement]::RootElement
$PIDC = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $petPid)

function Find-One([string]$id) {
    $c = New-Object System.Windows.Automation.AndCondition($PIDC,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Find-Many([string]$id) {
    $c = New-Object System.Windows.Automation.AndCondition($PIDC,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Shot([string]$f) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
# Texts inside the message area only (scoped to MessageScroll).
function Get-MessageTexts {
    $scroll = Find-One 'MessageScroll'
    if (-not $scroll) { return @() }
    $els = $scroll.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)))
    return @($els | ForEach-Object { $_.Current.Name } | Where-Object { $_.Trim().Length -gt 0 })
}

$ev = 'E:\AIGC-realtest\evidence'
New-Item -ItemType Directory -Path $ev -Force | Out-Null

Write-Output ''
Write-Output '=== A. open the QQNT client from the pet ==='
$chatBtn = Find-One 'ChatRoomButton'
Check 'A1_pet_chat_button_present' ($null -ne $chatBtn)
if ($chatBtn) { Invoke-El $chatBtn; Write-Output 'clicked the chat button' }
Start-Sleep -Seconds 6

$sessionList = Find-One 'SessionList'
Check 'A2_chat_window_rendered' ($null -ne $sessionList)
if (-not $sessionList) { $global:Report | Out-File "$ev\ui-report.txt" -Encoding utf8; exit 1 }

$rect = $sessionList.Current.BoundingRectangle
$onAny = $false
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
    if ($s.Bounds.IntersectsWith([System.Drawing.Rectangle]::FromLTRB(
        [int]$rect.X, [int]$rect.Y, [int]($rect.X + $rect.Width), [int]($rect.Y + $rect.Height)))) { $onAny = $true }
}
Check 'A3_chat_window_is_on_screen' $onAny "sessionList=$rect"
$titleText = Find-One 'TitleText'
Check 'A4_active_session_has_title' ($null -ne $titleText -and $titleText.Current.Name.Length -gt 0) "title='$($titleText.Current.Name)'"
Shot "$ev\01-chat-opened.png"

Write-Output ''
Write-Output '=== B. sidebar lists the migrated sessions (accessible names) ==='
$items = @($sessionList.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem))))
Check 'B1_sidebar_lists_migrated_sessions' ($items.Count -ge 4) "count=$($items.Count)"
$names = @($items | ForEach-Object { $_.Current.Name })
$generic = @($names | Where-Object { $_ -match 'ViewModel' })
Check 'B2_rows_have_human_readable_names' ($generic.Count -eq 0) ($names -join ' | ')
Check 'B3_rows_are_selectable' (($items | Where-Object { $_.Current.IsEnabled }).Count -eq $items.Count) 'all enabled'

Write-Output ''
Write-Output '=== C. switch sessions by selecting rows ==='
$startTitle = $titleText.Current.Name
$target = $items | Where-Object { $_.Current.Name -ne $startTitle } | Select-Object -First 1
$targetName = $target.Current.Name
$target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 4
$afterTitle = (Find-One 'TitleText').Current.Name
Check 'C1_switching_session_changes_header' ($afterTitle -ne $startTitle -and $afterTitle.Length -gt 0) "'$startTitle' -> '$afterTitle'"

# switch back to the original row (looked up by its accessible name, not by index)
$orig = $items | Where-Object { $_.Current.Name -eq $startTitle } | Select-Object -First 1
$orig.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 4
$backTitle = (Find-One 'TitleText').Current.Name
Check 'C2_switching_back_restores_session' ($backTitle -eq $startTitle) "'$afterTitle' -> '$backTitle'"
Check 'C3_history_survives_switch' ((Get-MessageTexts).Count -gt 0) "texts=$((Get-MessageTexts).Count)"
Shot "$ev\02-after-switching.png"

Write-Output ''
Write-Output '=== D. send a real message (real DeepSeek + real GPT-SoVITS) ==='
# Pick the CHAT window's composer, not the pet window's: the pet still has its own
# input box, and it sits to the LEFT of the sidebar. The chat composer is to the right.
$allInputs = @(Find-Many 'InputBox')
$allSends = @(Find-Many 'SendButton')
$listRect = (Find-One 'SessionList').Current.BoundingRectangle
$input = $allInputs | Where-Object { $_.Current.BoundingRectangle.X -gt $listRect.X } | Select-Object -First 1
$send = $allSends | Where-Object { $_.Current.BoundingRectangle.X -gt $listRect.X } | Select-Object -First 1
Check 'D1_composer_present' ($null -ne $input -and $null -ne $send) "inputs=$($allInputs.Count) sends=$($allSends.Count)"

$probe = 'you hao ya, jin tian zen me yang ' + (Get-Random -Maximum 9999)
$input.SetFocus()
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait($probe)
Start-Sleep -Milliseconds 400
$typed = $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Check 'D2_typed_text_landed_in_composer' ($typed -eq $probe) "typed='$typed'"

$before = (Get-MessageTexts).Count
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Write-Output "sent '$probe'; waiting for model + TTS..."

$mineSeen = $false; $reply = ''
for ($i = 0; $i -lt 120; $i++) {
    Start-Sleep -Seconds 2
    $texts = Get-MessageTexts
    if (-not $mineSeen -and ($texts -contains $probe)) { $mineSeen = $true }
    $others = @($texts | Where-Object { $_ -ne $probe -and $_.Length -gt 3 -and $_ -notmatch '^\d' })
    if ($mineSeen -and $others.Count -gt 0) { $reply = $others[-1]; break }
}
if (-not $mineSeen) {
    Write-Output 'DIAG: message text not found in the live tree; last 8 texts are:'
    Get-MessageTexts | Select-Object -Last 8 | ForEach-Object { Write-Output "   '$_'" }
}
Check 'D3_sent_message_appears_in_transcript' $mineSeen "probe='$probe'"
Check 'D4_character_replied' ($reply.Length -gt 0) "reply='$reply'"
Check 'D5_transcript_grew' ((Get-MessageTexts).Count -ge $before + 2) "before=$before after=$((Get-MessageTexts).Count)"
Shot "$ev\03-after-send.png"

Write-Output ''
Write-Output '=== E. persistence on disk ==='
$convo = 'E:\AIGC-realtest\data\conversations'
$tml = @(Get-ChildItem $convo -Recurse -Filter 'timeline.jsonl' -ErrorAction SilentlyContinue)
$total = 0; foreach ($f in $tml) { $total += @(Get-Content $f.FullName | Where-Object { $_.Trim() }).Count }
Check 'E1_timeline_persisted' ($total -gt 0) "turns=$total in $($tml.Count) sessions"
$probeOnDisk = $false
foreach ($f in $tml) { if ((Get-Content $f.FullName -Raw) -match [regex]::Escape($probe)) { $probeOnDisk = $true } }
Check 'E2_sent_message_persisted' $probeOnDisk 'probe found in a timeline.jsonl'
$idx = Join-Path $convo 'index.json'
$indexOk = (Test-Path $idx) -and ((Get-Content $idx -Raw) -notmatch 'ActivePresences')
Check 'E3_index_round_trips_no_derived_props' $indexOk 'no ActivePresences leak'
$views = @(Get-ChildItem $convo -Recurse -Filter '*.jsonl' -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -like '*view*' })
Check 'E4_per_member_views_exist' ($views.Count -gt 0) "view files=$($views.Count)"

Write-Output ''
Write-Output '=== F. TTS actually produced audio ==='
$gen = 'E:\AIGC-realtest\data\voice\generated'
$recent = @(Get-ChildItem $gen -Filter 'agent_tts_*.wav' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-12) })
$newest = $recent | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Check 'F1_tts_generated_new_audio' ($recent.Count -gt 0) "new=$($recent.Count) newest=$($newest.Name)"
if ($newest) { Check 'F2_audio_is_non_trivial' ($newest.Length -gt 20000) "$([int]($newest.Length/1024))KB" }

Write-Output ''
$global:Report | Out-File "$ev\ui-report.txt" -Encoding utf8
Write-Output "PASSED=$($global:Pass) FAILED=$($global:Fail)"
exit $(if ($global:Fail -gt 0) { 1 } else { 0 })
