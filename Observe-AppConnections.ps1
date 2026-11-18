# 只读观察现有应用的 TCP 连接，不加载 DLL、不解析域名、不改变网络。
# 运行：.\Observe-AppConnections.ps1 -Application Weixin -Seconds 30
# Application 可选 Weixin、Browser、Both。Ctrl+C 提前结束，不留后台任务或日志文件。
# 本地 IP 对应接口仅说明 socket 的本地地址；公网出口需另有服务端回显证据。
# 此脚本不接管流量。TCP 表不含请求域名、DNS 调用或 UDP/QUIC 数据。
[CmdletBinding()]
param(
    [ValidateSet('Weixin', 'Browser', 'Both')][string]$Application = 'Both',
    [ValidateRange(1, 120)][int]$Seconds = 30
)
$ErrorActionPreference = 'Stop'
$names = switch ($Application) {
    Weixin { @('Weixin', 'WeChat', 'WeChatAppEx') }
    Browser { @('msedge', 'chrome', 'firefox') }
    Both { @('Weixin', 'WeChat', 'WeChatAppEx', 'msedge', 'chrome', 'firefox') }
}
$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
$previous = @{}
$samples = 0
Write-Host "只读 TCP 观察开始：$Application，最长 $Seconds 秒；不会切换直连。"
try {
    while ([DateTime]::UtcNow -lt $deadline) {
        # 每轮重新识别 PID，涵盖观察期间新启动的已知应用子进程。
        $processes = @{}
        Get-Process -Name $names -ErrorAction SilentlyContinue | ForEach-Object {
            $processes[[int]$_.Id] = $_.ProcessName
        }
        $interfaces = @{}
        Get-NetIPAddress | ForEach-Object {
            $interfaces[$_.IPAddress] = $_.InterfaceIndex
        }
        $current = @{}
        # 只输出所选应用的地址元数据，其他进程记录不保存、不分析。
        Get-NetTCPConnection | Where-Object {
            $processes.ContainsKey([int]$_.OwningProcess) -and $_.RemotePort -ne 0
        } | ForEach-Object {
            $connection = $_
            $key = '{0}|{1}|{2}|{3}|{4}|{5}' -f $connection.OwningProcess,
                $connection.LocalAddress, $connection.LocalPort,
                $connection.RemoteAddress, $connection.RemotePort, $connection.State
            $current[$key] = $true
            if (-not $previous.ContainsKey($key)) {
                [pscustomobject]@{
                    Time = [DateTime]::Now.ToString('HH:mm:ss.fff')
                    Observation = $(if ($samples -eq 0) { 'BASELINE' } else { 'NEW_OR_CHANGED' })
                    Application = $processes[[int]$connection.OwningProcess]
                    PID = $connection.OwningProcess
                    LocalAddress = $connection.LocalAddress
                    LocalPort = $connection.LocalPort
                    LocalInterfaceIndex = $interfaces[$connection.LocalAddress]
                    RemoteAddress = $connection.RemoteAddress
                    RemotePort = $connection.RemotePort
                    State = [string]$connection.State
                } | ConvertTo-Json -Compress
            }
        }
        # 仅保留上一轮键集合，观察历史不累积。
        $previous = $current
        $samples++
        Start-Sleep -Milliseconds 750
    }
}
finally {
    $previous.Clear()
    Write-Host "观察结束，共 $samples 轮；没有驱动、后台会话或日志文件需要清理。"
}
