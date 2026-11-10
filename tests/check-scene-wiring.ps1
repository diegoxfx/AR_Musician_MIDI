# Structural YAML regression checks; these do not replace opening the scene in Unity.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repository 'AR Musicians\Assets'
$scene = Get-Content (Join-Path $assets 'Scenes\MainScene.unity') -Raw
$blocks = @{}
foreach ($block in [regex]::Matches($scene, '(?ms)^--- !u!(?<class>\d+) &(?<id>\d+)\r?\n(?<body>.*?)(?=^--- !u!|\z)')) {
    $blocks[$block.Groups['id'].Value] = $block.Groups['body'].Value
}
$script:checks = 0
function Assert-Scene([bool]$condition, [string]$message) {
    $script:checks++
    if (!$condition) { throw "MainScene wiring: $message" }
}
function Read-ScriptGuid([string]$path) {
    $meta = Get-Content (Join-Path $assets "$path.meta") -Raw
    $match = [regex]::Match($meta, '(?m)^guid: ([a-f0-9]{32})\r?$')
    if (!$match.Success) { throw "Missing script GUID in $path.meta" }
    return $match.Groups[1].Value
}
function Read-Reference([string]$body, [string]$field) {
    $match = [regex]::Match($body, '(?m)^  ' + [regex]::Escape($field) + ': \{fileID: (\d+)\}\r?$')
    if (!$match.Success) { throw "Missing serialized reference $field" }
    return $match.Groups[1].Value
}
$selectorGuid = Read-ScriptGuid 'Scripts\UI\InstrumentSelectorController.cs'
$selectors = @($blocks.Keys | Where-Object { $blocks[$_] -match "m_Script: \{fileID: 11500000, guid: $selectorGuid," })
Assert-Scene ($selectors.Count -eq 1) 'Expected exactly one InstrumentSelectorController component'
$selectorId = $selectors[0]
$selector = $blocks[$selectorId]
foreach ($reference in @(
    @{ Field = 'rhythmGameManager'; Script = 'Scripts\InstrumentsCommon\RhythmGameManager.cs' },
    @{ Field = 'pianoStrategy'; Script = 'Scripts\Piano\PianoHUD\PianoStrategy.cs' },
    @{ Field = 'bongoStrategy'; Script = 'Scripts\Bongos\BongosHUD\BongoStrategy.cs' }
)) {
    $id = Read-Reference $selector $reference.Field
    Assert-Scene ($id -ne '0' -and $blocks.ContainsKey($id)) "$($reference.Field) must resolve to a scene component"
    $guid = Read-ScriptGuid $reference.Script
    Assert-Scene ($blocks[$id] -match "m_Script: \{fileID: 11500000, guid: $guid,") "$($reference.Field) must reference the correct script type"
}
foreach ($button in @(
    @{ Field = 'pianoButton'; Name = 'PianoButton'; Method = 'selectPiano' },
    @{ Field = 'bongosButton'; Name = 'BongosButton'; Method = 'selectBongos' }
)) {
    $id = Read-Reference $selector $button.Field
    Assert-Scene ($id -ne '0' -and $blocks.ContainsKey($id)) "$($button.Field) must resolve to a scene component"
    $body = $blocks[$id]
    $objectId = Read-Reference $body 'm_GameObject'
    Assert-Scene ($blocks[$objectId] -match ('(?m)^  m_Name: ' + $button.Name + '\r?$')) "$($button.Field) must point to the matching named button"
    $calls = @([regex]::Matches($body, '(?ms)^      - m_Target: \{fileID: (?<target>\d+)\}\r?\n(?<body>.*?)(?=^      - m_Target:|\z)'))
    $selectionCalls = @($calls | Where-Object { $_.Groups['body'].Value -match 'm_TargetAssemblyTypeName: InstrumentSelectorController,' })
    Assert-Scene ($selectionCalls.Count -eq 1) "$($button.Name) must invoke exactly one instrument selection callback"
    $call = $selectionCalls[0]
    Assert-Scene ($call.Groups['target'].Value -eq $selectorId -and $call.Groups['body'].Value -match ('(?m)^        m_MethodName: ' + $button.Method + '\r?$') -and $call.Groups['body'].Value -match '(?m)^        m_CallState: [12]\r?$') "$($button.Name) must invoke enabled $($button.Method) on the assigned selector"
    $navigation = @($calls | Where-Object { $_.Groups['body'].Value -match '(?m)^        m_MethodName: postInstrumentSelectedMenu\r?$' })
    Assert-Scene ($navigation.Count -eq 1) "$($button.Name) must retain its post-selection menu callback"
}
$countdownGuid = Read-ScriptGuid 'Scripts\InstrumentsCommon\CountdownUIGeneric.cs'
$menuGuid = Read-ScriptGuid 'Scripts\UI\MenuControllerGeneric.cs'
$songSelectionPattern = '(?m)^        m_MethodName: OnSongSelected\r?$'
$songButtonIds = @($blocks.Keys | Where-Object { $blocks[$_] -match $songSelectionPattern })
Assert-Scene ($songButtonIds.Count -eq 1) 'Expected exactly one song selection button listener'
$songButton = $blocks[$songButtonIds[0]]
$songButtonObjectId = Read-Reference $songButton 'm_GameObject'
Assert-Scene ($blocks[$songButtonObjectId] -match '(?m)^  m_Name: SelectButton\r?$') 'Song selection listener must belong to SelectButton'
$songCalls = @([regex]::Matches($songButton, '(?ms)^      - m_Target: \{fileID: (?<target>\d+)\}\r?\n(?<body>.*?)(?=^      - m_Target:|\z)'))
Assert-Scene ($songCalls.Count -eq 1) 'SelectButton must have only its guarded song selection listener, with no unconditional menu callback'
$songCall = $songCalls[0]
Assert-Scene ($songCall.Groups['body'].Value -match $songSelectionPattern -and $songCall.Groups['body'].Value -match '(?m)^        m_CallState: [12]\r?$') 'SelectButton must invoke enabled OnSongSelected'
$songSelectorId = $songCall.Groups['target'].Value
$songsGuid = Read-ScriptGuid 'Scripts\Songs\SongsUIHandlerGeneric.cs'
Assert-Scene ($blocks.ContainsKey($songSelectorId) -and $blocks[$songSelectorId] -match "m_Script: \{fileID: 11500000, guid: $songsGuid,") 'SelectButton must target the scene SongsUIHandlerGeneric component'
$songManagerId = Read-Reference $blocks[$songSelectorId] 'manager'
Assert-Scene ($songManagerId -eq (Read-Reference $selector 'rhythmGameManager')) 'Song selector and instrument selector must use the same game manager'
$songMenuId = Read-Reference $blocks[$songManagerId] 'menucontroller'
Assert-Scene ($blocks.ContainsKey($songMenuId) -and $blocks[$songMenuId] -match "m_Script: \{fileID: 11500000, guid: $menuGuid,") 'Guarded song navigation must resolve to the game manager menu controller'
foreach ($button in @(
    @{ Name = 'StartButton'; Method = 'StartCountdown' },
    @{ Name = 'ResumeButton'; Method = 'ResumeCountdown' }
)) {
    $methodPattern = '(?m)^        m_MethodName: ' + $button.Method + '\r?$'
    $buttonIds = @($blocks.Keys | Where-Object { $blocks[$_] -match $methodPattern })
    Assert-Scene ($buttonIds.Count -eq 1) "Expected one actual $($button.Method) button listener"
    $body = $blocks[$buttonIds[0]]
    $objectId = Read-Reference $body 'm_GameObject'
    Assert-Scene ($blocks[$objectId] -match ('(?m)^  m_Name: ' + $button.Name + '\r?$')) "$($button.Method) must belong to the matching named button"
    $calls = @([regex]::Matches($body, '(?ms)^      - m_Target: \{fileID: (?<target>\d+)\}\r?\n(?<body>.*?)(?=^      - m_Target:|\z)'))
    Assert-Scene ($calls.Count -eq 2) "$($button.Name) must have exactly the hide and countdown listeners"
    Assert-Scene ($calls[0].Groups['body'].Value -match '(?m)^        m_MethodName: HideAllMenus\r?$' -and $calls[1].Groups['body'].Value -match $methodPattern -and $calls[0].Groups['body'].Value -match '(?m)^        m_CallState: [12]\r?$' -and $calls[1].Groups['body'].Value -match '(?m)^        m_CallState: [12]\r?$') "$($button.Name) must invoke enabled HideAllMenus FIRST, then $($button.Method), so rejection recovery stays visible"
    $countdownId = $calls[1].Groups['target'].Value
    $menuId = $calls[0].Groups['target'].Value
    Assert-Scene ($blocks.ContainsKey($countdownId) -and $blocks[$countdownId] -match "m_Script: \{fileID: 11500000, guid: $countdownGuid,") "$($button.Name) countdown target must resolve to CountdownUIGeneric"
    Assert-Scene ($blocks.ContainsKey($menuId) -and $blocks[$menuId] -match "m_Script: \{fileID: 11500000, guid: $menuGuid,") "$($button.Name) hide target must resolve to MenuControllerGeneric"
    $managerId = Read-Reference $blocks[$countdownId] 'rhythmGameManager'
    Assert-Scene ($managerId -eq (Read-Reference $selector 'rhythmGameManager')) "$($button.Name) countdown must use the same game manager as the selector"
    Assert-Scene ((Read-Reference $blocks[$managerId] 'menucontroller') -eq $menuId) "$($button.Name) must hide the same menu controller used by countdown recovery"
}
"PASS: $script:checks MainScene serialized wiring assertions (structural checks, not Unity scene execution)"
