param(
  [string]$Path = '',
  [switch]$ProbeOnly,
  [string]$Lang = '',
  [int]$CropX = 0,
  [int]$CropY = 0,
  [int]$CropW = 0,
  [int]$CropH = 0
)

# Windows built-in OCR (Windows.Media.Ocr). ASCII-only on purpose:
# Windows PowerShell 5.1 reads a .ps1 without a BOM as ANSI, and any non-ASCII
# comment or string in this file would corrupt its own parsing. OCR'd text is
# returned as UTF-8 JSON; that is unaffected by this file's encoding.
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Emit($obj) {
  Write-Output ($obj | ConvertTo-Json -Compress -Depth 6)
  exit 0
}

try {
  Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

  [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
  [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime] | Out-Null
  [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
  [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null

  $asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
  })[0]

  function Await($op, $type) {
    $asTask = $asTaskGeneric.MakeGenericMethod($type)
    $task = $asTask.Invoke($null, @($op))
    if (-not $task.Wait(20000)) { throw 'WinRT await timeout' }
    return $task.Result
  }

  # Available recognizer languages: the caller needs to know what this machine can
  # actually read before trusting any result.
  $languages = @()
  foreach ($l in [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages) {
    $languages += $l.LanguageTag
  }
  if ($languages.Count -eq 0) {
    Emit @{ ok = $false; error = 'NoOcrLanguage' ; languages = @() }
  }

  # Languages only: one launch, no image, no recognition. The caller uses this to
  # decide whether the backend is available, instead of starting a second
  # PowerShell just to ask which languages exist.
  # (ASCII only on purpose: PowerShell 5.1 reads a BOM-less UTF-8 .ps1 as ANSI.)
  if ($ProbeOnly) {
    Emit @{ ok = $true; backend = 'windows-ocr'; languages = $languages; lines = @(); elapsedMs = 0 }
  }

  $engine = $null
  if ($Lang) {
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage(
      (New-Object Windows.Globalization.Language($Lang)))
  }
  if ($null -eq $engine) {
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
  }
  if ($null -eq $engine) {
    Emit @{ ok = $false; error = 'NoOcrEngine'; languages = $languages }
  }

  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
  $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
  $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
  $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
  $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
  $sw.Stop()

  $lines = @()
  foreach ($line in @($result.Lines)) {
    $words = @($line.Words)
    if ($words.Count -eq 0) { continue }
    # Line box = min/max over ALL words, not "first word's top-left + last word's
    # bottom-right": the latter misses taller words or non-monotonic layouts.
    $minX = [double]::MaxValue
    $minY = [double]::MaxValue
    $maxX = [double]::MinValue
    $maxY = [double]::MinValue
    foreach ($w in $words) {
      $bx = [double]$w.BoundingRect.X
      $by = [double]$w.BoundingRect.Y
      $br = [double]($bx + $w.BoundingRect.Width)
      $bb = [double]($by + $w.BoundingRect.Height)
      if ($bx -lt $minX) { $minX = $bx }
      if ($by -lt $minY) { $minY = $by }
      if ($br -gt $maxX) { $maxX = $br }
      if ($bb -gt $maxY) { $maxY = $bb }
    }
    $x = $minX
    $y = $minY
    $right = $maxX
    $bottom = $maxY

    # Per-word boxes are kept: a line box is the union of its words, which is
    # wrong for sparse lines (a row with two distant labels).
    $wordList = @()
    foreach ($w in $words) {
      $wordList += @{
        text = $w.Text
        x = [double]$w.BoundingRect.X
        y = [double]$w.BoundingRect.Y
        w = [double]$w.BoundingRect.Width
        h = [double]$w.BoundingRect.Height
      }
    }

    $lines += @{
      text = $line.Text
      x = $x
      y = $y
      w = [double]($right - $x)
      h = [double]($bottom - $y)
      words = $wordList
    }
  }

  Emit @{
    ok = $true
    backend = 'windows-ocr'
    engineLanguage = $engine.RecognizerLanguage.LanguageTag
    languages = $languages
    image = @{ width = $decoder.PixelWidth; height = $decoder.PixelHeight }
    elapsedMs = $sw.ElapsedMilliseconds
    lines = $lines
  }
}
catch {
  Emit @{ ok = $false; error = ($_.Exception.GetType().Name + ': ' + $_.Exception.Message) }
}
