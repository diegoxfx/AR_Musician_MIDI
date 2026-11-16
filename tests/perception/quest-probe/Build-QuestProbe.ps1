param(
    [Parameter(Mandatory)][string]$Unity,
    [Parameter(Mandatory)][string]$Workspace,
    [string]$Apk,
    [string]$BuildLog,
    [string]$Onnx,
    [string]$Sentis,
    [switch]$ImportOnly
)
$ErrorActionPreference = 'Stop'
$Unity = (Resolve-Path -LiteralPath $Unity).Path
$Workspace = [IO.Path]::GetFullPath($Workspace)
$source = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$project = Join-Path $Workspace 'project'
$Apk = if ($Apk) { [IO.Path]::GetFullPath($Apk) } else { Join-Path $Workspace 'android/AR-Musician-YOLO-Probe.apk' }
$BuildLog = if ($BuildLog) { [IO.Path]::GetFullPath($BuildLog) } else { Join-Path $Workspace 'build.log' }
if ($Workspace.StartsWith($source, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Place generated projects and models outside the probe source directory.'
}
if (($Onnx -and !$Sentis) -or (!$Onnx -and $Sentis) -or ($ImportOnly -and !$Onnx)) {
    throw 'Specify both -Onnx and -Sentis for import.'
}
New-Item -ItemType Directory -Force -Path $project | Out-Null
foreach ($folder in 'Assets', 'Packages', 'ProjectSettings') {
    Copy-Item -LiteralPath (Join-Path $source $folder) -Destination $project -Recurse -Force
}

function Run-Unity([string]$Method, [string]$Log, [string[]]$Arguments, [string]$Marker) {
    if (Test-Path -LiteralPath $Log) { throw "Log already exists: $Log" }
    $unityArgs = @('-batchmode', '-nographics', '-quit', '-job-worker-count', '2', '-projectPath', $project,
        '-buildTarget', 'Android', '-executeMethod', $Method, '-logFile', $Log) + $Arguments
    # Start-Process joins arguments; quote each Windows filesystem path that contains spaces.
    $quoted = $unityArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    $process = Start-Process -FilePath $Unity -ArgumentList $quoted -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath $Log -Pattern $Marker -Quiet)) {
        throw "Unity $Method failed (exit $($process.ExitCode)); inspect $Log"
    }
}

function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
if (!$ImportOnly) {
    if (Test-Path -LiteralPath $Apk) { throw "APK already exists: $Apk" }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($BuildLog)) | Out-Null
    Run-Unity 'YoloQuestBuild.Build' $BuildLog @('-probeApk', $Apk) 'YOLO_PROBE_BUILD_PASS'
    $files = foreach ($folder in 'Assets', 'Packages', 'ProjectSettings') {
        Get-ChildItem -LiteralPath (Join-Path $source $folder) -Recurse -File |
            ForEach-Object { @{ path = $_.FullName.Substring($source.Length + 1); sha256 = Hash $_.FullName } }
    }
    Write-Json "$Apk.json" @{
        package = 'com.armusician.yoloprobe'; sha256 = Hash $Apk; unityExe = $Unity
        logSha256 = Hash $BuildLog; sourceFiles = @($files)
    }
    Get-Item -LiteralPath $Apk
}
if ($Onnx) {
    $onnxPath = (Resolve-Path -LiteralPath $Onnx).Path
    $sentisPath = [IO.Path]::GetFullPath($Sentis)
    if (Test-Path -LiteralPath $sentisPath) { throw "Sentis model already exists: $sentisPath" }
    $importLog = "$sentisPath.import.log"
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($sentisPath)) | Out-Null
    Run-Unity 'YoloQuestBuild.Import' $importLog @('-probeOnnx', $onnxPath, '-probeSentis', $sentisPath) 'YOLO_PROBE_IMPORT_PASS'
    Write-Json "$sentisPath.json" @{
        onnxSha256 = Hash $onnxPath; sha256 = Hash $sentisPath; unityExe = $Unity
        version = '2.4.1'; weights = 'unchanged FP32'; logSha256 = Hash $importLog
    }
    Get-Item -LiteralPath $sentisPath
}
