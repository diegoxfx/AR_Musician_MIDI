$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}
$repository = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repository 'AR Musicians\Assets\Scripts\Midi\MidiCore.cs'
$suite = Join-Path $PSScriptRoot 'csharp\MidiCoreTests.cs'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$outputDirectory = Join-Path $temporaryRoot ('AR-Musician-MidiTests-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $outputDirectory)
try {
    $executable = Join-Path $outputDirectory 'MidiCoreTests.exe'
    & $compiler /nologo /warn:4 /warnaserror /target:exe "/out:$executable" $source $suite
    if ($LASTEXITCODE -ne 0) { throw 'MIDI core compilation failed.' }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw 'MIDI core tests failed.' }
}
finally {
    $resolvedOutput = [IO.Path]::GetFullPath($outputDirectory)
    if (-not $resolvedOutput.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a test directory outside the temporary root.'
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
