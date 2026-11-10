# Requires modern PowerShell Add-Type/Roslyn; no Unity installation is required.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $repository 'AR Musicians\Assets\Scripts'
$sources = @(
    (Join-Path $scripts 'Midi\MidiCore.cs'),
    (Join-Path $scripts 'Midi\PianoMidiFeedback.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\PlaneController.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\PianoStrategy.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\CubeFallBehavior.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\NoteEvent.cs'),
    (Join-Path $scripts 'InstrumentsCommon\IGameplayStrategy.cs'),
    (Join-Path $scripts 'InstrumentsCommon\RhythmGameManager.cs'),
    (Join-Path $PSScriptRoot 'csharp\UnityIntegrationTests.cs')
)
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this integration suite with PowerShell 7 (pwsh) for modern C# compilation.' }
Add-Type -Path $sources -IgnoreWarnings -WarningAction SilentlyContinue
[UnityIntegrationTests]::Run()
