Public Class PageEasyTierLog

    ''' <summary>
    ''' 界面上最多保留的行数。EasyTier 在连接大量节点时会刷得很凶，太多行会拖慢布局。
    ''' </summary>
    Private Const MaxDisplayLines As Integer = 180
    ''' <summary>
    ''' 单行显示的最大长度，超长行会被截断，完整内容仍然保留在日志文件里。
    ''' </summary>
    Private Const MaxLineLength As Integer = 400

    Private IsLoadedOnce As Boolean = False
    Private LogHandler As Action(Of String)
    Private ReadOnly DisplayBuffer As New List(Of String)
    Private HasPendingUpdate As Boolean = False
    Private RefreshTimer As Windows.Threading.DispatcherTimer

    Public Sub New()
        InitializeComponent()
        LogHandler = AddressOf OnEasyTierLog
        AddHandler Me.Loaded, AddressOf OnPageLoaded
        AddHandler Me.Unloaded, AddressOf OnPageUnloaded
    End Sub

    Private Sub OnPageLoaded(sender As Object, e As RoutedEventArgs)
        If IsLoadedOnce Then Return
        IsLoadedOnce = True
        ModEasyTier.AddLogHandler(LogHandler)
        ' 只载入最后若干行，避免进页面时一次性渲染上百段内容
        DisplayBuffer.Clear()
        Dim AllLogs = ModEasyTier.GetLogs()
        Dim StartIndex As Integer = Math.Max(0, AllLogs.Count - MaxDisplayLines)
        For i = StartIndex To AllLogs.Count - 1
            DisplayBuffer.Add(TrimLine(AllLogs(i)))
        Next
        FlushView(True)
        ' 用低频定时器合并刷新，避免每条日志都触发一次文本布局
        RefreshTimer = New Windows.Threading.DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(450)}
        AddHandler RefreshTimer.Tick, AddressOf OnRefreshTick
        RefreshTimer.Start()
    End Sub

    Private Sub OnPageUnloaded(sender As Object, e As RoutedEventArgs)
        IsLoadedOnce = False
        ModEasyTier.RemoveLogHandler(LogHandler)
        If RefreshTimer IsNot Nothing Then
            RefreshTimer.Stop()
            RefreshTimer = Nothing
        End If
    End Sub

    Private Sub OnRefreshTick(sender As Object, e As EventArgs)
        If HasPendingUpdate Then FlushView(False)
    End Sub

    ''' <summary>
    ''' 新日志到达时只进缓冲，真正的界面刷新交给定时器合并处理。
    ''' </summary>
    Private Sub OnEasyTierLog(Line As String)
        RunInUi(
        Sub()
            If Not IsLoadedOnce Then Return
            DisplayBuffer.Add(TrimLine(Line))
            Do While DisplayBuffer.Count > MaxDisplayLines
                DisplayBuffer.RemoveAt(0)
            Loop
            HasPendingUpdate = True
        End Sub)
    End Sub

    ''' <summary>
    ''' 把缓冲一次性写进界面。
    ''' </summary>
    Private Sub FlushView(ForceScrollToEnd As Boolean)
        HasPendingUpdate = False
        ' 记录刷新前是否贴在底部，避免打断正在向上翻看的用户
        Dim WasAtBottom As Boolean = PanLog.ScrollableHeight <= 0 OrElse
                                     PanLog.VerticalOffset >= PanLog.ScrollableHeight - 24
        LabLog.Text = String.Join(vbCrLf, DisplayBuffer)
        LabLogCount.Text = If(DisplayBuffer.Count >= MaxDisplayLines,
                              $"显示最近 {DisplayBuffer.Count} 行（完整内容见日志文件）",
                              $"显示最近 {DisplayBuffer.Count} 行")
        If ForceScrollToEnd OrElse WasAtBottom Then PanLog.ScrollToEnd()
    End Sub

    ''' <summary>
    ''' 截断过长的行，避免单行把布局撑爆。
    ''' </summary>
    Private Function TrimLine(Line As String) As String
        Dim Value = If(Line, "")
        If Value.Length <= MaxLineLength Then Return Value
        Return Value.Substring(0, MaxLineLength) & " …（已截断）"
    End Function

    Private Sub BtnClear_Click() Handles BtnClear.Click
        ModEasyTier.ClearLogs()
        DisplayBuffer.Clear()
        HasPendingUpdate = False
        FlushView(True)
    End Sub

    Private Sub BtnCopy_Click() Handles BtnCopy.Click
        Dim AllLogs = ModEasyTier.GetLogs()
        If AllLogs.Count = 0 Then
            Hint("当前没有日志可以复制。", HintType.Blue)
            Return
        End If
        Try
            Clipboard.SetText(String.Join(vbCrLf, AllLogs))
            Hint($"已复制 {AllLogs.Count} 行日志。", HintType.Green)
        Catch ex As Exception
            Logger.Warn(ex, "复制 EasyTier 日志失败")
            Hint("复制失败，剪贴板可能被其他程序占用。", HintType.Red)
        End Try
    End Sub

    Private Sub BtnOpenDir_Click() Handles BtnOpenDir.Click
        Try
            Dim Target As String = ModEasyTier.PathEasyTierLogDir
            DirectoryUtils.Create(Target)
            Process.Start("explorer.exe", """" & Target & """")
        Catch ex As Exception
            Logger.Warn(ex, "打开 EasyTier 日志目录失败")
            Hint("打开目录失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

End Class
