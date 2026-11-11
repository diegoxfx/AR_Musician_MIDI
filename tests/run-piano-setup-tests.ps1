# Compiles the real piano setup scripts against focused hardware stubs.
# This verifies state/input behavior; it is not a Unity player or headset test.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run with PowerShell 7 (pwsh).' }
$repository = Split-Path -Parent $PSScriptRoot
$scripts = Join-Path $repository 'AR Musicians\Assets\Scripts'
$sources = @(
    (Join-Path $scripts 'InstrumentsCommon\InstrumentDefiner.cs'),
    (Join-Path $scripts 'Piano\PlaneFinder\AbstractPlaneFinderGeneric.cs'),
    (Join-Path $scripts 'Piano\PlaneFinder\CVPlaneFinderGeneric.cs'),
    (Join-Path $scripts 'Piano\PlaneFinder\DefinedPlane.cs'),
    (Join-Path $scripts 'Piano\PianoManagerInstrumentDefiner.cs'),
    (Join-Path $PSScriptRoot 'csharp\PianoSetupTests.cs')
)
Add-Type -Path $sources -IgnoreWarnings -WarningAction SilentlyContinue
[PianoSetupTests]::Run()

$scene = Get-Content (Join-Path $repository 'AR Musicians\Assets\Scenes\MainScene.unity') -Raw
$cv = [regex]::Match($scene, '(?ms)^--- !u!114 &545988033\r?\n(?<body>.*?)(?=^--- !u!|\z)').Groups['body'].Value
$status = [regex]::Match($scene, '(?ms)^--- !u!114 &434299167\r?\n(?<body>.*?)(?=^--- !u!|\z)').Groups['body'].Value
if ($cv -notmatch '(?m)^  statusText: \{fileID: 434299167\}\r?$') { throw 'CV must reference the existing InstructionsText TMP component.' }
if ($status -notmatch 'TMPro.TextMeshProUGUI' -or $status -notmatch 'Press X on the left controller') { throw 'CV instructions must show X left / A right on the existing TMP text.' }
if ($status -match 'Press \(A\) on the left') { throw 'Incorrect left-controller A instruction remains.' }
Write-Output 'Piano scene status wiring: passed.'
