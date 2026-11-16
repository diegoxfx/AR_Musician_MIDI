param(
    [string]$JdkBin = '',
    [string]$AndroidJar = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$native = Join-Path $repoRoot 'AR Musicians/Assets/Plugins/Android'
$outputDir = Join-Path ([IO.Path]::GetTempPath()) ('ar-musician-java-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDir | Out-Null
$javac = if ($JdkBin) { Join-Path $JdkBin 'javac.exe' } else { 'javac' }
$java = if ($JdkBin) { Join-Path $JdkBin 'java.exe' } else { 'java' }
try {
    & $javac --release 8 -d $outputDir (Join-Path $native 'MidiStreamDecoder.java') (Join-Path $PSScriptRoot 'MidiStreamDecoderTest.java')
    if ($LASTEXITCODE -ne 0) { throw 'Java decoder test compilation failed' }
    & $java -cp $outputDir com.armusicians.midi.MidiStreamDecoderTest
    if ($LASTEXITCODE -ne 0) { throw 'Java decoder test failed' }
    $stubs = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'stubs') -Filter '*.java' -Recurse | ForEach-Object FullName)
    & $javac --release 8 -d $outputDir @stubs (Join-Path $native 'MidiStreamDecoder.java') (Join-Path $native 'QuestMidi.java') (Join-Path $PSScriptRoot 'QuestMidiTest.java')
    if ($LASTEXITCODE -ne 0) { throw 'Java lifecycle test compilation failed' }
    & $java -cp $outputDir com.armusicians.midi.QuestMidiTest
    if ($LASTEXITCODE -ne 0) { throw 'Java lifecycle test failed' }
    if ($AndroidJar) {
        & $javac --release 8 -cp $AndroidJar -d $outputDir (Join-Path $native 'MidiStreamDecoder.java') (Join-Path $native 'QuestMidi.java')
        if ($LASTEXITCODE -ne 0) { throw 'Android API compilation failed' }
        Write-Output 'QuestMidi Android API compilation: PASS'
    }
} finally {
    # Only remove the exact temporary directory this invocation created.
    $resolvedOutput = (Resolve-Path -LiteralPath $outputDir).Path
    $resolvedTemp = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path.TrimEnd('\') + '\'
    if (!$resolvedOutput.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test output path' }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
