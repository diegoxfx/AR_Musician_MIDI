$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run with PowerShell 7 (pwsh).' }
$repo = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $repo 'AR Musicians\Assets\Scripts'
Add-Type -Path @(
    (Join-Path $scripts 'Midi\PianoHandPartition.cs'),
    (Join-Path $scripts 'Midi\PianoRollLayout.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\NoteEvent.cs'),
    (Join-Path $scripts 'UI\AppText.cs'),
    (Join-Path $PSScriptRoot 'csharp\PianoRollLayoutTests.cs')
)
[PianoRollLayoutTests]::Run()
