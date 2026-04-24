$ErrorActionPreference = "Stop"

$compiler = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $compiler)) {
    $compiler = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path $compiler)) {
    throw "csc.exe was not found."
}

$outDir = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$refs = @(
    "/r:System.dll"
    "/r:System.Core.dll"
    "/r:System.Drawing.dll"
    "/r:System.Windows.Forms.dll"
)

$shared = @(
    (Join-Path $PSScriptRoot "Shared\Protocol.cs")
    (Join-Path $PSScriptRoot "Shared\DpiAwareness.cs")
    (Join-Path $PSScriptRoot "Shared\DiscoveryProtocol.cs")
    (Join-Path $PSScriptRoot "Shared\FfmpegLocator.cs")
)

$hostSources = @(
    (Join-Path $PSScriptRoot "Host\Program.cs")
    (Join-Path $PSScriptRoot "Host\HostForm.cs")
    (Join-Path $PSScriptRoot "Host\RemoteHostServer.cs")
    (Join-Path $PSScriptRoot "Host\InputInjector.cs")
    (Join-Path $PSScriptRoot "Host\ScreenStreamer.cs")
    (Join-Path $PSScriptRoot "Host\DesktopDuplicationCapture.cs")
    (Join-Path $PSScriptRoot "Host\HostDiscoveryBroadcaster.cs")
    (Join-Path $PSScriptRoot "Host\FfmpegVideoStreamer.cs")
) + $shared

$viewerSources = @(
    (Join-Path $PSScriptRoot "Viewer\Program.cs")
    (Join-Path $PSScriptRoot "Viewer\ViewerForm.cs")
    (Join-Path $PSScriptRoot "Viewer\RemoteViewerClient.cs")
    (Join-Path $PSScriptRoot "Viewer\HostDiscoveryListener.cs")
    (Join-Path $PSScriptRoot "Viewer\FfmpegVideoDecoder.cs")
) + $shared

$hostOut = "/out:" + (Join-Path $outDir "RemoteHost.exe")
$viewerOut = "/out:" + (Join-Path $outDir "RemoteViewer.exe")

& $compiler /nologo /target:winexe $hostOut $refs $hostSources
if ($LASTEXITCODE -ne 0) {
    throw "Host build failed."
}

& $compiler /nologo /target:winexe $viewerOut $refs $viewerSources
if ($LASTEXITCODE -ne 0) {
    throw "Viewer build failed."
}

$ffmpegSource = Join-Path $PSScriptRoot "tools\ffmpeg\ffmpeg-8.1-essentials_build\bin\ffmpeg.exe"
if (Test-Path $ffmpegSource) {
    Copy-Item -Path $ffmpegSource -Destination (Join-Path $outDir "ffmpeg.exe") -Force
}

$ffmpegLicense = Join-Path $PSScriptRoot "tools\ffmpeg\ffmpeg-8.1-essentials_build\LICENSE"
if (Test-Path $ffmpegLicense) {
    Copy-Item -Path $ffmpegLicense -Destination (Join-Path $outDir "FFMPEG-LICENSE.txt") -Force
}

Write-Output "Built:"
Get-ChildItem $outDir | Select-Object Name, Length
