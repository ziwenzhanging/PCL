Imports System.Security.Principal
Imports System.Text
Imports Newtonsoft.Json
Imports Newtonsoft.Json.Linq

''' <summary>
''' EasyTier 组网模块。
''' 负责组件定位、配置读写、核心进程生命周期与 CLI 状态读取。
''' </summary>
Public Module ModEasyTier

#Region "运行状态"

    ''' <summary>
    ''' EasyTier 核心的运行状态。
    ''' </summary>
    Public Enum EasyTierStates
        ''' <summary>已停止。</summary>
        Stopped
        ''' <summary>正在启动。</summary>
        Starting
        ''' <summary>正在运行。</summary>
        Running
        ''' <summary>正在停止。</summary>
        Stopping
        ''' <summary>启动失败。</summary>
        Failed
    End Enum

    Private _State As EasyTierStates = EasyTierStates.Stopped
    ''' <summary>
    ''' 当前的运行状态。
    ''' </summary>
    Public Property State As EasyTierStates
        Get
            Return _State
        End Get
        Private Set(value As EasyTierStates)
            If _State = value Then Return
            _State = value
            Logger.Info($"EasyTier 状态切换为 {value}")
            RaiseStateChanged(value)
        End Set
    End Property

    Private ReadOnly StateHandlers As New List(Of Action(Of EasyTierStates))
    ''' <summary>
    ''' 订阅运行状态的变化。回调可能在任意线程触发。
    ''' </summary>
    Public Sub AddStateHandler(Handler As Action(Of EasyTierStates))
        SyncLock StateHandlers
            If Not StateHandlers.Contains(Handler) Then StateHandlers.Add(Handler)
        End SyncLock
    End Sub
    ''' <summary>
    ''' 取消订阅运行状态的变化。
    ''' </summary>
    Public Sub RemoveStateHandler(Handler As Action(Of EasyTierStates))
        SyncLock StateHandlers
            StateHandlers.Remove(Handler)
        End SyncLock
    End Sub
    Private Sub RaiseStateChanged(NewState As EasyTierStates)
        Dim Snapshot As Action(Of EasyTierStates)()
        SyncLock StateHandlers
            Snapshot = StateHandlers.ToArray()
        End SyncLock
        For Each Handler In Snapshot
            Try
                Handler(NewState)
            Catch ex As Exception
                Logger.Warn(ex, "处理 EasyTier 状态改变事件失败")
            End Try
        Next
    End Sub

    ''' <summary>
    ''' 最近一次启动失败的原因，供界面直接显示。
    ''' </summary>
    Public Property LastError As String = Nothing

#End Region

#Region "日志"

    Private Const MaxLogLines As Integer = 500
    Private ReadOnly LogBuffer As New List(Of String)
    Private ReadOnly LogHandlers As New List(Of Action(Of String))

    ''' <summary>
    ''' 读取现有的全部日志行（副本）。
    ''' </summary>
    Public Function GetLogs() As List(Of String)
        SyncLock LogBuffer
            Return New List(Of String)(LogBuffer)
        End SyncLock
    End Function

    ''' <summary>
    ''' 清空日志缓冲。
    ''' </summary>
    Public Sub ClearLogs()
        SyncLock LogBuffer
            LogBuffer.Clear()
        End SyncLock
    End Sub

    ''' <summary>
    ''' 订阅新的日志行。回调可能在任意线程触发。
    ''' </summary>
    Public Sub AddLogHandler(Handler As Action(Of String))
        SyncLock LogHandlers
            If Not LogHandlers.Contains(Handler) Then LogHandlers.Add(Handler)
        End SyncLock
    End Sub
    ''' <summary>
    ''' 取消订阅日志。
    ''' </summary>
    Public Sub RemoveLogHandler(Handler As Action(Of String))
        SyncLock LogHandlers
            LogHandlers.Remove(Handler)
        End SyncLock
    End Sub

    ''' <summary>
    ''' 写入一行日志，同时转发给所有订阅者，并写入独立的 EasyTier 日志文件。
    ''' </summary>
    Public Sub WriteLog(Line As String)
        If Line Is Nothing Then Return
        ' 从核心日志里捕捉 DHCP 地址协商事件，这比按时间猜地址何时稳定可靠得多
        ' 对应核心里的事件：DhcpIpv4Changed / DhcpIpv4Conflicted
        If Line.IndexOf("dhcp ip changed", StringComparison.OrdinalIgnoreCase) >= 0 Then
            LastDhcpEventAt = Now
        ElseIf Line.IndexOf("dhcp ip conflict", StringComparison.OrdinalIgnoreCase) >= 0 Then
            LastDhcpEventAt = Now
            DhcpConflictCount += 1
        End If
        SyncLock LogBuffer
            LogBuffer.Add(Line)
            Do While LogBuffer.Count > MaxLogLines
                LogBuffer.RemoveAt(0)
            Loop
        End SyncLock
        WriteLogFile(Line)
        Dim Snapshot As Action(Of String)()
        SyncLock LogHandlers
            Snapshot = LogHandlers.ToArray()
        End SyncLock
        For Each Handler In Snapshot
            Try
                Handler(Line)
            Catch ex As Exception
                Logger.Warn(ex, "转发 EasyTier 日志失败")
            End Try
        Next
    End Sub

    ''' <summary>
    ''' 最后一次 DHCP 地址变更或冲突的时间。
    ''' </summary>
    Public Property LastDhcpEventAt As DateTime = DateTime.MinValue
    ''' <summary>
    ''' 本次运行中 DHCP 发生地址冲突的次数。
    ''' </summary>
    Public Property DhcpConflictCount As Integer = 0
    ''' <summary>
    ''' DHCP 事件之后等待多久认为地址已经稳定。
    ''' </summary>
    Private Const DhcpSettleSeconds As Double = 4

    ''' <summary>
    ''' 虚拟地址是否仍处于 DHCP 协商中。
    ''' 此时界面不应显示具体地址，否则会看到「先是一个已被占用的地址，然后才变正确」的跳变。
    ''' </summary>
    Public ReadOnly Property IsDhcpSettling As Boolean
        Get
            If Not HasTomlConfig Then Return False
            If LastDhcpEventAt = DateTime.MinValue Then Return False
            Return (Now - LastDhcpEventAt).TotalSeconds < DhcpSettleSeconds
        End Get
    End Property

