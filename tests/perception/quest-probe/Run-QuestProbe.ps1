param(
    [Parameter(Mandatory)][string]$Adb,
    [Parameter(Mandatory)][string]$Aapt,
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$Model,
    [Parameter(Mandatory)][string]$Fixture,
    [Parameter(Mandatory)][string]$Results,
    [Parameter(Mandatory)][ValidateSet('CPU', 'GPUCompute')][string]$Backend,
    [ValidateRange(1, 25)][int]$Repeats = 3,
    [ValidateRange(0.5, 16)][double]$SliceBudgetMs = 4,
    [ValidateRange(30, 1800)][int]$MaxSeconds = 240,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$package = 'com.armusician.yoloprobe'
$Adb = (Resolve-Path -LiteralPath $Adb).Path
$Aapt = (Resolve-Path -LiteralPath $Aapt).Path
$Apk = (Resolve-Path -LiteralPath $Apk).Path
$Model = (Resolve-Path -LiteralPath $Model).Path
$Fixture = (Resolve-Path -LiteralPath $Fixture).Path
$Results = [IO.Path]::GetFullPath($Results)
$resultsPrefix = $Results.TrimEnd([char[]]'\/') + [IO.Path]::DirectorySeparatorChar
if (Test-Path -LiteralPath $Results) { throw "Results already exist: $Results" }

function Run-Adb {
    $response = & $Adb -s $Serial @args
    if ($LASTEXITCODE -ne 0) { throw "ADB exited $LASTEXITCODE for $($args[0])" }
    $response
}
function Read-Json([string]$Path) { Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }
function Device-Hash([string]$Path) {
    $response = (Run-Adb shell sha256sum $Path | Out-String).Trim()
    if ($response -notmatch '^([0-9a-f]{64})\s+') { throw "Missing device checksum for $Path" }
    $Matches[1]
}
function Installed-ApkHash([string]$Path) {
    try { return Device-Hash $Path }
    catch {
        # Some Android builds restrict shell access to /data/app. This development
        # probe can also read its own installed APK under its app identity.
        $response = (Run-Adb shell run-as $package sha256sum $Path | Out-String).Trim()
        if ($response -notmatch '^([0-9a-f]{64})\s+') { throw "Missing installed APK checksum" }
        return $Matches[1]
    }
}
function Read-Log {
    $lines = Run-Adb logcat -d "--pid=$probePid" -s Unity:I AndroidRuntime:E
    $lines | Set-Content -LiteralPath (Join-Path $Results 'device-unity.log')
    $lines -join "`n"
}

$provenance = Read-Json (Join-Path $Fixture 'provenance.json')
$modelInfo = Read-Json "$Model.json"
$apkHash = Hash $Apk
if ((Hash $Model) -ne $modelInfo.sha256 -or $modelInfo.onnxSha256 -ne $provenance.modelSha256) {
    throw 'The Sentis model and ORT fixtures must come from the same ONNX.'
}
$fixtureConfigPath = Join-Path $Fixture 'fixture.json'
if (!$provenance.fixtureConfigSha256 -or (Hash $fixtureConfigPath) -ne $provenance.fixtureConfigSha256) {
    throw 'Fixture configuration checksum mismatch; regenerate with reference.py prepare.'
}
$config = Read-Json $fixtureConfigPath
if ($config.confidence -ne $provenance.confidence -or $config.cases.Count -ne $provenance.cases.Count -or
    @($provenance.cases.id | Sort-Object -Unique).Count -ne $provenance.cases.Count) {
    throw 'Fixture confidence or case inventory differs from provenance.'
}
$caseInputs = @{}
foreach ($item in $config.cases) {
    if ($item.id -notmatch '^[a-zA-Z0-9_-]{1,120}$' -or $caseInputs.ContainsKey($item.id)) {
        throw "Invalid or duplicate case ID: $($item.id)"
    }
    $case = @($provenance.cases | Where-Object id -eq $item.id)
    $path = Join-Path $Fixture ($item.id + '.f32')
    if ($case.Count -ne 1 -or (Hash $path) -ne $case[0].inputSha256) {
        throw "Fixture checksum mismatch: $($item.id)"
    }
    $caseInputs[$item.id] = $path
}
if ($caseInputs.Count -eq 0) { throw 'No inference fixtures' }
$config | Add-Member -NotePropertyName 'backend' -NotePropertyValue $Backend
$config | Add-Member -NotePropertyName 'repeats' -NotePropertyValue $Repeats
$config | Add-Member -NotePropertyName 'sliceBudgetMs' -NotePropertyValue $SliceBudgetMs

$badging = (& $Aapt dump badging $Apk) -join "`n"
if ($LASTEXITCODE -ne 0 -or $badging -notmatch "package: name='com\.armusician\.yoloprobe'" -or
    $badging -notmatch "launchable-activity: name='([A-Za-z0-9_.]+)'") {
    throw 'APK is not the isolated YOLO probe or has no launch activity.'
}
$activity = $Matches[1]
New-Item -ItemType Directory -Path $Results | Out-Null
$configPath = Join-Path $Results 'run.json'
Write-Json $configPath $config
$metadata = [ordered]@{
    serial = $Serial; backend = $Backend; scope = $provenance.scope; startedUtc = [DateTime]::UtcNow.ToString('o')
    apkSha256 = $apkHash; modelSha256 = $modelInfo.onnxSha256; sentisSha256 = $modelInfo.sha256
    fixtureProvenanceSha256 = Hash (Join-Path $Fixture 'provenance.json')
    fixtureConfigSha256 = $provenance.fixtureConfigSha256
    validationSelectionSha256 = $provenance.validationSelectionSha256
    runConfigSha256 = Hash $configPath; panel = 'Android 2D, Vulkan, target 60 fps'; maxRunSeconds = $MaxSeconds
}
$samples = [Collections.Generic.List[object]]::new()
$peak = 0
$probePid = $null
$launched = $false
$outcome = 'Incomplete'
$clock = [Diagnostics.Stopwatch]::new()
try {
    if ((Run-Adb get-state | Out-String).Trim() -ne 'device') { throw 'Headset is not connected and authorized' }
    $metadata.deviceBuild = (Run-Adb shell getprop ro.build.fingerprint | Out-String).Trim()
    if ($Install) { Run-Adb install -r -t $Apk | Write-Output }
    $installed = (Run-Adb shell pm path $package | Out-String).Trim()
    if ($installed -notmatch '^package:(/data/app/[^\s]+/base\.apk)$' -or (Installed-ApkHash $Matches[1]) -ne $apkHash) {
        throw 'Installed probe APK checksum differs from the expected APK. Use -Install for this APK.'
    }
    Run-Adb shell am force-stop $package
    Run-Adb shell am start -W -n "$package/$activity" | Write-Output
    $launched = $true
    $clock.Restart()
    $ready = $null
    while ($clock.Elapsed.TotalSeconds -lt 35) {
        $probePid = ((& $Adb -s $Serial shell pidof $package) | Out-String).Trim()
        if ($probePid -match '^\d+$') {
            $ready = Read-Log
            if ($ready -match 'YOLO_PROBE READY path=(/[^\s]+) device=') { break }
        }
        Start-Sleep -Milliseconds 500
    }
    if (!$ready -or $ready -notmatch 'YOLO_PROBE READY path=(/[^\s]+) device=') {
        throw 'Probe did not become ready. Keep the Quest awake and the panel visible.'
    }
    $remote = $Matches[1]
    if ($remote -notmatch '^/(?:storage/emulated/0|sdcard)/Android/data/com\.armusician\.yoloprobe/files$') {
        throw "Unexpected app storage path: $remote"
    }
    Run-Adb push $Model "$remote/model.sentis" | Write-Output
    Run-Adb push $configPath "$remote/run.json" | Write-Output
    $files = @($caseInputs.Values)
    Run-Adb push @files "$remote/" | Write-Output
    if ((Device-Hash "$remote/model.sentis") -ne $modelInfo.sha256 -or
        (Device-Hash "$remote/run.json") -ne $metadata.runConfigSha256) { throw 'Staged model/config hash mismatch' }
    foreach ($case in $provenance.cases) {
        if ((Device-Hash "$remote/$($case.id).f32") -ne $case.inputSha256) {
            throw "Staged fixture hash mismatch: $($case.id)"
        }
    }
    Run-Adb shell "dumpsys meminfo $probePid; grep MemAvailable /proc/meminfo" |
        Set-Content -LiteralPath (Join-Path $Results 'baseline-meminfo.txt')
    $clock.Restart()
    Run-Adb shell touch "$remote/run.flag"
    $lastStage = ''
    while ($clock.Elapsed.TotalSeconds -lt $MaxSeconds) {
        $log = Read-Log
        $markers = [regex]::Matches($log, 'YOLO_PROBE [^\r\n]+')
        $stage = if ($markers.Count) { $markers[$markers.Count - 1].Value } else { 'launching' }
        if ($stage -ne $lastStage) { Write-Output $stage; $lastStage = $stage }
        $mem = Run-Adb shell "dumpsys meminfo $probePid; grep MemAvailable /proc/meminfo"
        $memText = $mem -join "`n"
        $pss = if ($memText -match 'TOTAL PSS:\s*(\d+)') { [int]$Matches[1] } else { $null }
        $available = if ($memText -match 'MemAvailable:\s*(\d+)') { [int]$Matches[1] } else { $null }
        $samples.Add([pscustomobject]@{
            seconds = [math]::Round($clock.Elapsed.TotalSeconds, 3); stage = $stage
            appPssKiB = $pss; systemAvailableKiB = $available
        })
        if ($null -ne $pss -and $pss -gt $peak) {
            $peak = $pss
            $mem | Set-Content -LiteralPath (Join-Path $Results 'peak-meminfo.txt')
        }
        if (($null -ne $available -and $available -lt 384 * 1024) -or $peak -ge 5.4 * 1024 * 1024) {
            throw 'Device memory guard reached'
        }
        if ($log -match 'YOLO_PROBE FAILED|FATAL EXCEPTION|Fatal signal|OutOfMemoryError') {
            throw 'Device reported a runtime failure; see device-unity.log'
        }
        if ($log -match 'YOLO_PROBE (ALL_PASS|COMPLETED_INTERRUPTED)') {
            $completed = $Matches[1]
            $output = Join-Path $Results 'device'
            New-Item -ItemType Directory -Path $output | Out-Null
            Run-Adb pull "$remote/run-output/benchmark.json" $output | Write-Output
            foreach ($id in $caseInputs.Keys) {
                foreach ($suffix in 'det.f32', 'proto.f32', 'masks') {
                    Run-Adb pull "$remote/run-output/$id.$suffix" $output | Write-Output
                }
            }
            $outcome = if ($completed -eq 'ALL_PASS') { 'Executed' } else { 'Interrupted' }
            break
        }
        $current = ((& $Adb -s $Serial shell pidof $package) | Out-String).Trim()
        if ($current -ne $probePid) { throw 'Probe process exited during inference' }
        Start-Sleep -Milliseconds 500
    }
    if ($outcome -eq 'Incomplete') { throw "Watchdog reached $MaxSeconds seconds" }
}
catch {
    $outcome = "Failed: $($_.Exception.Message)"
    throw
}
finally {
    if ($probePid -match '^\d+$') {
        try { $null = Read-Log } catch { Write-Warning $_.Exception.Message }
    }
    if ($launched) {
        try { $null = Run-Adb shell am force-stop $package } catch { Write-Warning $_.Exception.Message }
    }
    $samples | Export-Csv -LiteralPath (Join-Path $Results 'device-memory-samples.csv') -NoTypeInformation
    $metadata.outcome = $outcome
    $metadata.peakSampledPssKiB = if ($peak -gt 0) { $peak } else { $null }
    $metadata.secondsAfterTrigger = if ($clock.IsRunning) { $clock.Elapsed.TotalSeconds } else { $null }
    $metadata.endedUtc = [DateTime]::UtcNow.ToString('o')
    $metadata.resultFiles = @(Get-ChildItem -LiteralPath $Results -Recurse -File | ForEach-Object {
        @{ path = $_.FullName.Substring($resultsPrefix.Length).Replace('\', '/'); sha256 = Hash $_.FullName }
    })
    Write-Json (Join-Path $Results 'device-summary.json') $metadata
    Write-Output "YOLO Quest probe: $outcome"
}
if ($outcome -ne 'Executed') { throw 'Panel paused during the benchmark; review evidence and rerun.' }
