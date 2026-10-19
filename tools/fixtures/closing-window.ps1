param([int]$X = 700, [int]$Y = 400)

# Fixture: a window that CLOSES ITSELF when its label is clicked.
#
# Purpose: exercise "the click was sent, but the follow-up observation failed".
# Killing the shot directory is not an option -- the PNG of the observation lives
# there too, so the candidate lookup would fail first. Having the target vanish
# right after input is the honest version of "we no longer know what happened".
#
# ASCII only: PowerShell 5.1 reads a BOM-less .ps1 as ANSI.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$f = New-Object System.Windows.Forms.Form
$f.Text = 'dsc-closing-fixture'
$f.StartPosition = 'Manual'
$f.Location = New-Object System.Drawing.Point($X, $Y)
$f.Size = New-Object System.Drawing.Size(360, 200)
$f.BackColor = 'White'
$f.TopMost = $true

$lbl = New-Object System.Windows.Forms.Label
$lbl.Text = 'BUTTON'
$lbl.Font = New-Object System.Drawing.Font('Consolas', 26)
$lbl.AutoSize = $true
$lbl.Location = New-Object System.Drawing.Point(20, 40)
$lbl.Add_Click({
  $f.Close()
})
$f.Controls.Add($lbl)

[void]$f.Show()
Write-Output ("hwnd=" + $f.Handle)

$end = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $end) {
  if ($f.IsDisposed) { break }
  [System.Windows.Forms.Application]::DoEvents()
  Start-Sleep -Milliseconds 50
}
try { $f.Close() } catch { }
