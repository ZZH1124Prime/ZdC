param([Parameter(Mandatory)][string]$BuildDirectory)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$outputPath = (Resolve-Path -LiteralPath $BuildDirectory).Path
[void][Reflection.Assembly]::LoadFrom((Join-Path $outputPath 'ZdC.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $outputPath 'ZdC.dll'))

# Load only the error label from the card template. No main window or manager is constructed.
$app = [Windows.Application]::new()
foreach ($key in 'Muted', 'StatusWarning', 'StatusSuccess') {
    $app.Resources.Add($key, [Windows.Media.Brushes]::Gray)
}
$xaml = [xml][IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../src/ZdC.App/MainWindow.xaml'))
$ns = [Xml.XmlNamespaceManager]::new($xaml.NameTable)
$ns.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$element = $xaml.SelectSingleNode("//*[@x:Name='OperationErrorText']", $ns)
$label = [Windows.Markup.XamlReader]::Parse($element.OuterXml)
$entry = [ZdC.Core.RegisteredApplication]::new('fixture', 'C:\fixture.exe', $null, $null)
$row = [ZdC.App.ApplicationRow]::new($entry, $null)
$label.DataContext = $row
$row.SetState($true, $false, $false)

function Check([scriptblock]$Condition, [string]$Name) {
    [void]$label.Dispatcher.Invoke([Windows.Threading.DispatcherPriority]::ApplicationIdle, [Action]{})
    if (-not (& $Condition)) { throw $Name }
    Write-Output "PASS $Name"
}

Check { $label.Visibility -eq 'Collapsed' } 'healthy card hides the error label'
$timeout = [TimeoutException]::new().Message
$row.BeginOperation()
$row.FailOperation($timeout)
$row.EndOperation()
Check { $label.Text -ceq $timeout -and $label.Visibility -eq 'Visible' -and $row.TelemetryVisibility -eq 'Collapsed' } 'card shows the original timeout after an operation ends'

$observed = [ZdC.Core.ApplicationSessionState]::new($true, $true, $null)
$observed.ConnectionStatus = [ZdC.Core.ApplicationConnectionStatus]::DirectObserved
$observed.LatencyMs = 15
$row.SetState($true, $true, $true)
Check { $row.Status -ceq $timeout -and $label.Text -ceq $timeout } 'state refresh preserves the error before telemetry arrives'
$row.SetTelemetry($observed)
$row.FailOperation('observation failure', $true)
$external = [ZdC.Core.ApplicationSessionState]::new($false, $false, $null)
$external.IsExternalProcessRunning = $true
$row.SetTelemetry($external)
Check { $label.Text -ceq $timeout -and $label.Visibility -eq 'Visible' -and $row.Status -ceq $timeout } 'successful and failed background observations preserve the same error'

$row.ClearOperationFailure()
Check { $label.Visibility -eq 'Collapsed' -and $row.TelemetryVisibility -eq 'Visible' } 'the next manual stop clears the error label'
$failure = [UnauthorizedAccessException]::new().Message
$row.FailOperation($failure)
$row.SetState($false, $false, $false)
$row.SetTelemetry($external)
Check { $label.Text -ceq $failure -and $label.Visibility -eq 'Visible' } 'a stop failure stays visible through a missing-entry refresh'
$row.BeginOperation()
Check { $label.Visibility -eq 'Collapsed' -and -not $row.HasOperationFailure } 'the next icon operation clears the error label'
$row.EndOperation()
$row.FailOperation('observation failure', $true)
$row.SetState($true, $false, $false)
$row.SetTelemetry($external)
Check { -not $row.HasOperationFailure -and $label.Visibility -eq 'Collapsed' } 'observation-only errors still recover'
