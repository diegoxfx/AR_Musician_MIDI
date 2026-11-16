$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$repository = Split-Path -Parent $PSScriptRoot
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$outputDirectory = Join-Path $temporaryRoot ('AR-Musician-LearningTests-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $outputDirectory)
try {
    $executable = Join-Path $outputDirectory 'LearningPracticeTests.exe'
    & $compiler /nologo /warn:4 /warnaserror /target:exe "/out:$executable" (Join-Path $repository 'AR Musicians\Assets\Scripts\Midi\LearningPractice.cs') (Join-Path $PSScriptRoot 'csharp\LearningPracticeTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Learning core compilation failed.' }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw 'Learning core tests failed.' }
}
finally {
    $resolvedOutput = [IO.Path]::GetFullPath($outputDirectory)
    if (-not $resolvedOutput.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test output escaped temporary root.' }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
