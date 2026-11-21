''' <summary>
''' PCL Fix：在线二次元壁纸。
''' 从多个公开壁纸 API 获取随机动漫壁纸（支持 webp/jpeg/webp），缓存到 PCL\Pictures 后作为窗口背景。
''' </summary>
Public Module ModBackground

    ''' <summary>
    ''' 二次元壁纸 API 列表，依次尝试。
    ''' </summary>
    Private ReadOnly WallpaperApis As String() = {
        "https://t.alcy.cc/pc",
        "https://www.loliapi.com/acg/",
        "https://api.anosu.top/img?type=mp"
    }

    Private ReadOnly WallpaperLock As New Object

    ''' <summary>
    ''' 壁纸缓存文件夹。
    ''' </summary>
    Public ReadOnly Property WallpaperFolder As String
        Get
            Return Paths.Base & "PCL\Pictures\"
        End Get
    End Property

    ''' <summary>
    ''' 在线壁纸的缓存文件名。
    ''' </summary>
    Public ReadOnly Property WallpaperCachePath As String
        Get
            Return WallpaperFolder & "OnlineWallpaper.img"
        End Get
    End Property

    ''' <summary>
    ''' 从网络获取一张二次元壁纸并应用为窗口背景。
    ''' 必须在工作线程执行。
    ''' </summary>
    ''' <param name="Force">即使设置中关闭了在线壁纸也强制获取。</param>
    ''' <param name="ShowHint">是否显示结果提示。</param>
    Public Function RefreshOnlineWallpaper(Optional Force As Boolean = False, Optional ShowHint As Boolean = False) As Boolean
        SyncLock WallpaperLock
            Try
                If Not Force AndAlso Not Settings.Get(Of Boolean)("UiBackgroundOnline") Then Return False
                DirectoryUtils.Create(WallpaperFolder)
                Dim Target As String = WallpaperCachePath
                Dim ApiIndex As Integer = Settings.Get(Of Integer)("UiBackgroundApi")
                If ApiIndex < 0 OrElse ApiIndex >= WallpaperApis.Length Then ApiIndex = 0
                Dim Success As Boolean = False
                For i = 0 To WallpaperApis.Length - 1
                    Dim CurrentIndex As Integer = (ApiIndex + i) Mod WallpaperApis.Length
                    Dim Url As String = WallpaperApis(CurrentIndex)
                    Try
                        Logger.Info($"开始获取在线壁纸：{Url}")
                        FileUtils.Delete(Target)
                        NetDownloadByClient(Url, Target)
                        Dim Length As Long = If(FileUtils.Exists(Target), FileUtils.GetInfo(Target).Length, 0)
                        If Length < 10 * 1024 Then Throw New Exception($"获取到的壁纸文件过小（{Length} B）")
                        Logger.Info($"在线壁纸获取成功（{StringUtils.FormatByteSize(Length)}）：{Url}")
                        Settings.Set("UiBackgroundApi", CurrentIndex)
                        Success = True
                        Exit For
                    Catch ex As Exception
                        Logger.Warn(ex, $"获取在线壁纸失败（{Url}）")
                    End Try
                Next
                If Not Success Then Throw New Exception("所有在线壁纸 API 均获取失败")
                RunInUi(
                Sub()
                    Try
                        If FrmMain Is Nothing Then Return
                        FrmMain.ImgBack.Background = New MyBitmap(Target)
                        FrmMain.ImgBack.Visibility = Visibility.Visible
                        FrmMain.UpdateBackgroundAndTitleBar()
                    Catch ex As Exception
                        Logger.Warn(ex, "应用在线壁纸失败")
                    End Try
                End Sub)
                If ShowHint Then Hint("已更换网络壁纸！", HintType.Green, False)
                Return True
            Catch ex As Exception
                Logger.Warn(ex, "刷新在线壁纸失败")
                If ShowHint Then Hint("获取网络壁纸失败，请检查网络连接", HintType.Red)
                Return False
            End Try
        End SyncLock
    End Function

    ''' <summary>
    ''' 在后台线程刷新在线壁纸。
    ''' </summary>
    Public Sub RefreshOnlineWallpaperAsync(Optional Force As Boolean = False)
        RunInNewThread(
        Sub()
            RefreshOnlineWallpaper(Force, True)
        End Sub, "在线壁纸", ThreadPriority.BelowNormal)
    End Sub

End Module