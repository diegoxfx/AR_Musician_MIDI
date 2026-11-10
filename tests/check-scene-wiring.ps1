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
"PASS: $script:checks MainScene serialized wiring assertions (structural checks, not Unity scene execution)"
