Imports System.Windows.Media
Imports Newtonsoft.Json.Linq

Public Class PageEasyTierOverview

    Private Shared ReadOnly BrushRunning As Brush = New SolidColorBrush(Color.FromRgb(46, 204, 113))
    Private Shared ReadOnly BrushStarting As Brush = New SolidColorBrush(Color.FromRgb(245, 166, 35))
    Private Shared ReadOnly BrushFailed As Brush = New SolidColorBrush(Color.FromRgb(231, 76, 60))
    Private Shared ReadOnly BrushIdle As Brush = New SolidColorBrush(Color.FromRgb(166, 166, 166))
    Private Shared ReadOnly BrushGray As Brush = New SolidColorBrush(Color.FromRgb(115, 115, 115))
    Private Shared ReadOnly BrushBrand As Brush = New SolidColorBrush(Color.FromRgb(19, 112, 243))
    Private Shared ReadOnly BrushGreen As Brush = New SolidColorBrush(Color.FromRgb(70, 176, 90))
    Private Shared ReadOnly BrushOrange As Brush = New SolidColorBrush(Color.FromRgb(245, 166, 35))

    Private RefreshTimer As Windows.Threading.DispatcherTimer
    Private IsRefreshing As Boolean = False
    Private IsLoadedOnce As Boolean = False
    Private StateHandler As Action(Of ModEasyTier.EasyTierStates)
    Private LogHandler As Action(Of String)
    ''' <summary>上次显示的虚拟 IP，用于判断 DHCP 是否还在更换地址。</summary>
    Private LastShownIp As String = Nothing
    ''' <summary>虚拟 IP 最后一次变化的时间。</summary>
    Private LastIpChangedAt As DateTime = DateTime.MinValue
    ''' <summary>节点列表的刷新节拍，用于降低 easytier-cli 的调用频率。</summary>
    Private PeerTickCounter As Integer = 0

    Public Sub New()
        InitializeComponent()
        StateHandler = AddressOf OnEasyTierStateChanged
        LogHandler = AddressOf OnEasyTierLog
        AddHandler Me.Loaded, AddressOf OnPageLoaded
        AddHandler Me.Unloaded, AddressOf OnPageUnloaded
    End Sub

    Private Sub OnPageLoaded(sender As Object, e As RoutedEventArgs)
        If IsLoadedOnce Then Return
        IsLoadedOnce = True
        ModEasyTier.AddStateHandler(StateHandler)
        ModEasyTier.AddLogHandler(LogHandler)
        RefreshAll()
        RefreshTimer = New Windows.Threading.DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(1200)}
        AddHandler RefreshTimer.Tick, Sub() RefreshAsync()
        RefreshTimer.Start()
    End Sub

    Private Sub OnPageUnloaded(sender As Object, e As RoutedEventArgs)
        IsLoadedOnce = False
        ModEasyTier.RemoveStateHandler(StateHandler)
        ModEasyTier.RemoveLogHandler(LogHandler)
        If RefreshTimer IsNot Nothing Then
            RefreshTimer.Stop()
            RefreshTimer = Nothing
        End If
    End Sub

    ''' <summary>
    ''' 状态改变时刷新界面。可能在后台线程触发。
    ''' </summary>
    Private Sub OnEasyTierStateChanged(NewState As ModEasyTier.EasyTierStates)
        RunInUi(
        Sub()
            If Not IsLoadedOnce Then Return
            RefreshStatusVisual()
        End Sub)
    End Sub

    Private Sub OnEasyTierLog(Line As String)
        ' 概览页不展示完整日志，由「运行日志」页负责
    End Sub

    ''' <summary>
    ''' 立刻刷新一次全部信息。
    ''' </summary>
    Private Sub RefreshAll()
        RefreshStatusVisual()
        RefreshAsync()
    End Sub

    ''' <summary>
    ''' 后台读取状态，回到 UI 线程渲染。
    ''' </summary>
    Private Sub RefreshAsync()
        If IsRefreshing Then Return
        IsRefreshing = True
        RunInNewThread(
        Sub()
            Try
                Dim NodeInfo As JObject = Nothing
                Dim Peers As JArray = Nothing
                If ModEasyTier.IsRunning Then
                    ' 本机信息走管理 RPC，单次约 10ms，可以高频刷新
                    NodeInfo = ModEasyTier.GetNodeInfo()
                    ' 节点列表仍需调用 easytier-cli，开销明显更大，降低到约 1/3 的频率
                    PeerTickCounter += 1
                    If PeerTickCounter >= 3 Then
                        PeerTickCounter = 0
                        Peers = ModEasyTier.GetPeers()
                    End If
                End If
                RunInUi(
                Sub()
                    If Not IsLoadedOnce Then Return
                    RefreshStatusVisual()
                    RenderInfo(NodeInfo, Peers)
                    If Peers IsNot Nothing Then RenderPeers(Peers)
                End Sub)
            Catch ex As Exception
                Logger.Warn(ex, "刷新组网状态失败")
            Finally
                IsRefreshing = False
            End Try
        End Sub, "EasyTier Overview Refresh")
    End Sub

    ''' <summary>
    ''' 刷新状态文字、圆点与按钮。
    ''' </summary>
    Private Sub RefreshStatusVisual()
        Dim CurrentState = ModEasyTier.State
        Select Case CurrentState
            Case ModEasyTier.EasyTierStates.Running
                ShapeStatus.Fill = BrushRunning
                LabStatus.Text = "组网运行中"
                LabStatusDetail.Text = If(ModEasyTier.NeedsTun,
                    "虚拟网卡已就绪，同一网络内的设备可以互相访问了。",
                    "当前为仅转发模式，未创建虚拟网卡。")
                BtnToggle.Text = "停止组网"
                BtnToggle.ColorType = MyButton.ColorState.Red
            Case ModEasyTier.EasyTierStates.Starting
                ShapeStatus.Fill = BrushStarting
                LabStatus.Text = "正在启动…"
                LabStatusDetail.Text = "正在拉起 EasyTier 核心并等待管理端口就绪。"
                BtnToggle.Text = "启动中…"
                BtnToggle.ColorType = MyButton.ColorState.Normal
            Case ModEasyTier.EasyTierStates.Stopping
                ShapeStatus.Fill = BrushStarting
                LabStatus.Text = "正在停止…"
                LabStatusDetail.Text = "正在结束后台进程。"
                BtnToggle.Text = "停止中…"
                BtnToggle.ColorType = MyButton.ColorState.Normal
            Case ModEasyTier.EasyTierStates.Failed
                ShapeStatus.Fill = BrushFailed
                LabStatus.Text = "启动失败"
                LabStatusDetail.Text = If(String.IsNullOrWhiteSpace(ModEasyTier.LastError), "请查看运行日志了解详情。", ModEasyTier.LastError)
                BtnToggle.Text = "重新启动"
                BtnToggle.ColorType = MyButton.ColorState.Highlight
            Case Else
                ShapeStatus.Fill = BrushIdle
                LabStatus.Text = "未启动"
                Dim Problem As String = ModEasyTier.BinariesProblem
                If Problem IsNot Nothing Then
                    LabStatusDetail.Text = Problem
                ElseIf Not ModEasyTier.HasTomlConfig Then
                    LabStatusDetail.Text = "未找到 easytier.toml。请到「配置文件」页查看应该把文件放在哪里。"
                ElseIf ModEasyTier.NeedsTun AndAlso Not ModEasyTier.IsAdministrator Then
                    LabStatusDetail.Text = "创建虚拟网卡需要管理员权限。请以管理员身份重新启动 PCL，或在 easytier.toml 中设置 no_tun = true。"
                Else
                    LabStatusDetail.Text = $"使用 {IO.Path.GetFileName(ModEasyTier.PathEasyTierToml)} 启动 EasyTier，让同一网络内的设备互相访问。"
                End If
                BtnToggle.Text = "启动组网"
                BtnToggle.ColorType = MyButton.ColorState.Highlight
        End Select
        BtnToggle.IsEnabled = CurrentState <> ModEasyTier.EasyTierStates.Starting AndAlso
                              CurrentState <> ModEasyTier.EasyTierStates.Stopping
        If ModEasyTier.HasTomlConfig Then
            Dim NetName = ModEasyTier.GetTomlString("network_name")
            LabNetworkName.Text = If(String.IsNullOrWhiteSpace(NetName), "（未设置）", NetName)
        Else
            LabNetworkName.Text = "（无配置文件）"
        End If
        LabListenPort.Text = If(ModEasyTier.CurrentListenerPort > 0, ModEasyTier.CurrentListenerPort.ToString(), "—")
        LabRpcPort.Text = If(ModEasyTier.CurrentRpcPort > 0, ModEasyTier.CurrentRpcPort.ToString(), "—")
        LabVersion.Text = ModEasyTier.GetCoreVersionCached()
    End Sub

    ''' <summary>
    ''' 渲染本机信息。
    ''' </summary>
    Private Sub RenderInfo(NodeInfo As JObject, Peers As JArray)
        If NodeInfo Is Nothing Then
            LabIp.Text = "—"
            LabNatType.Text = "—"
            ' 把 RPC 失败原因直接显示出来，避免界面只有「—」却不知道哪里出错
            If Not String.IsNullOrWhiteSpace(ModEasyTier.LastRpcError) Then
                LabStatusDetail.Text = "读取组网信息失败：" & ModEasyTier.LastRpcError
            End If
            Return
        End If
        Dim Address As String = Nothing
        Try
            Address = NodeInfo.SelectToken("ipv4_addr")?.ToString()
        Catch
        End Try
        If Not String.IsNullOrWhiteSpace(Address) Then
            Address = Address.Split("/"c)(0).Trim()
        End If
        ' 官方客户端靠订阅 DhcpIpv4Changed / DhcpIpv4Conflicted 事件，地址没定下来就不显示。
        ' 这里用核心日志里的同名事件做判断，再叠加一层「地址稳定」兜底。
        If Address <> LastShownIp Then
            LastShownIp = Address
            LastIpChangedAt = Now
        End If
        Dim IsSettling As Boolean = ModEasyTier.IsDhcpSettling OrElse (Now - LastIpChangedAt).TotalSeconds < 2
        If IsSettling Then
            LabIp.Text = "分配中…"
        Else
            LabIp.Text = If(String.IsNullOrWhiteSpace(Address), "—", Address)
        End If
        Dim StunType As String = Nothing
        Try
            StunType = NodeInfo.SelectToken("stun_info")?.SelectToken("udp_nat_type")?.ToString()
        Catch
        End Try
        LabNatType.Text = ModEasyTier.GetNatTypeName(StunType)
    End Sub

    ''' <summary>
    ''' 渲染节点列表。
    ''' </summary>
    Private Sub RenderPeers(Peers As JArray)
        PanPeers.Children.Clear()
        If Peers Is Nothing Then
            LabPeersEmpty.Visibility = Visibility.Visible
            LabPeersEmpty.Text = If(ModEasyTier.IsRunning, "正在获取节点信息…", "暂未连接到任何节点。")
            LabPeerCount.Text = If(ModEasyTier.IsRunning, "—", "0")
            Return
        End If
        Dim Count As Integer = 0
        For Each Item As JToken In Peers
            Try
                Dim Row As Grid = BuildPeerRow(Item)
                If Row IsNot Nothing Then
                    PanPeers.Children.Add(Row)
                    Count += 1
                End If
            Catch ex As Exception
                Logger.Warn(ex, "渲染 EasyTier 节点失败")
            End Try
        Next
        LabPeerCount.Text = Count.ToString()
        LabPeersEmpty.Visibility = If(Count = 0, Visibility.Visible, Visibility.Collapsed)
        If Count = 0 Then LabPeersEmpty.Text = "暂未连接到任何节点。"
    End Sub

    ''' <summary>
    ''' 构建一行节点信息。
    ''' </summary>
    Private Function BuildPeerRow(Item As JToken) As Grid
        Dim Hostname As String = Nothing
        Dim PeerIp As String = Nothing
        Dim Cost As String = Nothing
        Dim Latency As String = Nothing
        Try
            Hostname = Item.SelectToken("hostname")?.ToString()
            PeerIp = Item.SelectToken("ipv4")?.ToString()
            Cost = Item.SelectToken("cost")?.ToString()
            Latency = Item.SelectToken("lat_ms")?.ToString()
        Catch
        End Try
        If String.IsNullOrWhiteSpace(Hostname) AndAlso String.IsNullOrWhiteSpace(PeerIp) Then Return Nothing

        Dim IsSelf As Boolean = Cost IsNot Nothing AndAlso Cost.Equals("Local", StringComparison.OrdinalIgnoreCase)
        Dim Row As New Grid With {.Height = 30}
        Dim Widths() As Double = {1.5, 1.1, 0.7, 0.9}
        For i = 0 To 3
            Row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(Widths(i), GridUnitType.Star)})
        Next
        ' 名称
        Dim LabName As New TextBlock With {
            .Text = If(String.IsNullOrWhiteSpace(Hostname), "(未命名)", Hostname),
            .VerticalAlignment = VerticalAlignment.Center,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        If IsSelf Then
            LabName.Text &= "（本机）"
            LabName.Foreground = BrushBrand
        End If
        Row.Children.Add(LabName)
        ' 虚拟 IP
        Dim LabPeerIpView As New TextBlock With {
            .Text = If(String.IsNullOrWhiteSpace(PeerIp), "—", PeerIp),
            .VerticalAlignment = VerticalAlignment.Center,
            .FontSize = 13,
            .Foreground = BrushGray,
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        Grid.SetColumn(LabPeerIpView, 1)
        Row.Children.Add(LabPeerIpView)
        ' 延迟
        Dim LabLatencyView As New TextBlock With {
            .Text = If(String.IsNullOrWhiteSpace(Latency) OrElse Latency = "-", "—", Latency & " ms"),
            .VerticalAlignment = VerticalAlignment.Center,
            .FontSize = 13,
            .Foreground = BrushGray
        }
        Grid.SetColumn(LabLatencyView, 2)
        Row.Children.Add(LabLatencyView)
        ' 连接方式
        Dim Relay As Boolean = Cost IsNot Nothing AndAlso Cost.IndexOf("relay", StringComparison.OrdinalIgnoreCase) >= 0
        Dim LabCostView As New TextBlock With {
            .Text = If(IsSelf, "本机", If(Relay, "中继", "直连")),
            .VerticalAlignment = VerticalAlignment.Center,
            .FontSize = 13,
            .Foreground = If(Relay, BrushOrange, BrushGreen)
        }
        Grid.SetColumn(LabCostView, 3)
        Row.Children.Add(LabCostView)
        Return Row
    End Function

    ''' <summary>
    ''' 启动或停止组网。
    ''' </summary>
    Private Sub BtnToggle_Click() Handles BtnToggle.Click
        Dim CurrentState = ModEasyTier.State
        If CurrentState = ModEasyTier.EasyTierStates.Running OrElse CurrentState = ModEasyTier.EasyTierStates.Starting Then
            ModEasyTier.StopCore()
            RefreshAll()
            Return
        End If
        ' 组件检查
        Dim Problem As String = ModEasyTier.BinariesProblem
        If Problem IsNot Nothing Then
            Hint(Problem, HintType.Red)
            Return
        End If
        ' 配置文件检查
        If Not ModEasyTier.HasTomlConfig Then
            Hint("未找到 easytier.toml，请先到「配置文件」页查看应该把文件放在哪里。", HintType.Red)
            Return
        End If
        ' 权限检查
        If ModEasyTier.NeedsTun AndAlso Not ModEasyTier.IsAdministrator Then
            MyMsgBox("创建虚拟网卡需要管理员权限，当前 PCL 没有以管理员身份运行。" & vbCrLf &
                     "请关闭 PCL 后右键「以管理员身份运行」，或在 easytier.toml 的 [flags] 中加上 no_tun = true 以仅转发模式启动。",
                     "需要管理员权限", "知道了", IsWarn:=True)
            Return
        End If
        BtnToggle.IsEnabled = False
        RunInNewThread(
        Sub()
            Try
                ModEasyTier.StartCore()
                RunInUi(Sub() Hint("EasyTier 组网已启动。", HintType.Green))
            Catch ex As Exception
                Logger.Warn(ex, "启动 EasyTier 失败")
                RunInUi(Sub()
                            Hint("启动组网失败：" & ex.Message, HintType.Red)
                            RefreshStatusVisual()
                        End Sub)
            Finally
                RunInUi(Sub() RefreshAll())
            End Try
        End Sub, "EasyTier Start")
    End Sub

End Class
