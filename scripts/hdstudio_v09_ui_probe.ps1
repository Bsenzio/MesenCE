param(
    [int]$ProcessId,
    [int]$TimeoutSeconds = 20
)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$main = $null
while((Get-Date) -lt $deadline -and $main -eq $null) {
    Start-Sleep -Milliseconds 300
    $main = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $pc)
}
if($main -eq $null) { Write-Output '{"ok":false,"reason":"main window not found"}'; exit 1 }
$buttonCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'RetroArchaeology')
$button = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
if($button -eq $null) { Write-Output '{"ok":false,"reason":"RetroArchaeology button not found"}'; exit 2 }
try {
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
} catch {
    Write-Output ('{"ok":false,"reason":"invoke failed: ' + ($_.Exception.Message -replace '"','') + '"}')
    exit 3
}
$arch = $null
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while((Get-Date) -lt $deadline -and $arch -eq $null) {
    Start-Sleep -Milliseconds 300
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pc)
    $arch = $windows | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Window' -and $_.Current.Name -like 'RetroArchaeology*' } | Select-Object -First 1
}
if($arch -eq $null) { Write-Output '{"ok":false,"reason":"archaeology window not found"}'; exit 4 }
$texts = $arch.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }
$hasRun = ($texts -contains 'Run Archaeology')
$hasCatalog = ($texts -contains 'Graphical catalog')
$hasEvidence = ($texts -contains 'EVIDENCE FIRST')
Write-Output ('{"ok":true,"title":"' + ($arch.Current.Name -replace '"','') + '","runButton":' + $hasRun.ToString().ToLower() + ',"catalog":' + $hasCatalog.ToString().ToLower() + ',"evidence":' + $hasEvidence.ToString().ToLower() + '}')
