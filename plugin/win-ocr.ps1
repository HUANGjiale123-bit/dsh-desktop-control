param(
  [Parameter(Mandatory = $true)][string]$Path,
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
    $w0 = $words[0]
    $wN = $words[$words.Count - 1]
    $x = [double]$w0.BoundingRect.X
    $y = [double]$w0.BoundingRect.Y
    $right = [double]($wN.BoundingRect.X + $wN.BoundingRect.Width)
    $bottom = [double]($wN.BoundingRect.Y + $wN.BoundingRect.Height)

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
