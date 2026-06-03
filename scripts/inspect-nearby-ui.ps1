param(
    [double]$X,
    [double]$Y,
    [double]$Radius = 450
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$results = New-Object System.Collections.Generic.List[object]

for ($index = 0; $index -lt $elements.Count; $index++) {
    $element = $elements.Item($index)
    try {
        $rect = $element.Current.BoundingRectangle
        $name = $element.Current.Name
        if ([string]::IsNullOrWhiteSpace($name) -or $rect.IsEmpty) {
            continue
        }

        $centerX = $rect.Left + $rect.Width / 2
        $centerY = $rect.Top + $rect.Height / 2
        $distance = [Math]::Sqrt([Math]::Pow($centerX - $X, 2) + [Math]::Pow($centerY - $Y, 2))
        if ($distance -le $Radius) {
            $results.Add([pscustomobject]@{
                Distance = [Math]::Round($distance, 1)
                Name = $name
                ControlType = $element.Current.ControlType.ProgrammaticName
                Left = [Math]::Round($rect.Left, 1)
                Top = [Math]::Round($rect.Top, 1)
                Width = [Math]::Round($rect.Width, 1)
                Height = [Math]::Round($rect.Height, 1)
            })
        }
    } catch {
    }
}

$results |
    Sort-Object Distance |
    Select-Object -First 40 |
    Format-Table -AutoSize