#End Region

#Region "独立日志文件"

    ''' <summary>
    ''' EasyTier 专用日志目录，以 \ 结尾。所有 EasyTier 输出都会单独写到这里，不混进 PCL 主日志。
    ''' </summary>
    Public ReadOnly Property PathEasyTierLogDir As String
        Get
            Return PathEasyTierData & "logs\"
        End Get
    End Property

    ''' <summary>
    ''' 本次运行对应的日志文件路径；尚未启动过时为 Nothing。
    ''' </summary>
    Public Property CurrentLogFile As String = Nothing

    Private LogWriter As IO.StreamWriter = Nothing
    Private ReadOnly LogWriterLock As New Object()
    Private Const MaxLogFiles As Integer = 10

    ''' <summary>
    ''' 开始一个新日志文件，并清理过旧的日志。
    ''' </summary>
    Private Sub OpenLogFile()
        SyncLock LogWriterLock
            CloseLogFileInternal()
            Try
                DirectoryUtils.Create(PathEasyTierLogDir)
                ' 只保留最近若干个日志文件
                Try
                    Dim Existed = IO.Directory.GetFiles(PathEasyTierLogDir, "easytier-*.log")
                    If Existed.Length >= MaxLogFiles Then
                        For Each OldFile In Existed.OrderBy(Function(f) IO.File.GetLastWriteTime(f)).Take(Existed.Length - MaxLogFiles + 1)
                            Try
                                IO.File.Delete(OldFile)
                            Catch
                            End Try
                        Next
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, "清理过旧的 EasyTier 日志失败")
                End Try
                CurrentLogFile = PathEasyTierLogDir & $"easytier-{Now:yyyyMMdd-HHmmss}.log"
                LogWriter = New IO.StreamWriter(CurrentLogFile, False, New UTF8Encoding(False)) With {.AutoFlush = True}
                LogWriter.WriteLine($"===== EasyTier 日志 {Now:yyyy-MM-dd HH:mm:ss} =====")
                LogWriter.WriteLine($"核心：{PathEasyTierCore}")
                LogWriter.WriteLine()
            Catch ex As Exception
                Logger.Warn(ex, "创建 EasyTier 日志文件失败")
                LogWriter = Nothing
                CurrentLogFile = Nothing
            End Try
        End SyncLock
    End Sub

    ''' <summary>
    ''' 结束当前日志文件。
    ''' </summary>
    Public Sub CloseLogFile()
        SyncLock LogWriterLock
            CloseLogFileInternal()
        End SyncLock
    End Sub

    Private Sub CloseLogFileInternal()
        If LogWriter Is Nothing Then Return
        Try
            LogWriter.WriteLine()
            LogWriter.WriteLine($"===== 日志结束 {Now:yyyy-MM-dd HH:mm:ss} =====")
            LogWriter.Dispose()
        Catch
        Finally
            LogWriter = Nothing
        End Try
    End Sub

    Private Sub WriteLogFile(Line As String)
        SyncLock LogWriterLock
            If LogWriter Is Nothing Then Return
            Try
                LogWriter.WriteLine($"[{Now:HH:mm:ss.fff}] {Line}")
            Catch
            End Try
        End SyncLock
    End Sub

#End Region

