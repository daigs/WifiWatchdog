param(
    [string]$ServiceName = "WifiWatchdog"
)

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Write-Host "Service $ServiceName does not exist."
    return
}

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
}

& sc.exe delete $ServiceName
