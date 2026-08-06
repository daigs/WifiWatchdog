param(
    [string]$ServiceName = "WifiWatchdog"
)

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

$executablePath = Join-Path $PSScriptRoot "..\WifiWatchdog.exe"
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Published executable was not found: $executablePath"
}
$executable = (Resolve-Path -LiteralPath $executablePath).Path

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service $ServiceName already exists. Uninstall it first or verify the existing service."
}

$binaryPath = '"' + $executable + '"'
$createArguments = @(
    "create"
    $ServiceName
    "binPath="
    $binaryPath
    "start="
    "delayed-auto"
    "depend="
    "WlanSvc"
)
& sc.exe @createArguments
if ($LASTEXITCODE -ne 0) {
    throw "Service creation failed. Exit code: $LASTEXITCODE"
}

& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/""/0
& sc.exe start $ServiceName
