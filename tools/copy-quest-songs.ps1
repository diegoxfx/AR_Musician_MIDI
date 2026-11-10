param(
    [Parameter(Mandatory = $true)]
    [string[]]$MidiFiles,
    [string]$Adb = 'adb',
    [string]$Serial = ''
)
$ErrorActionPreference = 'Stop'
$destination = '/sdcard/Android/data/com.armusician.midi/files/Music/'
$deviceArgs = if ($Serial) { @('-s', $Serial) } else { @() }
$resolvedFiles = foreach ($path in $MidiFiles) {
    $item = Get-Item -LiteralPath $path
    if ($item.PSIsContainer -or $item.Extension -notin @('.mid', '.midi')) {
        throw "Expected a .mid or .midi file: $path"
    }
    if ($item.Length -gt 1MB) { throw "MIDI exceeds the app's 1 MiB file limit: $path" }
    $item.FullName
    $metadata = Join-Path $item.DirectoryName ($item.BaseName + '_metadata.json')
    if (Test-Path -LiteralPath $metadata -PathType Leaf) {
        if ((Get-Item -LiteralPath $metadata).Length -gt 64KB) { throw "Metadata exceeds 64 KiB: $metadata" }
        $metadata
    }
}
& $Adb @deviceArgs get-state
if ($LASTEXITCODE -ne 0) { throw 'Connect one authorized Quest, or select it with -Serial.' }
& $Adb @deviceArgs shell mkdir -p $destination
if ($LASTEXITCODE -ne 0) { throw 'Could not access the music folder. Install and open AR-Musician MIDI once first.' }
foreach ($path in $resolvedFiles | Select-Object -Unique) {
    & $Adb @deviceArgs push $path $destination
    if ($LASTEXITCODE -ne 0) { throw "Could not copy: $path" }
}
Write-Output "Copied songs to $destination"
Write-Output 'Close and reopen AR-Musician MIDI, then select single-player to see your songs.'
Write-Output 'Import details are written to Music/import-report.txt on the headset.'
