$p = [System.Diagnostics.Process]::GetCurrentProcess()
$mods = $p.Modules | ForEach-Object { $_.ModuleName }
$mods | Sort-Object | Out-File -FilePath "D:\dsh workspace\dsh-desktop-control\helper\.cache\ps-modules.txt" -Encoding UTF8
Write-Output ("ps modules: " + $mods.Count)
