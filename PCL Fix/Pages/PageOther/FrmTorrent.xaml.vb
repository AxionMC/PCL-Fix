Imports System.Windows.Threading

''' <summary>
''' PCL Fix：BT 种子 / 磁力下载窗口。
''' </summary>
Public Class FrmTorrent

    Private ReadOnly RefreshTimer As New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(700)}
    Private IsRefreshing As Boolean = False
    Private CurrentFolder As String = Nothing

    Public Sub New()
        InitializeComponent()
        CurrentFolder = ModTorrent.DefaultFolder()
        LabFolder.Text = "保存到：" & CurrentFolder
        AddHandler RefreshTimer.Tick, AddressOf RefreshTick
        RefreshTimer.Start()
        RefreshTasks()
    End Sub

    Private Sub PanHead_MouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs)
        On Error Resume Next
        DragMove()
    End Sub

    Private Sub BtnClose_Click(sender As Object, e As RoutedEventArgs) Handles BtnClose.Click
        Close()
    End Sub

    Private Sub BtnFolder_Click(sender As Object, e As RoutedEventArgs) Handles BtnFolder.Click
        Try
            Dim Folder As String = Dialogs.SelectFolder("选择 BT 下载保存位置", False).FirstOrDefault
            If String.IsNullOrEmpty(Folder) Then Return
            CurrentFolder = Folder
            If Not CurrentFolder.EndsWith("\") Then CurrentFolder &= "\"
            LabFolder.Text = "保存到：" & CurrentFolder
            Settings.Set("ToolTorrentFolder", CurrentFolder)
            Hint("BT 保存位置已更新", HintType.Green, False)
        Catch ex As Exception
            Logger.Warn(ex, "选择 BT 保存位置失败")
            Hint("选择保存位置失败", HintType.Red)
        End Try
    End Sub

    Private Sub BtnPickTorrent_Click(sender As Object, e As RoutedEventArgs) Handles BtnPickTorrent.Click
        Try
            Dim File As String = Dialogs.SelectFile("选择 BT 种子文件", False, filter:={({"torrent"}, "BT 种子文件")}).FirstOrDefault
            If String.IsNullOrEmpty(File) Then Return
            StartTorrent(File)
        Catch ex As Exception
            Logger.Warn(ex, "选择 BT 种子文件失败")
            Hint("选择种子文件失败", HintType.Red)
        End Try
    End Sub

    Private Sub BtnStart_Click(sender As Object, e As RoutedEventArgs) Handles BtnStart.Click
        Dim Magnet As String = If(TextMagnet.Text, "").Trim()
        If Magnet = "" Then
            Hint("请先粘贴磁力链接，或选择 .torrent 文件", HintType.Red)
            Return
        End If
        Try
            ModTorrent.StartMagnet(Magnet, CurrentFolder)
            TextMagnet.Text = ""
            Hint("BT 任务已开始", HintType.Green, False)
            RefreshTasks()
        Catch ex As Exception
            Logger.Warn(ex, "开始磁力下载失败")
            Hint("开始下载失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub StartTorrent(FilePath As String)
        Try
            ModTorrent.StartTorrentFile(FilePath, CurrentFolder)
            Hint("BT 任务已开始：" & PathUtils.GetLastPart(FilePath), HintType.Green, False)
            RefreshTasks()
        Catch ex As Exception
            Logger.Warn(ex, "开始种子下载失败")
            Hint("开始下载失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    '拖放种子文件
    Private Sub FrmTorrent_Drop(sender As Object, e As DragEventArgs) Handles Me.Drop
        Try
            If e.Data Is Nothing OrElse Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
            Dim Files As String() = CType(e.Data.GetData(DataFormats.FileDrop), String())
            If Files Is Nothing Then Return
            For Each File In Files
                If PathUtils.GetExtension(File).ToLower = "torrent" Then StartTorrent(File)
            Next
        Catch ex As Exception
            Logger.Warn(ex, "拖放种子文件失败")
        End Try
    End Sub

    Private Sub RefreshTick(sender As Object, e As EventArgs)
        If IsRefreshing Then Return
        IsRefreshing = True
        RunInNewThread(
        Sub()
            Try
                ModTorrent.RefreshAll()
            Catch ex As Exception
                Logger.Warn(ex, "刷新 BT 任务失败")
            Finally
                IsRefreshing = False
                RunInUi(Sub() RefreshTasks())
            End Try
        End Sub, "BT 刷新", ThreadPriority.BelowNormal)
    End Sub

    ''' <summary>
    ''' 重建任务卡片。
    ''' </summary>
    Private Sub RefreshTasks()
        Try
            Dim TaskList As List(Of ModTorrent.BtTask) = ModTorrent.BtTasks.Values.OrderByDescending(Function(t) t.StartTime).ToList
            LabEmpty.Visibility = If(TaskList.Count = 0, Visibility.Visible, Visibility.Collapsed)
            PanTasks.Children.Clear()
            For Each TaskInfo In TaskList
                PanTasks.Children.Add(BuildCard(TaskInfo))
            Next
        Catch ex As Exception
            Logger.Warn(ex, "刷新 BT 任务列表失败")
        End Try
    End Sub

    Private Function MakeText(Content As String, Size As Double, Optional Bold As Boolean = False, Optional Opacity As Double = 1) As TextBlock
        Dim Text As New TextBlock With {
            .Text = Content, .FontSize = Size, .Opacity = Opacity,
            .FontWeight = If(Bold, FontWeights.Bold, FontWeights.Normal),
            .TextTrimming = TextTrimming.CharacterEllipsis
        }
        Text.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        Return Text
    End Function

    Private Function BuildCard(TaskInfo As ModTorrent.BtTask) As UIElement
        Dim Card As New Border With {
            .CornerRadius = New CornerRadius(14),
            .Background = New SolidColorBrush(Color.FromRgb(246, 248, 252)),
            .Padding = New Thickness(16, 12, 16, 12),
            .Margin = New Thickness(0, 0, 0, 9),
            .Tag = TaskInfo
        }
        Dim Root As New Grid
        Root.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
        Root.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(10)})
        Root.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(6)})
        Root.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(9)})
        Root.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})

        '标题行
        Dim TitleRow As New Grid
        Dim NameText As TextBlock = MakeText(TaskInfo.Name, 14, True)
        NameText.Margin = New Thickness(0, 0, 90, 0)
        Dim StateText As TextBlock = MakeText(TaskInfo.StateText, 12, False, 0.55)
        StateText.HorizontalAlignment = HorizontalAlignment.Right
        StateText.VerticalAlignment = VerticalAlignment.Center
        TitleRow.Children.Add(NameText)
        TitleRow.Children.Add(StateText)
        Grid.SetRow(TitleRow, 0)
        Root.Children.Add(TitleRow)

        '进度条
        Dim Progress As Double = Math.Max(0.002, Math.Min(1, TaskInfo.Progress))
        Dim Bar As New Grid With {.Height = 6}
        Bar.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(Progress, GridUnitType.Star)})
        Bar.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(Math.Max(0.002, 1 - Progress), GridUnitType.Star)})
        Dim Fill As New Rectangle With {.RadiusX = 3, .RadiusY = 3, .Fill = New SolidColorBrush(Color.FromRgb(47, 111, 237))}
        Dim Track As New Rectangle With {.RadiusX = 3, .RadiusY = 3, .Fill = New SolidColorBrush(Color.FromRgb(229, 235, 245))}
        Grid.SetColumn(Fill, 0)
        Grid.SetColumn(Track, 1)
        Bar.Children.Add(Fill)
        Bar.Children.Add(Track)
        Grid.SetRow(Bar, 2)
        Root.Children.Add(Bar)

        '信息行
        Dim PercentText As String = If(TaskInfo.Total > 0, $"{TaskInfo.Progress * 100:0.0}%", "--")
        Dim InfoRow As New Grid
        Dim InfoText As TextBlock = MakeText($"{PercentText} · {TaskInfo.SizeText} · {StringUtils.FormatByteSize(TaskInfo.Speed)}/s · {TaskInfo.Peers} 连接", 12, False, 0.6)
        InfoText.VerticalAlignment = VerticalAlignment.Center
        InfoRow.Children.Add(InfoText)

        Dim Buttons As New StackPanel With {.Orientation = Orientation.Horizontal, .HorizontalAlignment = HorizontalAlignment.Right}
        Dim BtnPause As New MyButton With {.Text = If(TaskInfo.State = "paused", "继续", "暂停"), .Height = 26, .Padding = New Thickness(14, 0, 14, 0), .TextPadding = New Thickness(9, 0, 9, 0)}
        AddHandler BtnPause.Click,
            Sub(s, e)
                If TaskInfo.State = "paused" Then
                    ModTorrent.Resume(TaskInfo)
                Else
                    ModTorrent.Pause(TaskInfo)
                End If
                RefreshTasks()
            End Sub
        Dim BtnOpen As New MyButton With {.Text = "打开文件夹", .Height = 26, .Padding = New Thickness(14, 0, 14, 0), .TextPadding = New Thickness(9, 0, 9, 0), .Margin = New Thickness(9, 0, 0, 0)}
        AddHandler BtnOpen.Click,
            Sub(s, e)
                Try
                    OpenExplorer(TaskInfo.Dir)
                Catch ex As Exception
                    Logger.Warn(ex, "打开 BT 下载文件夹失败")
                End Try
            End Sub
        Dim BtnRemove As New MyButton With {.Text = "删除", .Height = 26, .Padding = New Thickness(14, 0, 14, 0), .TextPadding = New Thickness(9, 0, 9, 0), .Margin = New Thickness(9, 0, 0, 0), .ColorType = MyButton.ColorState.Red}
        AddHandler BtnRemove.Click,
            Sub(s, e)
                ModTorrent.Remove(TaskInfo)
                RefreshTasks()
            End Sub
        Buttons.Children.Add(BtnPause)
        Buttons.Children.Add(BtnOpen)
        Buttons.Children.Add(BtnRemove)
        InfoRow.Children.Add(Buttons)
        Grid.SetRow(InfoRow, 4)
        Root.Children.Add(InfoRow)

        Card.Child = Root
        Return Card
    End Function

End Class