#Region "进程"

    ''' <summary>
    ''' 保证路径以 \ 结尾。
    ''' </summary>
    Private Function EnsureSlash(Path As String) As String
        If Path Is Nothing Then Return "\"
        If Path.EndsWith("\") Then Return Path
        Return Path & "\"
    End Function

    ''' <summary>
    ''' EasyTier 组件所在目录，以 \ 结尾。
    ''' 优先使用程序目录下的 EasyTier 文件夹，其次使用 AppData 下的文件夹。
    ''' </summary>
    Public ReadOnly Property PathEasyTier As String
        Get
            Dim PortablePath = EnsureSlash(Paths.Base & "EasyTier")
            If FileUtils.Exists(PortablePath & "easytier-core.exe") Then Return PortablePath
            Dim AppDataPath = EnsureSlash(Paths.AppDataThenName & "EasyTier")
            If FileUtils.Exists(AppDataPath & "easytier-core.exe") Then Return AppDataPath
            Return PortablePath
        End Get
    End Property

    ''' <summary>
    ''' 存放配置文件与运行数据的目录，以 \ 结尾。始终可写。
    ''' </summary>
    Public ReadOnly Property PathEasyTierData As String
        Get
            Return EnsureSlash(Paths.AppDataThenName & "EasyTierData")
        End Get
    End Property

    ''' <summary>
    ''' EasyTier 核心可执行文件路径。
    ''' </summary>
    Public ReadOnly Property PathEasyTierCore As String
        Get
            Return PathEasyTier & "easytier-core.exe"
        End Get
    End Property

    ''' <summary>
    ''' 配置文件路径。
    ''' </summary>
    Public ReadOnly Property PathEasyTierConfig As String
        Get
            Return PathEasyTierData & "config.json"
        End Get
    End Property

    ''' <summary>
    ''' easytier.toml 的候选位置，按优先级排列。
    ''' </summary>
    Public ReadOnly Property TomlCandidates As List(Of String)
        Get
            Return New List(Of String) From {
                EnsureSlash(Paths.Base & "EasyTier") & "easytier.toml",
                EnsureSlash(Paths.Base) & "easytier.toml",
                EnsureSlash(Paths.AppDataThenName & "EasyTierData") & "easytier.toml"
            }
        End Get
    End Property

    ''' <summary>
    ''' 当前实际使用的 easytier.toml 路径；一个都没找到时为 Nothing。
    ''' </summary>
    Public ReadOnly Property PathEasyTierToml As String
        Get
            For Each Candidate In TomlCandidates
                If FileUtils.Exists(Candidate) Then Return Candidate
            Next
            Return Nothing
        End Get
    End Property

    ''' <summary>
    ''' 是否存在可用的 easytier.toml。存在时一切网络参数都以该文件为准。
    ''' </summary>
    Public ReadOnly Property HasTomlConfig As Boolean
        Get
            Return PathEasyTierToml IsNot Nothing
        End Get
    End Property

    ''' <summary>
    ''' 从 easytier.toml 中解析监听端口，仅用于界面展示。解析失败返回 0。
    ''' </summary>
    Public ReadOnly Property TomlListenerPort As Integer
        Get
            If Not HasTomlConfig Then Return 0
            Try
                Dim Text = FileUtils.ReadAsString(PathEasyTierToml)
                If Text Is Nothing Then Return 0
                Dim Match = System.Text.RegularExpressions.Regex.Match(Text, "(?s)listeners\s*=\s*\[(.*?)\]")
                If Not Match.Success Then Return 0
                Dim PortMatch = System.Text.RegularExpressions.Regex.Match(Match.Groups(1).Value, ":(\d{2,5})")
                If Not PortMatch.Success Then Return 0
                Return Val(PortMatch.Groups(1).Value)
            Catch
                Return 0
            End Try
        End Get
    End Property

    ''' <summary>
    ''' 读取 easytier.toml 的文本内容；未找到或读取失败时返回空字符串。
    ''' </summary>
    Public Function ReadTomlText() As String
        Dim TomlPath = PathEasyTierToml
        If TomlPath Is Nothing Then Return ""
        Try
            Return FileUtils.ReadAsString(TomlPath)
        Catch ex As Exception
            Logger.Warn(ex, "读取 easytier.toml 失败")
            Return ""
        End Try
    End Function

    ''' <summary>
    ''' 从 easytier.toml 中读取指定键的字符串值；未找到时返回 Nothing。
    ''' </summary>
    Public Function GetTomlString(Key As String) As String
        Dim TomlPath = PathEasyTierToml
        If TomlPath Is Nothing Then Return Nothing
        Try
            Dim Text = FileUtils.ReadAsString(TomlPath)
            If Text Is Nothing Then Return Nothing
            Dim Pattern = "(?im)^\s*" & System.Text.RegularExpressions.Regex.Escape(Key) & "\s*=\s*""([^""]*)"""
            Dim Match = System.Text.RegularExpressions.Regex.Match(Text, Pattern)
            If Match.Success Then Return Match.Groups(1).Value
            Return Nothing
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 统计 easytier.toml 中会被跳过的 https 对等节点数量。
    ''' EasyTier 2.7.0 的 rustls 存在 CryptoProvider 缺陷，连接 https:// 节点会让核心直接崩溃。
    ''' </summary>
    Public Function CountSkippedHttpsPeers() As Integer
        Dim Source = PathEasyTierToml
        If Source Is Nothing Then Return 0
        Try
            Dim Text = FileUtils.ReadAsString(Source)
            If Text Is Nothing Then Return 0
            Return CountHttpsPeerBlocks(Text)
        Catch
            Return 0
        End Try
    End Function

    Private Function CountHttpsPeerBlocks(Text As String) As Integer
        Dim Lines = Text.Replace(vbCrLf, vbLf).Split(ChrW(10))
        Dim Count As Integer = 0
        For i = 0 To Lines.Length - 2
            If Lines(i).Trim() = "[[peer]]" AndAlso Lines(i + 1).Trim().StartsWith("uri = ""https://", StringComparison.OrdinalIgnoreCase) Then
                Count += 1
            End If
        Next
        Return Count
    End Function

    ''' <summary>
    ''' 生成实际用于启动的配置文件：把会让核心崩溃的 https 对等节点剔除掉，其余原样保留。
    ''' 没有任何需要剔除的内容时，直接返回原文件路径。
    ''' </summary>
    Public Function PrepareRuntimeToml() As String
        Dim Source = PathEasyTierToml
        If Source Is Nothing Then Return Nothing
        Try
            Dim Text = FileUtils.ReadAsString(Source)
            If Text Is Nothing Then Return Source
            Dim Lines = Text.Replace(vbCrLf, vbLf).Split(ChrW(10))
            Dim Output As New List(Of String)
            Dim Skipped As Integer = 0
            Dim i As Integer = 0
            While i < Lines.Length
                ' [[peer]] 与紧随其后的 uri 成对，遇到 https 地址就整块跳过
                If Lines(i).Trim() = "[[peer]]" AndAlso i + 1 < Lines.Length AndAlso
                   Lines(i + 1).Trim().StartsWith("uri = ""https://", StringComparison.OrdinalIgnoreCase) Then
                    i += 2
                    Skipped += 1
                    Continue While
                End If
                Output.Add(Lines(i))
                i += 1
            End While
            If Skipped = 0 Then Return Source
            DirectoryUtils.Create(PathEasyTierData)
            Dim RuntimePath As String = PathEasyTierData & "easytier.runtime.toml"
            FileUtils.Write(RuntimePath, String.Join(vbCrLf, Output))
            Logger.Info($"easytier.toml 中有 {Skipped} 条 https:// 对等节点，已从运行配置中跳过（核心 2.7.0 会因此崩溃）")
            Return RuntimePath
        Catch ex As Exception
            Logger.Warn(ex, "生成运行用 EasyTier 配置失败，将直接使用原文件")
            Return Source
        End Try
    End Function

    ''' <summary>
    ''' 本次启动是否需要创建虚拟网卡（决定是否需要管理员权限）。
    ''' </summary>
    Public ReadOnly Property NeedsTun As Boolean
        Get
            If HasTomlConfig Then
                Try
                    Dim Text = FileUtils.ReadAsString(PathEasyTierToml)
                    If Text Is Nothing Then Return True
                    Return Not System.Text.RegularExpressions.Regex.IsMatch(Text, "(?im)^\s*no_tun\s*=\s*true")
                Catch
                    Return True
                End Try
            End If
            Return Not Config.NoTun AndAlso (Config.Dhcp OrElse Not String.IsNullOrWhiteSpace(Config.Ipv4))
        End Get
    End Property

    ''' <summary>
    ''' 检查组件是否齐全，缺失时抛出带说明的异常。
    ''' </summary>
    Public Sub EnsureBinaries()
        Dim Dir As String = PathEasyTier
        If Not FileUtils.Exists(Dir & "easytier-core.exe") Then
            Throw New Exception($"未找到 EasyTier 核心组件（{Dir}easytier-core.exe）。{vbCrLf}请确认 EasyTier 文件夹与 PCL 位于同一目录，或在 PCL 设置中重新部署组网组件。")
        End If

        If Not FileUtils.Exists(Dir & "wintun.dll") Then
            Throw New Exception($"未找到虚拟网卡驱动（{Dir}wintun.dll）。{vbCrLf}缺少该 DLL 将无法创建虚拟网卡。")
        End If
    End Sub

    ''' <summary>
    ''' 组件是否已就位。
    ''' </summary>
    Public ReadOnly Property IsBinariesReady As Boolean
        Get
            Try
                EnsureBinaries()
                Return True
            Catch
                Return False
            End Try
        End Get
    End Property

    ''' <summary>
    ''' 组件缺失时的说明文字；组件齐全时返回 Nothing。
    ''' </summary>
    Public ReadOnly Property BinariesProblem As String
        Get
            Try
                EnsureBinaries()
                Return Nothing
            Catch ex As Exception
                Return ex.Message
            End Try
        End Get
    End Property

    ''' <summary>
    ''' 当前进程是否以管理员身份运行。
    ''' EasyTier 创建虚拟网卡需要管理员权限。
    ''' </summary>
    Public ReadOnly Property IsAdministrator As Boolean
        Get
            Try
                Dim Identity = WindowsIdentity.GetCurrent()
                Dim Principal As New WindowsPrincipal(Identity)
                Return Principal.IsInRole(WindowsBuiltInRole.Administrator)
            Catch ex As Exception
                Logger.Warn(ex, "检查管理员权限失败")
                Return False
            End Try
        End Get
    End Property

