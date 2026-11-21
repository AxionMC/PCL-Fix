''' <summary>
''' PCL Fix：BT 种子 / 磁力链接下载。
''' 复用 aria2c 引擎（BitTorrent 协议），提供 128 连接的任务管理。
''' </summary>
Public Module ModTorrent

    ''' <summary>
    ''' 一个 BT 下载任务。
    ''' </summary>
    Public Class BtTask
        Public Gid As String = Nothing
        Public Name As String = ""
        Public Dir As String = ""
        Public Source As String = ""
        Public State As String = "waiting"
        Public Total As Long = 0
        Public Done As Long = 0
        Public Speed As Long = 0
        Public Peers As Integer = 0
        Public Err As String = Nothing
        Public StartTime As Long = GetTimeMs()

        ''' <summary>
        ''' 进度，0 ~ 1。
        ''' </summary>
        Public ReadOnly Property Progress As Double
            Get
                If State = "complete" Then Return 1
                If Total <= 0 Then Return 0
                Return Math.Min(1, Done / Total)
            End Get
        End Property

        ''' <summary>
        ''' 状态的中文描述。
        ''' </summary>
        Public ReadOnly Property StateText As String
            Get
                Select Case State
                    Case "active" : Return "下载中"
                    Case "waiting" : Return "等待中"
                    Case "paused" : Return "已暂停"
                    Case "complete" : Return "已完成"
                    Case "error" : Return "出错"
                    Case "removed" : Return "已删除"
                    Case Else : Return If(State, "未知")
                End Select
            End Get
        End Property

        Public ReadOnly Property SizeText As String
            Get
                Return $"{StringUtils.FormatByteSize(Done)} / {StringUtils.FormatByteSize(Total)}"
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 所有 BT 任务，键为 aria2 的 GID。
    ''' </summary>
    Public ReadOnly BtTasks As New ConcurrentDictionary(Of String, BtTask)

    ''' <summary>
    ''' 默认下载目录。
    ''' </summary>
    Public Function DefaultFolder() As String
        Dim Folder As String = Settings.Get(Of String)("ToolTorrentFolder")
        If String.IsNullOrWhiteSpace(Folder) Then Folder = Paths.Base & "PCL\BT"
        Folder = Folder.Replace("/", "\")
        If Not Folder.EndsWith("\") Then Folder &= "\"
        DirectoryUtils.Create(Folder)
        Return Folder
    End Function

    ''' <summary>
    ''' 开始一个磁力链接任务。
    ''' </summary>
    Public Function StartMagnet(Magnet As String, Optional Folder As String = Nothing) As BtTask
        Magnet = If(Magnet, "").Trim()
        If Not Magnet.StartsWithF("magnet:", True) Then Throw New Exception("磁力链接格式不正确，应以 magnet:?xt=urn:btih: 开头")
        Dim TargetFolder As String = If(Folder, DefaultFolder())
        Dim Gid As String = ModAria2.AddMagnet(Magnet, TargetFolder)
        Dim TaskInfo As New BtTask With {.Gid = Gid, .Dir = TargetFolder, .Source = Magnet}
        BtTasks(Gid) = TaskInfo
        Try
            ApplyStatus(TaskInfo, ModAria2.TellStatus(Gid))
        Catch ex As Exception
            Logger.Warn(ex, "读取 BT 任务初始状态失败")
        End Try
        Return TaskInfo
    End Function

    ''' <summary>
    ''' 开始一个 .torrent 种子文件任务。
    ''' </summary>
    Public Function StartTorrentFile(FilePath As String, Optional Folder As String = Nothing) As BtTask
        If Not FileUtils.Exists(FilePath) Then Throw New Exception("种子文件不存在：" & FilePath)
        Dim TargetFolder As String = If(Folder, DefaultFolder())
        Dim Gid As String = ModAria2.AddTorrentFile(FilePath, TargetFolder)
        Dim TaskInfo As New BtTask With {.Gid = Gid, .Dir = TargetFolder, .Source = FilePath}
        BtTasks(Gid) = TaskInfo
        Try
            ApplyStatus(TaskInfo, ModAria2.TellStatus(Gid))
        Catch ex As Exception
            Logger.Warn(ex, "读取 BT 任务初始状态失败")
        End Try
        Return TaskInfo
    End Function

    ''' <summary>
    ''' 把 aria2 的状态写回任务对象。
    ''' </summary>
    Public Sub ApplyStatus(TaskInfo As BtTask, Stat As JObject)
        If TaskInfo Is Nothing OrElse Stat Is Nothing Then Return
        TaskInfo.State = Stat("status").ToString()
        Dim TempLong As Long = 0
        If Long.TryParse(Stat("totalLength").ToString(), TempLong) Then TaskInfo.Total = TempLong
        If Long.TryParse(Stat("completedLength").ToString(), TempLong) Then TaskInfo.Done = TempLong
        If Long.TryParse(Stat("downloadSpeed").ToString(), TempLong) Then TaskInfo.Speed = TempLong
        Dim TempInt As Integer = 0
        If Integer.TryParse(Stat("connections").ToString(), TempInt) Then TaskInfo.Peers = TempInt
        TaskInfo.Err = If(Stat("errorMessage") IsNot Nothing, Stat("errorMessage").ToString(), Nothing)
        'BT 名称
        Dim BtObject As JObject = TryCast(Stat("bittorrent"), JObject)
        If BtObject IsNot Nothing Then
            Dim InfoObject As JObject = TryCast(BtObject("info"), JObject)
            If InfoObject IsNot Nothing AndAlso InfoObject("name") IsNot Nothing Then
                TaskInfo.Name = InfoObject("name").ToString()
            End If
            If TaskInfo.Name = "" AndAlso BtObject("info") IsNot Nothing Then
                TaskInfo.Name = BtObject("info").ToString()
            End If
        End If
        If TaskInfo.Name = "" Then
            Dim FilesObject As JArray = TryCast(Stat("files"), JArray)
            If FilesObject IsNot Nothing AndAlso FilesObject.Count > 0 Then
                Dim FirstPath As JToken = FilesObject(0)("path")
                If FirstPath IsNot Nothing Then TaskInfo.Name = PathUtils.GetLastPart(FirstPath.ToString())
            End If
        End If
        If TaskInfo.Name = "" Then TaskInfo.Name = "BT 任务 " & TaskInfo.Gid
    End Sub

    ''' <summary>
    ''' 刷新所有任务的状态。应在工作线程调用。
    ''' </summary>
    Public Sub RefreshAll()
        For Each TaskInfo As BtTask In BtTasks.Values
            Try
                ApplyStatus(TaskInfo, ModAria2.TellStatus(TaskInfo.Gid))
            Catch ex As Exception
                TaskInfo.Err = ex.Message
                If TaskInfo.State = "active" Then TaskInfo.State = "error"
            End Try
        Next
    End Sub

    ''' <summary>
    ''' 暂停任务。
    ''' </summary>
    Public Sub Pause(TaskInfo As BtTask)
        If TaskInfo Is Nothing Then Return
        ModAria2.PauseGid(TaskInfo.Gid)
        TaskInfo.State = "paused"
    End Sub

    ''' <summary>
    ''' 继续任务。
    ''' </summary>
    Public Sub ResumeTask(TaskInfo As BtTask)
        If TaskInfo Is Nothing Then Return
        ModAria2.UnpauseGid(TaskInfo.Gid)
        TaskInfo.State = "active"
    End Sub

    ''' <summary>
    ''' 删除任务。
    ''' </summary>
    Public Sub Remove(TaskInfo As BtTask)
        If TaskInfo Is Nothing Then Return
        Try
            ModAria2.Remove(TaskInfo.Gid, True)
        Catch
        End Try
        Dim Removed As BtTask = Nothing
        BtTasks.TryRemove(TaskInfo.Gid, Removed)
    End Sub

End Module