$q = Get-CimInstance Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -ne 0 }
"problem devices total: $(@($q).Count)"
foreach ($d in $q) { "{0} | {1} | {2} | code={3}" -f $d.Name, $d.PNPClass, $d.DeviceID, $d.ConfigManagerErrorCode }
"--- net class devices:"
Get-CimInstance Win32_PnPEntity | Where-Object { $_.PNPClass -eq 'Net' } | ForEach-Object { "{0} | code={1}" -f $_.Name, $_.ConfigManagerErrorCode }