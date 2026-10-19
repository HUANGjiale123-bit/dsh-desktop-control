Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class SO {
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@
$kids = New-Object System.Collections.ArrayList
$cb = [SO+EnumProc]{
  param($h,$l)
  $sb = New-Object System.Text.StringBuilder 256
  [void][SO]::GetClassNameW($h, $sb, 256)
  [void]$script:kids.Add([pscustomobject]@{ H=$h; C=$sb.ToString() })
  return $true
}
[void][SO]::EnumChildWindows([IntPtr]66988, $cb, [IntPtr]::Zero)
Write-Output "WITHOUT UIA loaded:"
foreach ($k in $kids) {
  $r = [SO]::SendMessage($k.H, 0x003D, [IntPtr]::Zero, [IntPtr]::new(-4))
  Write-Output ("  " + $k.C.PadRight(30) + " -> " + $r)
}
