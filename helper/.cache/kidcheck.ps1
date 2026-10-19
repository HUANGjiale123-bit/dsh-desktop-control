Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class KC {
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@
$kids = New-Object System.Collections.ArrayList
$cb = [KC+EnumProc]{
  param($h,$l)
  $sb = New-Object System.Text.StringBuilder 256
  [void][KC]::GetClassNameW($h, $sb, 256)
  [void]$script:kids.Add([pscustomobject]@{ H=$h; C=$sb.ToString() })
  return $true
}
[void][KC]::EnumChildWindows([IntPtr]66988, $cb, [IntPtr]::Zero)
foreach ($k in $kids) { Write-Output ("  " + $k.C.PadRight(30) + " hwnd=" + $k.H.ToInt64()) }