#End Region

#Region "配置"

    ''' <summary>
    ''' EasyTier 的一次组网配置。
    ''' </summary>
    Public Class EasyTierConfig
        ''' <summary>网络名称，同一网络中的节点必须一致。</summary>
        Public Property NetworkName As String = ""
        ''' <summary>网络密钥，同一网络中的节点必须一致。</summary>
        Public Property NetworkSecret As String = ""
        ''' <summary>本机名称，留空则由 EasyTier 自动生成。</summary>
        Public Property Hostname As String = ""
        ''' <summary>本机的虚拟 IPv4 地址，留空时不创建 TUN 设备。</summary>
        Public Property Ipv4 As String = ""
        ''' <summary>是否由 EasyTier 自动分配虚拟 IP。</summary>
        Public Property Dhcp As Boolean = True
        ''' <summary>最初要连接的对等节点，逗号或换行分隔。</summary>
        Public Property Peers As String = ""
        ''' <summary>监听端口，0 表示自动选择空闲端口。</summary>
        Public Property ListenerPort As Integer = 0
        ''' <summary>RPC 管理端口，0 表示自动选择空闲端口。</summary>
        Public Property RpcPort As Integer = 0
        ''' <summary>虚拟网卡 MTU，0 表示使用默认值。</summary>
        Public Property Mtu As Integer = 0
        ''' <summary>要导出到虚拟网络的本地子网，逗号或换行分隔。</summary>
        Public Property ProxyNetworks As String = ""
        ''' <summary>本地端口转发规则，逗号或换行分隔。</summary>
        Public Property PortForwards As String = ""
        ''' <summary>是否优先选择低延迟路径。</summary>
        Public Property LatencyFirst As Boolean = False
        ''' <summary>是否不创建虚拟网卡（仅做转发）。</summary>
        Public Property NoTun As Boolean = False
        ''' <summary>是否禁用 IPv6。</summary>
        Public Property DisableIpv6 As Boolean = False
        ''' <summary>是否转发物理网卡的 UDP 广播包，有助于局域网游戏发现房间。</summary>
        Public Property UdpBroadcastRelay As Boolean = True
        ''' <summary>是否启用私有模式。</summary>
        Public Property PrivateMode As Boolean = True
        ''' <summary>压缩算法，可选 none、zstd。</summary>
        Public Property Compression As String = "none"
        ''' <summary>TCP 端口白名单，逗号分隔。</summary>
        Public Property TcpWhitelist As String = ""
        ''' <summary>UDP 端口白名单，逗号分隔。</summary>
        Public Property UdpWhitelist As String = ""
        ''' <summary>加密算法，留空则使用默认的 aes-gcm。</summary>
        Public Property EncryptionAlgorithm As String = ""

        ''' <summary>
        ''' 复制一份配置。
        ''' </summary>
        Public Function Clone() As EasyTierConfig
            Return CType(MemberwiseClone(), EasyTierConfig)
        End Function
    End Class

    Private ConfigCache As EasyTierConfig = Nothing

    ''' <summary>
    ''' 当前使用的配置。若尚未读取，会先从磁盘加载。
    ''' </summary>
    Public Property Config As EasyTierConfig
        Get
            If ConfigCache Is Nothing Then LoadConfig()
            Return ConfigCache
        End Get
        Set(value As EasyTierConfig)
            ConfigCache = value
        End Set
    End Property

    ''' <summary>
    ''' 从磁盘读取配置；文件不存在或损坏时使用默认配置。
    ''' </summary>
    Public Sub LoadConfig()
        Dim Loaded As EasyTierConfig = Nothing
        Try
            If FileUtils.Exists(PathEasyTierConfig) Then
                Dim Raw = FileUtils.ReadAsString(PathEasyTierConfig)
                Loaded = JsonConvert.DeserializeObject(Of EasyTierConfig)(Raw)
            End If
        Catch ex As Exception
            Logger.Warn(ex, "读取 EasyTier 配置失败，将使用默认配置")
            Loaded = Nothing
        End Try
        If Loaded Is Nothing Then Loaded = New EasyTierConfig
        ' 首次使用时给出一个可用的默认网络名，避免用户面对空配置
        If String.IsNullOrWhiteSpace(Loaded.NetworkName) Then
            Loaded.NetworkName = "pcl_network"
        End If
        If String.IsNullOrWhiteSpace(Loaded.Hostname) Then
            Loaded.Hostname = GetDefaultHostname()
        End If
        ' 既没有固定 IP 又没有开启 DHCP 时，核心会因为「无地址却要建网卡」直接退出
        ' 这里自动补上 DHCP，保证开箱即可启动
        If Not Loaded.Dhcp AndAlso String.IsNullOrWhiteSpace(Loaded.Ipv4) Then
            Loaded.Dhcp = True
        End If
        ConfigCache = Loaded
    End Sub

    ''' <summary>
    ''' 把当前配置写入磁盘。
    ''' </summary>
    Public Sub SaveConfig()
        Try
            If ConfigCache Is Nothing Then ConfigCache = Config
            DirectoryUtils.Create(PathEasyTierData)
            FileUtils.Write(PathEasyTierConfig, JsonConvert.SerializeObject(ConfigCache, Formatting.Indented))
            Logger.Info($"EasyTier 配置已保存到 {PathEasyTierConfig}")
        Catch ex As Exception
            Logger.Warn(ex, "保存 EasyTier 配置失败")
        End Try
    End Sub

    ''' <summary>
    ''' 生成默认的主机名。
    ''' </summary>
    Private Function GetDefaultHostname() As String
        Try
            Dim Name = Environment.MachineName
            If String.IsNullOrWhiteSpace(Name) Then Name = "PCL"
            Return Name
        Catch
            Return "PCL"
        End Try
    End Function

