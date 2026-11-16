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
# Android/data is scoped to the app on Quest. A shell-created Music directory can
# be visible to adb but unreadable by the actual app UID, even inside its files folder.
# The app creates this directory itself; never substitute adb mkdir for first launch.
# Also accept an existing legacy folder whose access was explicitly repaired to
# permit read/write/traversal for the app. This helper never changes its permissions.
$filesFolder = $destination.TrimEnd('/').Substring(0, $destination.TrimEnd('/').LastIndexOf('/'))
$attributes = @(& $Adb @deviceArgs shell stat -c '%u:%a' $filesFolder $destination 2>&1)
if ($LASTEXITCODE -ne 0 -or $attributes.Count -ne 2 -or $attributes[0] -notmatch '^\d+:[0-7]{3,4}$' -or $attributes[1] -notmatch '^\d+:[0-7]{3,4}$') {
    throw 'Could not verify the app Music folder. On first launch, install and open AR-Musician MIDI to let the app create it. A legacy adb-created folder may need its app access repaired separately. No files were copied.'
}
$filesOwner = $attributes[0].Split(':')[0]
$musicAttributes = $attributes[1].Split(':')
if ($filesOwner -ne $musicAttributes[0] -and $musicAttributes[1] -notmatch '7$') {
    throw 'Music has a different owner than the app files folder and lacks app read/write/traversal access (often from a legacy adb mkdir). Repair its ownership or access separately before copying. On first launch, let AR-Musician MIDI create Music itself; rebuilding cannot repair a shell-owned folder. No files were copied.'
}
foreach ($path in $resolvedFiles | Select-Object -Unique) {
    & $Adb @deviceArgs push $path $destination
    if ($LASTEXITCODE -ne 0) { throw "Could not copy: $path" }
}
Write-Output "Copied songs to $destination"
Write-Output 'Close and reopen AR-Musician MIDI, then select single-player to see your songs.'
Write-Output 'Import details are written to Music/import-report.txt on the headset.'
