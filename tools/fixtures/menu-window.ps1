# Test fixture: a window with a real menu, to verify clicking a menu item.
#
# Why this exists:
#
#   Third-party acceptance found that clicking a menu item (e.g. Notepad's
#   Edit > Time/Date) was REFUSED with "coordinate belongs to another window".
#   Root cause: the click path activated the target window first, and activating
#   a parent closes its popup menu; the check then compared a coordinate whose
#   window no longer existed.
#
#   The fix treats same-process popup windows as the same app and skips
#   activation for them. This fixture proves the click actually lands.
#
# NOTE: keep this file ASCII-only. PowerShell 5.1 reads BOM-less files as ANSI,
#       so non-ASCII text here becomes a parse error.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools\fixtures\menu-window.ps1 -LogPath out.log -CoordPath coords.json
#
# When the menu opens it writes the screen coordinates of the menu bar item and
# of the dropdown item, so the test clicks exact points instead of guessing.

param(
  [Parameter(Mandatory=$true)][string]$LogPath,
  [Parameter(Mandatory=$true)][string]$CoordPath
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Declare DPI awareness: otherwise Windows scales the form by the display scale
# and the coordinates written to the file no longer match physical pixels.
Add-Type -Namespace NativeDpi -Name Api -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[void][NativeDpi.Api]::SetProcessDPIAware()

$form = New-Object System.Windows.Forms.Form
$form.Text = "Menu Fixture"
$form.Width = 420
$form.Height = 260
$form.StartPosition = 'Manual'
$form.Left = 400
$form.Top = 300
# TopMost：测试要验的是"同进程弹出窗口的落点判定"，不是抢前台。
# 放在普通层时会被 DSH 主窗口盖住，第一次点击落在 DSH 身上（实测）。
$form.TopMost = $true

$menu = New-Object System.Windows.Forms.MenuStrip
$edit = New-Object System.Windows.Forms.ToolStripMenuItem("Edit")
$item = New-Object System.Windows.Forms.ToolStripMenuItem("Test Item")
$item2 = New-Object System.Windows.Forms.ToolStripMenuItem("Second Item")
[void]$edit.DropDownItems.Add($item)
[void]$edit.DropDownItems.Add($item2)
[void]$menu.Items.Add($edit)
$form.MainMenuStrip = $menu
$form.Controls.Add($menu)

$label = New-Object System.Windows.Forms.Label
$label.Text = "Open the menu; clicking Test Item writes a log line."
$label.AutoSize = $true
$label.Left = 10
$label.Top = 40
$form.Controls.Add($label)

# Second ordinary window, same process, NOT owned by main and not a menu:
# used by the test to prove that "same process" alone must not be enough.
$other = New-Object System.Windows.Forms.Form
$other.Text = "Other Window"
$other.Width = 300
$other.Height = 200
$other.StartPosition = 'Manual'
$other.Left = 800
$other.Top = 620
$otherLabel = New-Object System.Windows.Forms.Label
$otherLabel.Text = "not a menu"
$otherLabel.AutoSize = $true
$otherLabel.Left = 20
$otherLabel.Top = 20
$other.Controls.Add($otherLabel)

# B: another ordinary owned window of the main form. The test uses it as the
# explicitly specified target while C (the window above) covers the point --
# both share the same root owner, which the old rule mistook for "belongs to B".
$ownedB = New-Object System.Windows.Forms.Form
$ownedB.Text = "Target B"
$ownedB.Width = 220
$ownedB.Height = 160
$ownedB.StartPosition = 'Manual'
$ownedB.Left = 500
$ownedB.Top = 900

# Clicking the item writes a line: that is the proof the menu item executed.
$item.add_Click({
  Add-Content -Path $LogPath -Value ("clicked " + (Get-Date -Format "HH:mm:ss.fff"))
})

# When the dropdown opens, record both coordinates.
$edit.add_DropDownOpened({
  try {
    $bar = $edit.Bounds
    $barPt = $form.PointToScreen((New-Object System.Drawing.Point(($bar.Left + $bar.Width / 2), ($bar.Top + $bar.Height / 2))))
    $dd = $edit.DropDown
    $itemBounds = $item.Bounds
    $itemPt = $dd.PointToScreen((New-Object System.Drawing.Point(($itemBounds.Left + $itemBounds.Width / 2), ($itemBounds.Top + $itemBounds.Height / 2))))
    $oc2 = $other.PointToScreen((New-Object System.Drawing.Point(150, 100)))
    $obj = @{
      hwnd = [int64]$form.Handle
      barX = [int]$barPt.X; barY = [int]$barPt.Y
      itemX = [int]$itemPt.X; itemY = [int]$itemPt.Y
      otherHwnd = [int64]$other.Handle; otherX = [int]$oc2.X; otherY = [int]$oc2.Y
      bHwnd = [int64]$ownedB.Handle; bX = [int]$bc.X; bY = [int]$bc.Y
    }
    ($obj | ConvertTo-Json -Compress) | Set-Content -Path $CoordPath -Encoding UTF8
  } catch { }
})

$other.Owner = $form
$ownedB.Owner = $form

$form.add_Shown({
  if (Test-Path $CoordPath) { Remove-Item $CoordPath -Force }
  # Publish the handle and the menu-bar point immediately, so the test does not
  # have to guess the window position or enumerate windows.
  try {
    $bar = $edit.Bounds
    $barPt = $form.PointToScreen((New-Object System.Drawing.Point(($bar.Left + $bar.Width / 2), ($bar.Top + $bar.Height / 2))))
    $other.Show()
    $ownedB.Show()
    $bc = $ownedB.PointToScreen((New-Object System.Drawing.Point(110, 80)))
    $oc = $other.PointToScreen((New-Object System.Drawing.Point(150, 100)))
    $obj = @{
      hwnd = [int64]$form.Handle; barX = [int]$barPt.X; barY = [int]$barPt.Y
      otherHwnd = [int64]$other.Handle; otherX = [int]$oc.X; otherY = [int]$oc.Y
      bHwnd = [int64]$ownedB.Handle; bX = [int]$bc.X; bY = [int]$bc.Y
    }
    ($obj | ConvertTo-Json -Compress) | Set-Content -Path $CoordPath -Encoding UTF8
  } catch { }

  $script:menuTimer.Start()
})

[void]$form.ShowDialog()
