# Windows clipboard history (Win+V) through WinRT, for POC experiment E8.
# Windows PowerShell 5.1 only (powershell.exe): PowerShell 7 cannot load WinRT
# types this way. Prints JSON with each item's id, timestamp and format names.
# It never reads item contents: the history can hold the user's private data.
#
#   powershell.exe -NoProfile -File clipboard_history.ps1            # list
#   powershell.exe -NoProfile -File clipboard_history.ps1 -Delete ID # remove one item
#
# WARNING: -Delete calls Clipboard.DeleteItemFromHistory on one item. The one
# E8 run that used it ended with the whole history (25 items) gone, most likely
# because of it. Do not use it on a history you care about.
param([string]$Delete)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.ApplicationModel.DataTransfer.Clipboard, Windows.ApplicationModel.DataTransfer, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Wait-WinRT($op, [Type]$type) {
    $task = $asTask.MakeGenericMethod($type).Invoke($null, @($op))
    $null = $task.Wait(10000)
    $task.Result
}
$C = [Windows.ApplicationModel.DataTransfer.Clipboard]
$result = Wait-WinRT ($C::GetHistoryItemsAsync()) ([Windows.ApplicationModel.DataTransfer.ClipboardHistoryItemsResult])
$out = [ordered]@{
    history_enabled = $C::IsHistoryEnabled()
    status = "$($result.Status)"
    deleted = $null
    items = @()
}
foreach ($item in $result.Items) {
    if ($Delete -and $item.Id -eq $Delete) {
        $out.deleted = $C::DeleteItemFromHistory($item)
        continue
    }
    $out.items += [ordered]@{
        id = $item.Id
        timestamp = $item.Timestamp.ToUniversalTime().ToString('o')
        formats = @($item.Content.AvailableFormats)
    }
}
$out | ConvertTo-Json -Depth 4 -Compress
