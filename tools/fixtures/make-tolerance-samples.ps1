param([string]$OutDir)

# Build controlled image pairs for the tolerance sweep.
#
# Ground truth is by construction:
#   PASS   = semantically the same screen, only rendering noise
#   REJECT = the screen really changed (text, position, overlay, state mark)
#
# Everything is drawn deterministically: no RNG seeds that vary, no clock.
# ASCII only (PowerShell 5.1 reads a BOM-less UTF-8 .ps1 as ANSI).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function New-Base() {
  $bmp = New-Object System.Drawing.Bitmap(300, 80, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.Clear([System.Drawing.Color]::White)
  $font = New-Object System.Drawing.Font('Microsoft YaHei', 20)
  $g.DrawString('CONFIRM CANCEL', $font, [System.Drawing.Brushes]::Black, 10, 18)
  # A small square that a "state mark" can appear in.
  $g.DrawRectangle([System.Drawing.Pens]::Gray, 250, 18, 30, 30)
  $g.Dispose()
  return $bmp
}

function Save($bmp, $path) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }

# Pixel-level noise: deterministic, value depends only on (x, y, variant).
function Add-Noise($src, [int]$amp) {
  $dst = $src.Clone()
  for ($y = 0; $y -lt $dst.Height; $y++) {
    for ($x = 0; $x -lt $dst.Width; $x++) {
      $c = $dst.GetPixel($x, $y)
      $d = ((($x * 7 + $y * 13) % 3) - 1) * $amp
      $nr = [Math]::Max(0, [Math]::Min(255, $c.R + $d))
      $ng = [Math]::Max(0, [Math]::Min(255, $c.G + $d))
      $nb = [Math]::Max(0, [Math]::Min(255, $c.B + $d))
      $dst.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $nr, $ng, $nb))
    }
  }
  return $dst
}

$base = New-Base
$basePath = Join-Path $OutDir 'base.png'
Save $base $basePath
Write-Output ('base|' + $basePath + '|base')

# ---------- PASS: same picture, only noise ----------
$p1 = Add-Noise $base 1
Save $p1 (Join-Path $OutDir 'p_noise1.png')
Write-Output ('p_noise1|' + (Join-Path $OutDir 'p_noise1.png') + '|pass')

$p2 = Add-Noise $base 2
Save $p2 (Join-Path $OutDir 'p_noise2.png')
Write-Output ('p_noise2|' + (Join-Path $OutDir 'p_noise2.png') + '|pass')

# One column brighter by 1: the DPI resampling seam that was actually observed.
$p3 = $base.Clone()
for ($y = 0; $y -lt $p3.Height; $y++) {
  $c = $p3.GetPixel(151, $y)
  $p3.SetPixel(151, $y, [System.Drawing.Color]::FromArgb(255,
    [Math]::Min(255, $c.R + 1), [Math]::Min(255, $c.G + 1), [Math]::Min(255, $c.B + 1)))
}
Save $p3 (Join-Path $OutDir 'p_seam.png')
Write-Output ('p_seam|' + (Join-Path $OutDir 'p_seam.png') + '|pass')

# ---------- REJECT: the picture really changed ----------
# 1) text changed
$r1 = New-Object System.Drawing.Bitmap(300, 80, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($r1)
$g.Clear([System.Drawing.Color]::White)
$font = New-Object System.Drawing.Font('Microsoft YaHei', 20)
$g.DrawString('CONFIRM UNDO', $font, [System.Drawing.Brushes]::Black, 10, 18)
$g.DrawRectangle([System.Drawing.Pens]::Gray, 250, 18, 30, 30)
$g.Dispose()
Save $r1 (Join-Path $OutDir 'r_text.png')
Write-Output ('r_text|' + (Join-Path $OutDir 'r_text.png') + '|reject')

# 2) everything shifted 2px right
$r2 = New-Object System.Drawing.Bitmap(300, 80, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($r2)
$g.Clear([System.Drawing.Color]::White)
$g.DrawImage($base, 2, 0)
$g.Dispose()
Save $r2 (Join-Path $OutDir 'r_shift2.png')
Write-Output ('r_shift2|' + (Join-Path $OutDir 'r_shift2.png') + '|reject')

# 3) an opaque strip covering part of the text (dialog/tooltip on top)
$r3 = $base.Clone()
$g = [System.Drawing.Graphics]::FromImage($r3)
$g.FillRectangle([System.Drawing.Brushes]::LightSkyBlue, 40, 10, 90, 40)
$g.Dispose()
Save $r3 (Join-Path $OutDir 'r_overlay.png')
Write-Output ('r_overlay|' + (Join-Path $OutDir 'r_overlay.png') + '|reject')

# 4) the whole region slightly darker (theme/state change)
$r4 = $base.Clone()
for ($y = 0; $y -lt $r4.Height; $y++) {
  for ($x = 0; $x -lt $r4.Width; $x++) {
    $c = $r4.GetPixel($x, $y)
    $r4.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255,
      [Math]::Max(0, $c.R - 8), [Math]::Max(0, $c.G - 8), [Math]::Max(0, $c.B - 8)))
  }
}
Save $r4 (Join-Path $OutDir 'r_darker.png')
Write-Output ('r_darker|' + (Join-Path $OutDir 'r_darker.png') + '|reject')

# 5) a small state mark appears in the box (checkbox tick) -- few pixels, but it matters
$r5 = $base.Clone()
$g = [System.Drawing.Graphics]::FromImage($r5)
$g.DrawLine([System.Drawing.Pens]::Black, 255, 33, 262, 42)
$g.DrawLine([System.Drawing.Pens]::Black, 262, 42, 275, 24)
$g.Dispose()
Save $r5 (Join-Path $OutDir 'r_mark.png')
Write-Output ('r_mark|' + (Join-Path $OutDir 'r_mark.png') + '|reject')

$base.Dispose()
$p1.Dispose(); $p2.Dispose(); $p3.Dispose()
$r1.Dispose(); $r2.Dispose(); $r3.Dispose(); $r4.Dispose(); $r5.Dispose()
