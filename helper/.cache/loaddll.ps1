Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p = "D:\dsh workspace\dsh-desktop-control\helper\.cache\ProbeDll.dll"
try {
  Add-Type -Path $p -ErrorAction Stop
  Write-Output "    DLL loaded"
} catch {
  Write-Output ("    Add-Type -Path failed: " + $_.Exception.Message)
  try {
    $bytes = [System.IO.File]::ReadAllBytes($p)
    [System.Reflection.Assembly]::Load($bytes) | Out-Null
    Write-Output "    loaded via bytes"
  } catch {
    Write-Output ("    bytes load failed: " + $_.Exception.Message)
    exit 1
  }
}
$r = [ProbeDll]::Read(67072)
foreach ($line in $r) { Write-Output ("    " + $line) }
