# Requires modern PowerShell Add-Type/Roslyn; no Unity installation is required.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $repository 'AR Musicians\Assets\Scripts'
& (Join-Path $PSScriptRoot 'check-scene-wiring.ps1')
$sources = @(
    (Join-Path $scripts 'Midi\MidiCore.cs'),
    (Join-Path $scripts 'Midi\LearningPractice.cs'),
    (Join-Path $scripts 'Midi\PianoHandPartition.cs'),
    (Join-Path $scripts 'Midi\PianoRollLayout.cs'),
    (Join-Path $scripts 'Midi\PianoMidiFeedback.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\PlaneController.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\PianoStrategy.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\CubeFallBehavior.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\NoteEvent.cs'),
    (Join-Path $scripts 'InstrumentsCommon\IGameplayStrategy.cs'),
    (Join-Path $scripts 'InstrumentsCommon\RhythmGameManager.cs'),
    (Join-Path $scripts 'InstrumentsCommon\CountdownUIGeneric.cs'),
    (Join-Path $scripts 'UI\AppText.cs'),
    (Join-Path $scripts 'UI\InstrumentSelectorController.cs'),
    (Join-Path $scripts 'UI\MenuControllerGeneric.cs'),
    (Join-Path $PSScriptRoot 'csharp\UnityIntegrationTests.cs')
)
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this integration suite with PowerShell 7 (pwsh) for modern C# compilation.' }
Add-Type -Path $sources -IgnoreWarnings -WarningAction SilentlyContinue
[UnityIntegrationTests]::Run()
[NoteEvent[]]$bundledNotes = Get-Content (Join-Path $repository 'AR Musicians\Assets\StreamingAssets\Music\darude-sandstorm.json') -Raw | ConvertFrom-Json | ForEach-Object {
    $note = [NoteEvent]::new()
    $note.key = $_.key
    $note.time = $_.time
    $note.duration = $_.duration
    $note
}
[UnityIntegrationTests]::RunBundled($bundledNotes)
