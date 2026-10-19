param([string]$OutDir, [int]$W = 400, [int]$H = 200)

# Generate deterministic test images plus exact crops of them, so the hashing
# code can be checked WITHOUT a desktop: a big canvas, and the same pixels saved
# as their own PNG. If the two hashes ever differ, the crop path is wrong.
#
# Uses Bitmap.Clone(rect, format) for the crop, not DrawImage: DrawImage
# interpolates and shifts by half a pixel, which is exactly the bug this test
# exists to catch.
#
# ASCII only: PowerShell 5.1 reads a BOM-less UTF-8 .ps1 as ANSI and any
# non-ASCII byte in this file would corrupt its own parsing.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Make-Pattern([int]$width, [int]$height, [bool]$withAlpha) {
  $fmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
  if (-not $withAlpha) { $fmt = [System.Drawing.Imaging.PixelFormat]::Format24bppRgb }
  $bmp = New-Object System.Drawing.Bitmap($width, $height, $fmt)
  for ($y = 0; $y -lt $height; $y++) {
    for ($x = 0; $x -lt $width; $x++) {
      $r = ($x * 7 + $y * 3) % 256
      $g = ($x * 13 + $y * 11) % 256
      $b = ($x * 5 + $y * 17) % 256
      $a = 255
      if ($withAlpha) { $a = (($x + $y) % 2) * 255 }
      $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $r, $g, $b))
    }
  }
  return $bmp
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Widths are deliberately awkward: 1, 3, 7, 17, 171. Row stride, alignment and
# odd widths all have to agree for this to pass.
$cases = @(
  @{ name = 'w171'; x = 53; y = 123; w = 171; h = 34; alpha = $true },
  @{ name = 'w17';  x = 1;  y = 99;  w = 17;  h = 5;  alpha = $true },
  @{ name = 'w7';   x = 3;  y = 3;   w = 7;   h = 7;  alpha = $true },
  @{ name = 'w3';   x = 396; y = 197; w = 3;  h = 3;  alpha = $true },
  @{ name = 'w1';   x = 0;  y = 0;   w = 1;   h = 1;  alpha = $true },
  @{ name = 'rgb17'; x = 5; y = 7;   w = 17;  h = 9;  alpha = $false }
)

foreach ($c in $cases) {
  $big = Make-Pattern $W $H $c.alpha
  $bigPath = Join-Path $OutDir ("big-" + $c.name + ".png")
  $big.Save($bigPath, [System.Drawing.Imaging.ImageFormat]::Png)

  $rect = New-Object System.Drawing.Rectangle($c.x, $c.y, $c.w, $c.h)
  $cropFmt = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
  if (-not $c.alpha) { $cropFmt = [System.Drawing.Imaging.PixelFormat]::Format24bppRgb }
  $crop = $big.Clone($rect, $cropFmt)
  $cropPath = Join-Path $OutDir ("crop-" + $c.name + ".png")
  $crop.Save($cropPath, [System.Drawing.Imaging.ImageFormat]::Png)

  $crop.Dispose()
  $big.Dispose()

  Write-Output ($c.name + "|" + $bigPath + "|" + $cropPath + "|" + $c.x + "|" + $c.y + "|" + $c.w + "|" + $c.h)
}
