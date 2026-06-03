param(
    [string]$Query = "",
    [int]$Limit = 80
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Normalize-Name([string]$Name) {
    return (($Name -split "[\r\n\t ]+" | Where-Object { $_ }) -join " ").Trim()
}

function Is-Clickable($ControlType) {
    $name = $ControlType.ProgrammaticName
    return $name -in @(
        "ControlType.Button",
        "ControlType.Hyperlink",
        "ControlType.MenuItem",
        "ControlType.TabItem",
        "ControlType.ListItem",
        "ControlType.Edit",
        "ControlType.ComboBox",
        "ControlType.CheckBox",
        "ControlType.RadioButton",
        "ControlType.SplitButton"
    )
}

$cursor = [System.Windows.Forms.Cursor]::Position
$screens = [System.Windows.Forms.Screen]::AllScreens |
    Sort-Object @{ Expression = { -[int]$_.Bounds.Contains($cursor) } }, @{ Expression = { $_.Bounds.Left } }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)

Write-Host "Cursor: $($cursor.X),$($cursor.Y)"
Write-Host "Screens:"
for ($screenIndex = 0; $screenIndex -lt $screens.Count; $screenIndex++) {
    $screen = $screens[$screenIndex]
    $bounds = $screen.Bounds
    Write-Host ("  screen {0}: left={1} top={2} width={3} height={4} cursor={5}" -f ($screenIndex + 1), $bounds.Left, $bounds.Top, $bounds.Width, $bounds.Height, $bounds.Contains($cursor))
}

$rows = New-Object System.Collections.Generic.List[object]
$queryPattern = if ([string]::IsNullOrWhiteSpace($Query)) { $null } else { [regex]::Escape($Query).Replace("\ ", ".*") }

for ($index = 0; $index -lt $all.Count; $index++) {
    $element = $all.Item($index)
    try {
        $name = Normalize-Name $element.Current.Name
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        if ($queryPattern -and $name -notmatch $queryPattern) {
            continue
        }

        $rect = $element.Current.BoundingRectangle
        if ($rect.IsEmpty -or $rect.Width -lt 4 -or $rect.Height -lt 4) {
            continue
        }

        $screenNumber = 0
        for ($screenIndex = 0; $screenIndex -lt $screens.Count; $screenIndex++) {
            $bounds = $screens[$screenIndex].Bounds
            if ($rect.Right -ge $bounds.Left -and $rect.Left -le $bounds.Right -and $rect.Bottom -ge $bounds.Top -and $rect.Top -le $bounds.Bottom) {
                $screenNumber = $screenIndex + 1
                break
            }
        }

        if ($screenNumber -eq 0) {
            continue
        }

        $centerX = $rect.Left + $rect.Width / 2
        $centerY = $rect.Top + $rect.Height / 2
        $rows.Add([pscustomobject]@{
            Screen = $screenNumber
            Name = $name
            Type = $element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")
            Clickable = Is-Clickable $element.Current.ControlType
            Left = [Math]::Round($rect.Left, 1)
            Top = [Math]::Round($rect.Top, 1)
            Width = [Math]::Round($rect.Width, 1)
            Height = [Math]::Round($rect.Height, 1)
            CenterX = [Math]::Round($centerX, 1)
            CenterY = [Math]::Round($centerY, 1)
        })
    } catch {
    }
}

Write-Host ""
Write-Host "Matching accessible elements: $($rows.Count)"
$rows |
    Sort-Object @{ Expression = { -[int]$_.Clickable } }, Screen, Top, Left |
    Select-Object -First $Limit |
    Format-Table -AutoSize
