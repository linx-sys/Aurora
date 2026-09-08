param([string]$Id = "", [string]$ShotName = "", [int]$WaitMs = 400, [string]$Text = "")
# 通用 UIA 驱动：按 AutomationId 调用按钮 / 设置文本
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$p = Get-Process AuroraPlayer -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'NOPROC'; exit }

if ($Id -ne "") {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $winCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
    if (-not $win) { Write-Output 'WIN NOT FOUND'; exit }
    $idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
    if (-not $el) { Write-Output "NOT FOUND: $Id"; exit }
    try {
        ($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
        Write-Output "invoked: $Id"
    } catch {
        try {
            $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $vp.SetValue($Text)
            Write-Output "set text: $Id = $Text"
        } catch { Write-Output "no pattern: $Id" }
    }
}
Start-Sleep -Milliseconds $WaitMs
if ($ShotName -ne "") {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
    $bmp.Save("D:\WorkSpace\MP3Player\$ShotName", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "$ShotName saved"
}