#End Region

#Region "进程"

    Private ProcessCore As Process = Nothing
    Private ReadOnly ProcessLock As New Object
    ''' <summary>
    ''' 本次运行实际使用的配置文件路径（可能是剔除 https 节点后的临时文件）。
    ''' </summary>
    Private RuntimeTomlCache As String = Nothing

    ''' <summary>本次运行实际使用的监听端口。</summary>
    Public Property CurrentListenerPort As Integer = 0
    ''' <summary>本次运行实际使用的 RPC 端口。</summary>
    Public Property CurrentRpcPort As Integer = 0

    ''' <summary>
    ''' EasyTier 核心当前是否正在运行。
    ''' </summary>
    Public ReadOnly Property IsRunning As Boolean
        Get
            Dim ProcessRef = ProcessCore
            If ProcessRef Is Nothing Then Return False
            Try
                Return Not ProcessRef.HasExited
            Catch
                Return False
            End Try
        End Get
    End Property

    ''' <summary>
    ''' 把参数中的值安全地包成引号形式。
    ''' </summary>
    Private Function QuoteArgument(Value As String) As String
        If String.IsNullOrEmpty(Value) Then Return ""
        ' 去掉可能破坏命令行的换行
        Dim Cleaned = Value.Replace(vbCr, " ").Replace(vbLf, " ")
        If Cleaned.Contains("""") Then Cleaned = Cleaned.Replace("""", "\""")
        Return """" & Cleaned & """"
    End Function

    ''' <summary>
    ''' 把逗号、中文逗号或换行分隔的文本拆成列表。
    ''' </summary>
    Public Function SplitList(Value As String) As List(Of String)
        Dim Result As New List(Of String)
        If String.IsNullOrWhiteSpace(Value) Then Return Result
        For Each Item In Value.Replace(vbCr, vbLf).Split("，"c, ","c, vbLf, vbCr, " "c, ";"c, "；"c)
            Dim Trimmed = Item.Trim()
            If Trimmed.Length > 0 Then Result.Add(Trimmed)
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 根据当前配置生成命令行参数。
    ''' </summary>
    Public Function BuildArguments(Optional ConfigOverride As EasyTierConfig = Nothing) As String
        Dim C = If(ConfigOverride, Config)
        Dim Builder As New StringBuilder()
        ' 优先使用 easytier.toml：网络参数全部交给配置文件，PCL 只负责指定管理端口
        If HasTomlConfig Then
            Dim TomlForRun As String = If(RuntimeTomlCache, PathEasyTierToml)
            Builder.Append($" -c {QuoteArgument(TomlForRun)}")
            If CurrentRpcPort > 0 Then
                Builder.Append($" -r {CurrentRpcPort}")
                Builder.Append(" --rpc-portal-whitelist 127.0.0.1/32")
            End If
            Return Builder.ToString().Trim()
        End If
        ' 网络标识
        If Not String.IsNullOrWhiteSpace(C.NetworkName) Then
            Builder.Append($" --network-name={QuoteArgument(C.NetworkName)}")
        End If
        If Not String.IsNullOrWhiteSpace(C.NetworkSecret) Then
            Builder.Append($" --network-secret={QuoteArgument(C.NetworkSecret)}")
        End If
        If Not String.IsNullOrWhiteSpace(C.Hostname) Then
            Builder.Append($" --hostname={QuoteArgument(C.Hostname)}")
        End If
        ' 虚拟 IP
        If C.Dhcp Then
            Builder.Append(" -d")
        ElseIf Not String.IsNullOrWhiteSpace(C.Ipv4) Then
            Builder.Append($" -i {QuoteArgument(C.Ipv4.Trim())}")
        Else
            ' 既没有开启 DHCP 也没有填写地址时，绝不能尝试创建网卡，否则核心会立即退出
            Builder.Append(" --no-tun true")
        End If
        ' 监听与 RPC
        If CurrentListenerPort > 0 Then
            Builder.Append($" --listeners {CurrentListenerPort}")
        End If
        If CurrentRpcPort > 0 Then
            Builder.Append($" --rpc-portal {CurrentRpcPort} --rpc-portal-whitelist 127.0.0.1/32")
        End If
        ' 对等节点
        For Each Peer In SplitList(C.Peers)
            Builder.Append($" -p {QuoteArgument(Peer)}")
        Next
        ' 可选参数
        If C.Mtu > 0 Then Builder.Append($" --mtu {C.Mtu}")
        If C.LatencyFirst Then Builder.Append(" --latency-first")
        If C.NoTun Then Builder.Append(" --no-tun true")
        If C.DisableIpv6 Then Builder.Append(" --disable-ipv6 true")
        If C.UdpBroadcastRelay Then Builder.Append(" --enable-udp-broadcast-relay true")
        If C.PrivateMode Then Builder.Append(" --private-mode true")
        If Not String.IsNullOrWhiteSpace(C.Compression) AndAlso Not C.Compression.Equals("none", StringComparison.OrdinalIgnoreCase) Then
            Builder.Append($" --compression {QuoteArgument(C.Compression)}")
        End If
        If Not String.IsNullOrWhiteSpace(C.EncryptionAlgorithm) Then
            Builder.Append($" --encryption-algorithm {QuoteArgument(C.EncryptionAlgorithm)}")
        End If
        ' 子网代理
        For Each Network In SplitList(C.ProxyNetworks)
            Builder.Append($" -n {QuoteArgument(Network)}")
        Next
        ' 端口转发
        For Each Rule In SplitList(C.PortForwards)
            Builder.Append($" --port-forward {QuoteArgument(Rule)}")
        Next
        ' 白名单
        For Each Port In SplitList(C.TcpWhitelist)
            Builder.Append($" --tcp-whitelist={QuoteArgument(Port)}")
        Next
        For Each Port In SplitList(C.UdpWhitelist)
            Builder.Append($" --udp-whitelist={QuoteArgument(Port)}")
        Next
        Return Builder.ToString().Trim()
    End Function

    ''' <summary>
    ''' 启动 EasyTier 核心。失败时抛出异常。
    ''' </summary>
    ''' <param name="WaitReady">是否等待 RPC 端口可用后再返回。</param>
    Public Sub StartCore(Optional WaitReady As Boolean = True)
        SyncLock ProcessLock
            If IsRunning Then Throw New Exception("EasyTier 已经在运行了。")
            EnsureBinaries()
            ' 创建虚拟网卡需要管理员权限（配置文件里写了 no_tun = true 时不需要）
            If NeedsTun AndAlso Not IsAdministrator Then
                Throw New Exception("创建虚拟网卡需要管理员权限。" & vbCrLf & "请以管理员身份重新启动 PCL，或在 easytier.toml 中设置 no_tun = true。")
            End If
            ' 清理上一次运行残留的进程
            KillStaleProcesses()
            ' 分配端口：监听器会占用连续多个端口，因此预留 4 个
            Dim FreePorts = FindFreePorts(6, Config.ListenerPort, Config.ListenerPort + 1,
                                          Config.ListenerPort + 2, Config.ListenerPort + 3, Config.RpcPort)
            If HasTomlConfig Then
                CurrentListenerPort = TomlListenerPort
                CurrentRpcPort = If(Config.RpcPort > 0, Config.RpcPort, FreePorts(4))
            Else
                CurrentListenerPort = If(Config.ListenerPort > 0, Config.ListenerPort, FreePorts(0))
                CurrentRpcPort = If(Config.RpcPort > 0, Config.RpcPort, FreePorts(4))
            End If
            ' 准备启动
            LastError = Nothing
            LastDhcpEventAt = DateTime.MinValue
            DhcpConflictCount = 0
            ClearLogs()
            OpenLogFile()
            State = EasyTierStates.Starting
            RuntimeTomlCache = PrepareRuntimeToml()
            Dim Arguments As String = BuildArguments()
            Dim SkippedHttps = CountSkippedHttpsPeers()
            WriteLog($"[PCL] 配置文件：{If(PathEasyTierToml, "（未找到，使用 PCL 内置设置）")}")
            If SkippedHttps > 0 Then
                WriteLog($"[PCL] 已自动跳过 {SkippedHttps} 条 https:// 对等节点（EasyTier 2.7.0 连接它们会崩溃）")
            End If
            Dim Info As New ProcessStartInfo With {
                .FileName = PathEasyTierCore,
                .Arguments = Arguments,
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8,
                .WorkingDirectory = PathEasyTier
            }
            Logger.Info($"正在启动 EasyTier：{PathEasyTierCore} {Arguments}")
            WriteLog($"[PCL] 启动参数：{Arguments}")
            Dim ProcessNew As New Process With {.StartInfo = Info, .EnableRaisingEvents = True}
            AddHandler ProcessNew.OutputDataReceived, AddressOf OnProcessOutput
            AddHandler ProcessNew.ErrorDataReceived, AddressOf OnProcessOutput
            AddHandler ProcessNew.Exited, AddressOf OnProcessExited
            Try
                ProcessNew.Start()
            Catch ex As Exception
                ProcessNew.Dispose()
                State = EasyTierStates.Failed
                LastError = ex.Message
                Logger.Error(ex, "启动 EasyTier 失败")
                Throw New Exception($"启动 EasyTier 失败：{ex.Message}")
            End Try
            ProcessCore = ProcessNew
            ProcessNew.BeginOutputReadLine()
            ProcessNew.BeginErrorReadLine()
        End SyncLock
        ' 等待 RPC 端口就绪（在锁外进行，避免阻塞状态回调）
        If WaitReady Then
            Dim Ready As Boolean = False
            For i = 1 To 40 '最多等待 10 秒
                Threading.Thread.Sleep(250)
                If Not IsRunning Then Exit For
                If IsRpcReady() Then
                    Ready = True
                    Exit For
                End If
            Next
            If Not IsRunning Then
                Dim Detail As String = String.Join(vbCrLf, GetLogs())
                Dim Reason As String = "EasyTier 启动后立即退出。"
                If Detail.Contains("Failed to create adapter") OrElse Detail.Contains("tun device error") Then
                    Reason = "无法创建虚拟网卡，请确认 PCL 以管理员身份运行。"
                ElseIf Detail.Contains("PANIC") OrElse Detail.Contains("panic occurred") OrElse Detail.Contains("CryptoProvider") Then
                    Reason = "EasyTier 核心发生崩溃（rustls CryptoProvider 缺陷），通常由配置文件中的 https:// 对等节点引起。"
                ElseIf Detail.Contains("some instances stopped with errors") Then
                    Reason = "EasyTier 启动失败，请检查配置文件 easytier.toml 是否有效。"
                ElseIf Detail.Contains("failed to listen on") Then
                    Reason = "监听端口被占用，请更换端口后重试。"
                End If
                State = EasyTierStates.Failed
                LastError = Reason
                CloseLogFile()
                Throw New Exception($"{Reason}{vbCrLf}详细日志：{CurrentLogFile}")
            End If
            If Not Ready Then Logger.Warn("等待 EasyTier RPC 端口就绪超时，状态信息可能暂时不可用")
            State = EasyTierStates.Running
        Else
            ' 不阻塞调用方时（例如启动游戏顺带拉起组网），由后台继续确认就绪状态，
            ' 否则界面会一直停在「正在启动」
            RunInNewThread(
            Sub()
                Dim Ready = False
                For i = 1 To 60 '最多等待 15 秒
                    Threading.Thread.Sleep(250)
                    If State <> EasyTierStates.Starting Then Exit Sub '期间被停止或已就绪
                    If Not IsRunning Then Exit Sub '进程退出由 OnProcessExited 负责收尾
                    If IsRpcReady() Then
                        Ready = True
                        Exit For
                    End If
                Next
                '超时但进程仍在，同样视为运行中，避免状态卡死
                If Not Ready Then Logger.Warn("EasyTier 后台就绪检查超时，仍将状态置为运行中")
                If State = EasyTierStates.Starting Then State = EasyTierStates.Running
            End Sub, "EasyTier Ready Watcher")
        End If
    End Sub

    ''' <summary>
    ''' 停止 EasyTier 核心。不会抛出异常。
    ''' </summary>
    Public Sub StopCore()
        SyncLock ProcessLock
            If State = EasyTierStates.Stopped AndAlso ProcessCore Is Nothing Then Return
            State = EasyTierStates.Stopping
            Dim ProcessRef = ProcessCore
            ProcessCore = Nothing
            If ProcessRef IsNot Nothing Then
                Try
                    If Not ProcessRef.HasExited Then
                        ProcessRef.Kill()
                        ProcessRef.WaitForExit(5000)
                    End If
                Catch ex As Exception
                    Logger.Warn(ex, "结束 EasyTier 进程失败")
                Finally
                    Try
                        ProcessRef.Dispose()
                    Catch
                    End Try
                End Try
            End If
            KillStaleProcesses()
            CurrentListenerPort = 0
            CurrentRpcPort = 0
            '停止后不再保留上一次 RPC 失败信息，避免界面在「未启动」时显示连接错误
            LastRpcError = Nothing
            State = EasyTierStates.Stopped
            WriteLog("[PCL] EasyTier 已停止。")
            CloseLogFile()
        End SyncLock
    End Sub

    ''' <summary>
    ''' 结束后台残留的 EasyTier 进程，避免端口被占用。
    ''' </summary>
    Private Sub KillStaleProcesses()
        Try
            Dim CurrentId = If(ProcessCore Is Nothing, -1, ProcessCore.Id)
            For Each ProcessObject In Process.GetProcesses
                Try
                    If ProcessObject.Id = CurrentId Then Continue For
                    If ProcessObject.ProcessName <> "easytier-core" Then Continue For
                    ' 只处理来自本项目组件目录的进程，避免误伤其他 EasyTier
                    Dim FilePath As String = Nothing
                    Try
                        FilePath = ProcessObject.MainModule?.FileName
                    Catch
                    End Try
                    If FilePath IsNot Nothing AndAlso Not FilePath.StartsWith(PathEasyTier, StringComparison.OrdinalIgnoreCase) Then Continue For
                    Logger.Info($"结束后台残留的 EasyTier 进程，PID：{ProcessObject.Id}")
                    ProcessObject.Kill()
                Catch
                Finally
                    Try
                        ProcessObject.Dispose()
                    Catch
                    End Try
                End Try
            Next
        Catch ex As Exception
            Logger.Warn(ex, "清理 EasyTier 残留进程失败")
        End Try
    End Sub

    Private Sub OnProcessOutput(sender As Object, e As DataReceivedEventArgs)
        If e.Data Is Nothing Then Return
        ' 逐行输出只进 EasyTier 专用日志与界面，不混进 PCL 主日志
        WriteLog(e.Data)
    End Sub

    Private Sub OnProcessExited(sender As Object, e As EventArgs)
        If State = EasyTierStates.Stopping Then Return
        Dim ExitCode As Integer = -1
        Try
            ExitCode = CType(sender, Process).ExitCode
        Catch
        End Try
        Logger.Warn($"EasyTier 进程已退出，退出码 {ExitCode}")
        WriteLog($"[PCL] EasyTier 进程已退出（退出码 {ExitCode}）。")
        If State = EasyTierStates.Running OrElse State = EasyTierStates.Starting Then
            State = EasyTierStates.Stopped
        End If
    End Sub

    ''' <summary>
    ''' RPC 端口是否可以连接。
    ''' </summary>
    Public Function IsRpcReady() As Boolean
        If CurrentRpcPort <= 0 Then Return False
        Try
            Using Client As New Net.Sockets.TcpClient
                Dim ConnectTask = Client.ConnectAsync("127.0.0.1", CurrentRpcPort)
                ' 连接任务完成且无异常即视为端口可用。
                ' 不用 TcpClient.Connected 判定，该属性在异步连接后并不可靠。
                If Not ConnectTask.Wait(400) Then Return False
                Return Not ConnectTask.IsFaulted AndAlso Not ConnectTask.IsCanceled
            End Using
        Catch
            Return False
        End Try
    End Function

