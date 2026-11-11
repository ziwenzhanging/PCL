Public Class PageEasyTierLeft

    ''' <summary>
    ''' 当前页面的编号，从 0 开始计算。
    ''' </summary>
    Public PageID As FormMain.PageSubType

    Public Sub New()
        InitializeComponent()
        PageID = FormMain.PageSubType.EasyTierOverview
    End Sub

    ''' <summary>
    ''' 勾选事件改变页面。
    ''' </summary>
    Private Sub PageCheck(sender As MyListItem, e As EventArgs) Handles ItemOverview.Check, ItemLog.Check
        ' 尚未初始化控件属性时，sender.Tag 为 Nothing，此时跳过切换
        If sender.Tag IsNot Nothing Then PageChange(Val(sender.Tag))
    End Sub

    ''' <summary>
    ''' 获取当前导航指定的右页面。
    ''' </summary>
    Public Function PageGet(Optional ID As FormMain.PageSubType = -1)
        If ID = -1 Then ID = PageID
        Select Case ID
            Case FormMain.PageSubType.EasyTierOverview
                If FrmEasyTierOverview Is Nothing Then FrmEasyTierOverview = New PageEasyTierOverview
                Return FrmEasyTierOverview
            Case FormMain.PageSubType.EasyTierLog
                If FrmEasyTierLog Is Nothing Then FrmEasyTierLog = New PageEasyTierLog
                Return FrmEasyTierLog
            Case Else
                Throw New Exception("未知的组网子页面种类：" & ID)
        End Select
    End Function

    ''' <summary>
    ''' 切换现有页面。
    ''' </summary>
    Public Sub PageChange(ID As FormMain.PageSubType)
        If PageID = ID Then Return
        AniControlEnabled += 1
        Try
            Select Case ID
                Case FormMain.PageSubType.EasyTierOverview
                    If FrmEasyTierOverview Is Nothing Then FrmEasyTierOverview = New PageEasyTierOverview
                    PageChangeRun(FrmEasyTierOverview)
                Case FormMain.PageSubType.EasyTierLog
                    If FrmEasyTierLog Is Nothing Then FrmEasyTierLog = New PageEasyTierLog
                    PageChangeRun(FrmEasyTierLog)
                Case Else
                    Throw New Exception("未知的组网子页面种类：" & ID)
            End Select
            PageID = ID
        Catch ex As Exception
            Logger.Error(ex, $"切换组网分页面失败（ID {ID}）")
        Finally
            AniControlEnabled -= 1
        End Try
    End Sub

    ''' <summary>
    ''' 实际执行右侧页面切换动画。
    ''' </summary>
    Private Shared Sub PageChangeRun(Target As MyPageRight)
        AniStop("FrmMain PageChangeRight") '停止主页面的右页面切换动画，防止它与本动画一起触发多次 PageOnEnter
        If Target.Parent IsNot Nothing Then Target.SetValue(ContentPresenter.ContentProperty, Nothing)
        FrmMain.PageRight = Target
        If FrmMain.PanMainRight.Child IsNot Nothing Then CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnExit()
        AniStart({
            AaCode(
            Sub()
                If FrmMain.PanMainRight.Child IsNot Nothing Then CType(FrmMain.PanMainRight.Child, MyPageRight).PageOnForceExit()
                FrmMain.PanMainRight.Child = FrmMain.PageRight
                FrmMain.PageRight.Opacity = 0
            End Sub, 130),
            AaCode(
            Sub()
                '延迟触发页面通用动画，以使得在 Loaded 事件中加载的控件得以处理
                FrmMain.PageRight.Opacity = 1
                FrmMain.PageRight.PageOnEnter()
            End Sub, 30, True)
        }, "PageLeft PageChange")
    End Sub

End Class
