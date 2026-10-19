param([int]$X = 500, [int]$Y = 300, [int]$DelayMs = 2500)

# Test fixture: a window whose text CHANGES BY ITSELF after DelayMs.
#
# Why: the recheck test needs "window did not move, shot is fresh, but the content
# changed". Driving that through the real app made the test depend on the app's
# state and on nobody moving the window; a self-owned window that flips its own
# label is deterministic.
#
# ASCII only: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so any
# non-ASCII byte would corrupt this file's own parsing.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$f = New-Object System.Windows.Forms.Form
$f.Text = 'dsc-recheck-fixture'
$f.StartPosition = 'Manual'
$f.Location = New-Object System.Drawing.Point($X, $Y)
$f.Size = New-Object System.Drawing.Size(360, 240)
$f.BackColor = 'White'
# TopMost: the screenshot is CopyFromScreen over the window rect, so if anything
# covers this window the OCR reads the WRONG pixels -- and the test then reports
# a failure that has nothing to do with the recheck. Being topmost keeps the
# fixture and what is on screen identical.
$f.TopMost = $true

$lblBefore = New-Object System.Windows.Forms.Label
$lblBefore.Text = 'ALPHA-ONE'
$lblBefore.Font = New-Object System.Drawing.Font('Consolas', 20)
$lblBefore.AutoSize = $true
$lblBefore.Location = New-Object System.Drawing.Point(20, 24)
$f.Controls.Add($lblBefore)

$lblStatic = New-Object System.Windows.Forms.Label
$lblStatic.Text = 'KEEP-STILL'
$lblStatic.Font = New-Object System.Drawing.Font('Consolas', 14)
$lblStatic.AutoSize = $true
$lblStatic.Location = New-Object System.Drawing.Point(20, 120)
$f.Controls.Add($lblStatic)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $DelayMs
$timer.Add_Tick({
  $lblBefore.Text = 'BETA-TWO'
  $timer.Stop()
})

[void]$f.Show()
[void]$timer.Start()
Write-Output ("hwnd=" + $f.Handle)

$end = (Get-Date).AddSeconds([Math]::Max(10, ($DelayMs / 1000) + 20))
while ((Get-Date) -lt $end) {
  [System.Windows.Forms.Application]::DoEvents()
  Start-Sleep -Milliseconds 60
}
$f.Close()