#End Region

#Region "CLI 状态"

    ''' <summary>
    ''' 读取节点列表。完全通过管理 RPC 获取，不再依赖 easytier-cli。失败时返回 Nothing。
    ''' </summary>
    Public Function GetPeers() As JArray
        Try
            If Not IsRunning OrElse CurrentRpcPort <= 0 Then Return Nothing
            Using Client As New Global.PCLCS.EasyTier.EasyTierRpcClient("127.0.0.1", CurrentRpcPort, 2500)
                Dim Json As String = Client.ListPeersJson(GetInstanceName())
                If String.IsNullOrWhiteSpace(Json) Then Return Nothing
                Return JArray.Parse(Json)
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "读取 EasyTier 节点列表失败")
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 读取本机节点信息。完全通过管理 RPC 获取，不再依赖 easytier-cli。
    ''' </summary>
    Public Function GetNodeInfo() As JObject
        If Not IsRunning OrElse CurrentRpcPort <= 0 Then Return Nothing
        Return TryGetNodeInfoByRpc()
    End Function

    ''' <summary>
    ''' 通过 EasyTier 私有管理 RPC 读取节点信息；失败时返回 Nothing。
    ''' </summary>
    Private Function TryGetNodeInfoByRpc() As JObject
        Try
            Using Client As New Global.PCLCS.EasyTier.EasyTierRpcClient("127.0.0.1", CurrentRpcPort, 2000)
                Dim Json As String = Client.GetNodeInfoJson(GetInstanceName())
                If String.IsNullOrWhiteSpace(Json) Then
                    LastRpcError = "管理 RPC 返回了空内容。"
                    Return Nothing
                End If
                '读取成功时清除历史错误，避免旧提示残留
                LastRpcError = Nothing
                Return JObject.Parse(Json)
            End Using
        Catch ex As Exception
            '停止或退出过程中的连接中断属于正常现象，不记录为错误，避免界面出现误导性提示
            If State = EasyTierStates.Running OrElse State = EasyTierStates.Starting Then
                LastRpcError = ex.Message
                ' 同时写进 EasyTier 专用日志，方便用户直接在「运行日志」里看到
                WriteLog($"[PCL] 读取节点信息失败：{ex.GetType().Name}: {ex.Message}")
                Logger.Warn(ex, "通过管理 RPC 读取 EasyTier 节点信息失败")
            End If
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 最近一次管理 RPC 失败的原因，供界面显示。
    ''' </summary>
    Public Property LastRpcError As String = Nothing

    ''' <summary>
    ''' 从 easytier.toml 中读取实例名；未配置时返回 Nothing（使用默认实例）。
    ''' </summary>
    Public Function GetInstanceName() As String
        Dim Name As String = GetTomlString("instance_name")
        If String.IsNullOrWhiteSpace(Name) Then Return Nothing
        Return Name.Trim()
    End Function

    ''' <summary>
    ''' 读取核心版本号。失败时返回 "未知"。
    ''' </summary>
    Public Function GetCoreVersion() As String
        Try
            If Not FileUtils.Exists(PathEasyTierCore) Then Return "未知"
            Dim Info As New ProcessStartInfo With {
                .FileName = PathEasyTierCore,
                .Arguments = "--version",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .WorkingDirectory = PathEasyTier
            }
            Using ProcessVersion As Process = Process.Start(Info)
                Dim Output As String = ProcessVersion.StandardOutput.ReadToEnd()
                ProcessVersion.WaitForExit(4000)
                Return Output.Trim().Replace("easytier-core", "EasyTier").Trim()
            End Using
        Catch ex As Exception
            Logger.Warn(ex, "读取 EasyTier 版本失败")
            Return "未知"
        End Try
    End Function

    Private VersionCache As String = Nothing
    ''' <summary>
    ''' 读取并缓存核心版本号，供界面反复显示。
    ''' </summary>
    Public Function GetCoreVersionCached() As String
        If VersionCache IsNot Nothing Then Return VersionCache
        If Not FileUtils.Exists(PathEasyTierCore) Then Return "未安装"
        VersionCache = GetCoreVersion()
        Return VersionCache
    End Function

    ''' <summary>
    ''' 清空版本号缓存。
    ''' </summary>
    Public Sub ClearVersionCache()
        VersionCache = Nothing
    End Sub

    ''' <summary>
    ''' 把 NAT 类型的枚举值转换成可读文字。
    ''' </summary>
    Public Function GetNatTypeName(Value As String) As String
        Select Case Value?.Trim()
            Case "Unknown", "0" : Return "未知"
            Case "OpenInternet", "1" : Return "开放互联网"
            Case "NoPAT", "2" : Return "无端口映射"
            Case "FullCone", "3" : Return "全锥形"
            Case "Restricted", "4" : Return "限制锥形"
            Case "PortRestricted", "5" : Return "端口限制锥形"
            Case "Symmetric", "6" : Return "对称型"
            Case "SymUdpFirewall", "7" : Return "对称型（UDP 防火墙）"
            Case "SymmetricEasyInc", "8" : Return "对称型（易增）"
            Case "SymmetricEasyDec", "9" : Return "对称型（易减）"
            Case Else : Return If(String.IsNullOrWhiteSpace(Value), "未知", Value)
        End Select
    End Function

#End Region

End Module
