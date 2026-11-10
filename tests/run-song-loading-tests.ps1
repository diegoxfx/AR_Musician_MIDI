# Real production catalog, selector and gameplay loading code with focused Unity stubs.
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
    'Songs\MidiUtils.cs', 'Songs\ExternalMidiLibrary.cs', 'Songs\SongsFinder.cs',
    'Songs\SongLoader.cs', 'Songs\SongsUIHandlerGeneric.cs', 'Songs\ISongMetadata.cs',
    'Songs\SongDifficulty.cs', 'Songs\FileList.cs', 'Piano\PianoHUD\NoteEvent.cs',
    'InstrumentsCommon\IGameplayStrategy.cs', 'InstrumentsCommon\RhythmGameManager.cs'
) | ForEach-Object { Join-Path $scripts $_ }
$sources += Join-Path $PSScriptRoot 'csharp\SongLoadingIntegrationTests.cs'
$frameworkReferences = Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | Select-Object -ExpandProperty FullName
Add-Type -Path $sources -ReferencedAssemblies (@($frameworkReferences) + @($midiDll, $NewtonsoftDll)) -IgnoreWarnings -WarningAction SilentlyContinue
[SongLoadingIntegrationTests]::Run()
