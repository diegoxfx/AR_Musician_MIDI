# Tests real MIDI conversion with the shipped DryWetMidi and Unity's Newtonsoft package.
param([string]$NewtonsoftDll)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this suite with PowerShell 7 (pwsh).' }
$repository = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $repository 'AR Musicians\Assets\Scripts'
$midiDll = Join-Path $repository 'AR Musicians\Assets\Plugins\Melanchall.DryWetMidi.dll'
if (-not $NewtonsoftDll) {
    $cache = Join-Path $env:LOCALAPPDATA 'AR-MusicianBuild\source\AR Musicians\Library\PackageCache'
    $NewtonsoftDll = Get-ChildItem -LiteralPath $cache -Filter Newtonsoft.Json.dll -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/]AOT[\\/]' } | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $NewtonsoftDll -or -not (Test-Path -LiteralPath $NewtonsoftDll)) {
    throw 'Unity Newtonsoft.Json.dll not found. Supply -NewtonsoftDll with the package Runtime DLL path.'
}
[void][Reflection.Assembly]::LoadFrom($midiDll)
[void][Reflection.Assembly]::LoadFrom($NewtonsoftDll)
$sources = @(
    (Join-Path $scripts 'Songs\MidiUtils.cs'),
    (Join-Path $scripts 'Songs\ExternalMidiLibrary.cs'),
    (Join-Path $scripts 'Piano\PianoHUD\NoteEvent.cs'),
    (Join-Path $PSScriptRoot 'csharp\ExternalMidiLibraryTests.cs')
)
$frameworkReferences = Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
Add-Type -Path $sources -ReferencedAssemblies (@($frameworkReferences) + @($midiDll, $NewtonsoftDll)) -IgnoreWarnings -WarningAction SilentlyContinue
[ExternalMidiLibraryTests]::Run()
