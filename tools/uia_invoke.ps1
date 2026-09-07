param([string]$Id, [string]$ShotName = "", [int]$WaitMs = 400)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$p = Get-Process AuroraPlayer -EA SilentlyContinue
if (-not $p) { Write-Output 'NOPROC'; exit }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$winCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
if (-not $win) { Write-Output 'WIN NOT FOUND'; exit }

$idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
if (-not $el) { Write-Output "ELEMENT NOT FOUND: $Id"; exit }

try {
  $inv = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $inv.Invoke()
  Write-Output "invoked: $Id"
} catch {
  # 尝试 SelectionItem（ListBox 等）
  try {
    $sel = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    Write-Output "selected: $Id"
  } catch { Write-Output "no pattern for: $Id" }
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
