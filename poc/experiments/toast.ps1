# Show or remove a test toast notification, for POC experiment E19.
# Windows PowerShell 5.1 only (powershell.exe): PowerShell 7 cannot load WinRT
# types this way. The toast is sent as Windows PowerShell's own app id, tagged
# so it can be removed from the notification history again.
#
#   powershell.exe -NoProfile -File toast.ps1 -Show     # show the test toast
#   powershell.exe -NoProfile -File toast.ps1 -Remove   # remove it from history
param([switch]$Show, [switch]$Remove)
$ErrorActionPreference = 'Stop'
$null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
$null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
$app = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
if ($Show) {
    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
    $xml.LoadXml('<toast><visual><binding template="ToastGeneric"><text>Paint presentator test</text><text>E19: does this show over Paint?</text></binding></visual></toast>')
    $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
    $toast.Tag = 'e19'
    $toast.Group = 'presentator'
    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($app).Show($toast)
    'shown'
}
if ($Remove) {
    [Windows.UI.Notifications.ToastNotificationManager]::History.Remove('e19', 'presentator', $app)
    'removed'
}
