# Copy workflow regression: adb visibility must not be mistaken for app ownership.
$ErrorActionPreference = 'Stop'
$global:ARMusicianCopyTestCalls = [Collections.Generic.List[string]]::new()
$global:ARMusicianCopyTestOwnerMode = 'mismatch'
function Invoke-FakeQuestAdb {
    $global:ARMusicianCopyTestCalls.Add(($args -join ' '))
    $global:LASTEXITCODE = 0
    if ($args -contains 'get-state') { return 'device' }
    if ($args -contains 'stat') {
        if ($global:ARMusicianCopyTestOwnerMode -eq 'missing') { $global:LASTEXITCODE = 1; return 'stat: Music: No such file or directory' }
        if ($global:ARMusicianCopyTestOwnerMode -eq 'malformed') { return '10090:2770', 'unrecognized output' }
        if ($global:ARMusicianCopyTestOwnerMode -eq 'matching') { return '10090:2770', '10090:2770' }
        return '10090:2770', ('2000:' + $global:ARMusicianCopyTestOwnerMode)
    }
    if ($args -contains 'push') { return 'copied' }
    throw "Unexpected adb operation: $args"
}
$inputMidi = Join-Path ([IO.Path]::GetTempPath()) ('AR-Musician-CopyTest-' + [Guid]::NewGuid().ToString('N') + '.mid')
$copyTool = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\copy-quest-songs.ps1'
try {
    [IO.File]::WriteAllBytes($inputMidi, [byte[]]@(0))
    foreach ($mode in '2770', '2775', '2776', '2773', 'missing', 'malformed', 'matching', '777', '2777') {
        $global:ARMusicianCopyTestOwnerMode = $mode
        $global:ARMusicianCopyTestCalls.Clear()
        $rejected = $false
        try { & $copyTool -MidiFiles $inputMidi -Adb Invoke-FakeQuestAdb -Serial test-device | Out-Null }
        catch { $rejected = $true; if ($mode -in 'matching', '777', '2777') { throw } }
        $pushes = @($global:ARMusicianCopyTestCalls | Where-Object { $_ -match '\bpush\b' })
        if ($global:ARMusicianCopyTestCalls | Where-Object { $_ -match '\b(mkdir|chmod|chown)\b' }) { throw 'Copy helper must never create or alter ownership/access of Music.' }
        if ($mode -in 'matching', '777', '2777') {
            if ($rejected -or $pushes.Count -ne 1) { throw "An app-owned or repaired $mode Music folder must allow one copy." }
        }
        elseif (!$rejected -or $pushes.Count -ne 0) { throw "Unsafe $mode folder must be rejected before any push." }
    }
    'Quest copy workflow tests passed (9 ownership/access cases; no device accessed).'
}
finally {
    Remove-Item -LiteralPath $inputMidi -Force
    Remove-Variable ARMusicianCopyTestCalls, ARMusicianCopyTestOwnerMode -Scope Global
}
