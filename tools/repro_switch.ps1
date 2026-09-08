param([int]$Switches = 6)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Start-Process -FilePath 'D:\WorkSpace\MP3Player\build\AuroraPlayer.exe' -ArgumentList '"D:\WorkSpace\MP3Player\testmusic\01-夜航星.mp3"' -PassThru
Start-Sleep -Seconds 6

for ($i = 1; $i -le $Switches; $i++) {
    $p.Refresh()
    if ($p.HasExited) { Write-Output ("CRASHED at switch " + ($i-1)); break }
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $winCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
    if (-not $win) { Write-Output ("window gone at switch " + $i); break }
    $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BtnNext')
    $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
    if (-not $btn) { Write-Output 'BtnNext not found'; break }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Write-Output ("switch $i ok")
    Start-Sleep -Seconds 3
}
$p.Refresh()
Write-Output ("final alive: " + (-not $p.HasExited))
if ($p.HasExited) {
    Start-Sleep -Seconds 1
    Get-Content "$env:TEMP\aurora_crash.log" -Tail 25
}
