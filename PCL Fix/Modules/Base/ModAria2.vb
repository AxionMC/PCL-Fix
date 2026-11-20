''' <summary>
''' PCL Fix：aria2c 多线程下载引擎。
''' 通过启动本地 aria2c 并使用其 JSON-RPC 接口，把 PCL 的大文件下载交给 aria2 处理，
''' 从而获得真正的分段并发、多源回退、断点续传与实时速度统计。
''' 若本机没有 aria2c，会自动尝试从官方 Release 或镜像下载一次。
''' </summary>
Public Module ModAria2

#Region "常量"

    ''' <summary>
    ''' 自动下载 aria2c 时使用的版本。
    ''' </summary>
    Public Const Aria2Version As String = "1.37.0"

    ''' <summary>
    ''' aria2c 各下载地址，依次尝试。
    ''' </summary>
    Private ReadOnly Aria2DownloadUrls As String() = {
        $"https://github.com/aria2/aria2/releases/download/release-{Aria2Version}/aria2-{Aria2Version}-win-64bit-build1.zip",
        $"https://ghproxy.net/https://github.com/aria2/aria2/releases/download/release-{Aria2Version}/aria2-{Aria2Version}-win-64bit-build1.zip",
        $"https://ghfast.top/https://github.com/aria2/aria2/releases/download/release-{Aria2Version}/aria2-{Aria2Version}-win-64bit-build1.zip",
        $"https://mirror.ghproxy.com/https://github.com/aria2/aria2/releases/download/release-{Aria2Version}/aria2-{Aria2Version}-win-64bit-build1.zip",
        $"https://github.com/aria2/aria2/releases/download/release-{Aria2Version}/aria2-{Aria2Version}-win-32bit-build1.zip"
    }

    ''' <summary>
    ''' aria2 分段数上限。aria2 单服务器最大并发连接数被限制在 16。
    ''' </summary>
    Public Const Aria2MaxSplit As Integer = 16

#End Region

#Region "状态"

    Private ReadOnly ServerLock As New Object
    Private ServerProcess As Process = Nothing
    Private ServerPort As Integer = -1
    Private ServerSecret As String = Nothing
    Private RpcClient As HttpClient = Nothing
    Private IsStartFailed As Boolean = False
    Private IsDownloadingEngine As Boolean = False
    Private RpcId As Integer = 0
    ''' <summary>
    ''' 上一次报告的引擎不可用原因，用于避免重复提示。
    ''' </summary>
    Private LastUnavailableReason As String = Nothing

#End Region

#Region "路径与可用性"

    ''' <summary>
    ''' 获取自定义的 aria2c 路径，若未设置则为空字符串。
    ''' </summary>
    Public ReadOnly Property Aria2CustomPath As String
        Get
            Try
                Dim Custom As String = Settings.Get(Of String)("ToolDownloadAria2Path")
                If Custom Is Nothing Then Return ""
                Custom = Custom.Trim().Trim(""""c)
                If Custom.Contains("%") Then Custom = Environment.ExpandEnvironmentVariables(Custom)
                Return Custom
            Catch ex As Exception
                Return ""
            End Try
        End Get
    End Property

    ''' <summary>
    ''' 查找可用的 aria2c.exe 路径。若找不到，返回 Nothing。
    ''' </summary>
    Public Function FindAria2() As String
        '1. 设置中手动指定的路径
        Dim Custom As String = Aria2CustomPath
        If Custom <> "" AndAlso FileUtils.Exists(Custom) Then Return Custom
        '2. 程序目录与常见子目录
        Dim Candidates As New List(Of String) From {
            Paths.Base & "aria2c.exe",
            Paths.Base & "aria2\aria2c.exe",
            Paths.Base & "Resources\aria2c.exe",
            Path.Combine(IO.Path.GetDirectoryName(PathExe), "aria2c.exe")
        }
        For Each Candidate In Candidates
            Try
                If Candidate IsNot Nothing AndAlso FileUtils.Exists(Candidate) Then Return Candidate
            Catch
            End Try
        Next
        '3. 系统 PATH
        Try
            Dim PathValue As String = Environment.GetEnvironmentVariable("PATH")
            If Not String.IsNullOrEmpty(PathValue) Then
                For Each Folder In PathValue.Split(";"c)
                    Try
                        If String.IsNullOrWhiteSpace(Folder) Then Continue For
                        Dim Candidate = Path.Combine(Folder.Trim(), "aria2c.exe")
                        If FileUtils.Exists(Candidate) Then Return Candidate
                    Catch
                    End Try
                Next
            End If
        Catch
        End Try
        Return Nothing
    End Function

    ''' <summary>
    ''' 引擎是否可用：已启动、本地已有 aria2c，或允许在下载时自动下载 aria2c。
    ''' 该属性不做任何耗时操作，可安全地在下载线程中调用。
    ''' </summary>
    Public ReadOnly Property HasEngine As Boolean
        Get
            If IsDownloadingEngine Then Return True
            If IsStartFailed Then Return False
            '即使本地暂时没有 aria2c.exe，也允许 Aria2Thread 调用 EnsureServer 进行自动下载
            Return True
        End Get
    End Property

    ''' <summary>
    ''' 异步确保 aria2c 存在（首次启用时可能会自动下载）。
    ''' </summary>
    Public Sub OnAria2SettingChanged(Value As Object)
        Try
            If CType(Value, Boolean) Then
                RunInNewThread(
                Sub()
                    If FindAria2() Is Nothing Then
                        Try
                            DownloadAria2()
                        Catch ex As Exception
                            Logger.Warn(ex, "自动下载 aria2c 失败")
                        End Try
                    End If
                    If FindAria2() IsNot Nothing Then Logger.Info($"aria2c 已就绪：{FindAria2()}")
                End Sub, "Aria2 初始化", ThreadPriority.BelowNormal)
            Else
                ShutdownEngine()
            End If
        Catch ex As Exception
            Logger.Warn(ex, "处理 Aria2 设置变更失败")
        End Try
    End Sub

#End Region

#Region "自动下载 aria2c"

    ''' <summary>
    ''' 从网络下载 aria2c 并解压到程序目录。
    ''' </summary>
    Public Sub DownloadAria2()
        SyncLock ServerLock
            If FindAria2() IsNot Nothing Then Return
            Dim ZipPath As String = PathTemp & "Aria2Engine.zip"
            Dim ExtractFolder As String = PathTemp & "Aria2Engine\"
            Dim LastError As Exception = Nothing
            Logger.Info("本地未找到 aria2c.exe，开始自动下载 aria2 引擎")
            For Each Url In Aria2DownloadUrls
                Try
                    Logger.Info($"尝试从 {Url} 下载 aria2c")
                    DirectoryUtils.Create(PathTemp)
                    FileUtils.Delete(ZipPath)
                    NetDownloadByClient(Url, ZipPath)
                    If Not FileUtils.Exists(ZipPath) Then Throw New Exception("下载得到的压缩包不存在")
                    DirectoryUtils.Delete(ExtractFolder)
                    DirectoryUtils.Create(ExtractFolder)
                    ZipFile.ExtractToDirectory(ZipPath, ExtractFolder)
                    Dim ExePath As String = Nothing
                    For Each Candidate In DirectoryUtils.EnumerateFiles(ExtractFolder, searchPattern:="aria2c.exe")
                        ExePath = Candidate
                        Exit For
                    Next
                    If ExePath Is Nothing Then Throw New Exception("压缩包中没有找到 aria2c.exe")
                    Dim Target As String = Paths.Base & "aria2c.exe"
                    FileUtils.Delete(Target)
                    FileUtils.Copy(ExePath, Target)
                    Logger.Info($"aria2c 已安装到 {Target}")
                    Return
                Catch ex As Exception
                    LastError = ex
                    Logger.Warn(ex, $"从 {Url} 获取 aria2c 失败")
                Finally
                    Try
                        FileUtils.Delete(ZipPath)
                    Catch
                    End Try
                End Try
            Next
            DirectoryUtils.Delete(ExtractFolder)
            Throw New Exception("所有下载地址均失败，请手动把 aria2c.exe 放到启动器同目录，或在设置中指定路径。", LastError)
        End SyncLock
    End Sub

#End Region

#Region "JSON-RPC 基础"

    Private Function GetRpcUrl() As String
        Return $"http://127.0.0.1:{ServerPort}/jsonrpc"
    End Function

    ''' <summary>
    ''' 启动 aria2c 并等待 RPC 就绪。
    ''' </summary>
    Public Function EnsureServer() As Boolean
        SyncLock ServerLock
            If IsDownloadingEngine AndAlso ServerProcess IsNot Nothing AndAlso Not ServerProcess.HasExited Then Return True
            If IsStartFailed Then
                '若用户手动补齐了 aria2c，则允许再次尝试启动
                If FindAria2() Is Nothing Then Return False
                IsStartFailed = False
            End If
            Try
                Dim ExePath As String = FindAria2()
                If ExePath Is Nothing Then
                    Try
                        DownloadAria2()
                    Catch ex As Exception
                        Logger.Warn(ex, "aria2c 缺失且自动下载失败，回退到内置下载引擎")
                    End Try
                    ExePath = FindAria2()
                End If
                If ExePath Is Nothing Then
                    IsStartFailed = True
                    LastUnavailableReason = "未找到 aria2c.exe"
                    Return False
                End If
                '分配端口与密钥
                ServerPort = FindFreePort()
                ServerSecret = Guid.NewGuid().ToString("N")
                DirectoryUtils.Create(PathTemp & "Aria2\")
                RpcClient = New HttpClient(New HttpClientHandler With {.UseProxy = False}) With {.Timeout = TimeSpan.FromSeconds(20)}
                Dim Args = String.Join(" ", {
                    "--enable-rpc=true",
                    "--rpc-listen-all=false",
                    "--rpc-listen-port=" & ServerPort,
                    "--rpc-secret=" & ServerSecret,
                    "--rpc-max-request-size=16M",
                    "--max-concurrent-downloads=16",
                    "--split=" & Aria2MaxSplit,
                    "--max-connection-per-server=" & Aria2MaxSplit,
                    "--min-split-size=1M",
                    "--file-allocation=none",
                    "--continue=true",
                    "--allow-overwrite=true",
                    "--auto-file-renaming=false",
                    "--check-certificate=false",
                    "--disk-cache=32M",
                    "--http-accept-gzip=true",
                    "--enable-http-keep-alive=true",
                    "--retry-wait=1",
                    "--max-tries=5",
                    "--connect-timeout=15",
                    "--timeout=30",
                    "--summary-interval=0",
                    "--console-log-level=warn",
                    "--quiet=true",
                    "--no-conf=true",
                    "--dir=" & PathTemp & "Aria2\"
                })
                Logger.Info($"启动 aria2c：{ExePath}（端口 {ServerPort}）")
                ServerProcess = StartProcess(New ProcessStartInfo With {
                    .FileName = ExePath,
                    .Arguments = Args,
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .WindowStyle = ProcessWindowStyle.Hidden,
                    .WorkingDirectory = Paths.Base
                })
                '等待 RPC 就绪
                Dim StartTime As Long = GetTimeMs()
                While GetTimeMs() - StartTime < 12000
                    If ServerProcess.HasExited Then
                        Throw New Exception($"aria2c 启动后立即退出（退出码 {ServerProcess.ExitCode}）")
                    End If
                    Try
                        Dim Version = Rpc("aria2.getVersion")
                        If Version IsNot Nothing Then
                            IsDownloadingEngine = True
                            IsStartFailed = False
                            LastUnavailableReason = Nothing
                            Logger.Info($"aria2c 已就绪，版本 {CType(Version, JObject)("version")}")
                            Return True
                        End If
                    Catch
                    End Try
                    Thread.Sleep(200)
                End While
                Throw New TimeoutException("aria2c 的 RPC 接口在 12 秒内未就绪")
            Catch ex As Exception
                Logger.Warn(ex, "启动 aria2c 失败，回退到内置下载引擎")
                IsStartFailed = True
                LastUnavailableReason = ex.Message
                ShutdownEngineInternal()
                Return False
            End Try
        End SyncLock
    End Function

    ''' <summary>
    ''' 发送一次 JSON-RPC 请求并返回 result 部分。
    ''' </summary>
    Private Function Rpc(Method As String, ParamArray Params As Object()) As JToken
        If RpcClient Is Nothing OrElse ServerPort <= 0 Then Throw New Exception("aria2 RPC 尚未启动")
        Dim Id As Integer = Interlocked.Increment(RpcId)
        Dim Request As New JObject From {
            {"jsonrpc", "2.0"},
            {"id", Id.ToString()},
            {"method", Method}
        }
        Dim ParamList As New JArray From {New JValue("token:" & ServerSecret)}
        For Each P In Params
            If P Is Nothing Then
                ParamList.Add(JValue.CreateNull())
            ElseIf TypeOf P Is JToken Then
                ParamList.Add(CType(P, JToken))
            Else
                ParamList.Add(New JValue(P))
            End If
        Next
        Request("params") = ParamList
        Dim Body As String = Request.ToString(Newtonsoft.Json.Formatting.None)
        Dim Content As New StringContent(Body, Encoding.UTF8, "application/json")
        Dim Response As HttpResponseMessage = RpcClient.PostAsync(GetRpcUrl(), Content).GetAwaiter().GetResult()
        Dim ResponseText As String = Response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        If Not Response.IsSuccessStatusCode Then
            Throw New Exception($"aria2 RPC 返回错误状态码 {CInt(Response.StatusCode)}：{ResponseText}")
        End If
        Dim Result As JObject = JObject.Parse(ResponseText)
        If Result("error") IsNot Nothing Then
            Dim ErrorObj As JObject = CType(Result("error"), JObject)
            Throw New Exception($"aria2 RPC 调用 {Method} 失败：{ErrorObj("code")} {ErrorObj("message")}")
        End If
        Return Result("result")
    End Function

#End Region

#Region "下载任务接口"

    ''' <summary>
    ''' 添加一个下载任务，返回 GID。
    ''' </summary>
    ''' <param name="Urls">下载源列表，aria2 会自动回退。</param>
    ''' <param name="Dir">保存目录。</param>
    ''' <param name="FileName">保存文件名。</param>
    ''' <param name="Split">分段连接数。</param>
    ''' <param name="Headers">额外的 HTTP 头，形如 "User-Agent: xxx"。</param>
    Public Function AddUri(Urls As IEnumerable(Of String), Dir As String, FileName As String,
                           Optional Split As Integer = Aria2MaxSplit, Optional Headers As IEnumerable(Of String) = Nothing,
                           Optional UserAgent As String = Nothing, Optional Referer As String = Nothing) As String
        Dim SplitCount As Integer = Split.Clamp(1, Aria2MaxSplit)
        '整理下载源
        Dim UriArray As New JArray
        For Each Url In Urls
            If Url Is Nothing Then Continue For
            UriArray.Add(New JValue(Url.Trim()))
        Next
        If Not UriArray.Any() Then Throw New Exception("aria2 任务没有可用的下载源")
        '整理参数
        Dim Options As New JObject From {
            {"dir", Dir},
            {"out", FileName},
            {"continue", "true"},
            {"allow-overwrite", "true"},
            {"auto-file-renaming", "false"},
            {"split", SplitCount.ToString()},
            {"max-connection-per-server", SplitCount.ToString()},
            {"min-split-size", "1M"},
            {"file-allocation", "none"},
            {"check-certificate", "false"},
            {"retry-wait", "1"},
            {"max-tries", "5"},
            {"connect-timeout", "15"},
            {"timeout", "30"},
            {"lowest-speed-limit", "8K"}
        }
        If Not String.IsNullOrEmpty(UserAgent) Then Options("user-agent") = UserAgent
        If Not String.IsNullOrEmpty(Referer) Then Options("referer") = Referer
        If Headers IsNot Nothing Then
            Dim HeaderArray As New JArray
            For Each Header In Headers
                If Header IsNot Nothing Then HeaderArray.Add(New JValue(Header))
            Next
            If HeaderArray.Any() Then Options("header") = HeaderArray
        End If
        Dim Result = Rpc("aria2.addUri", UriArray, Options)
        If Result Is Nothing Then Throw New Exception("aria2 未返回有效的 GID")
        Dim Gid As String = Result.ToString()
        Logger.Info($"aria2 任务已创建：{Gid} → {Dir}{FileName}（{UriArray.Count} 个下载源，{SplitCount} 线程）")
        Return Gid
    End Function

    ''' <summary>
    ''' 查询任务状态。
    ''' </summary>
    Public Function TellStatus(Gid As String) As JObject
        Dim Keys As New JArray From {New JValue("gid"), New JValue("status"), New JValue("totalLength"), New JValue("completedLength"),
                                     New JValue("downloadSpeed"), New JValue("connections"), New JValue("errorCode"),
                                     New JValue("errorMessage"), New JValue("files")}
        Dim Result = Rpc("aria2.tellStatus", Gid, Keys)
        Return TryCast(Result, JObject)
    End Function

    ''' <summary>
    ''' 取消并清理一个任务。
    ''' </summary>
    Public Sub Remove(Gid As String, Optional Force As Boolean = False)
        If Gid Is Nothing Then Return
        Try
            Rpc("aria2.remove", Gid)
        Catch
            '任务可能已完成或已被移除，忽略
        End Try
        Try
            Rpc("aria2.removeDownloadResult", Gid)
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 查询全局速度，单位为 Byte/s。
    ''' </summary>
    Public Function GetGlobalSpeed() As Long
        Try
            Dim Stat = Rpc("aria2.getGlobalStat")
            If Stat Is Nothing Then Return 0
            Return CLng(CType(Stat, JObject)("downloadSpeed").ToString())
        Catch
            Return 0
        End Try
    End Function

#End Region

#Region "关闭"

    ''' <summary>
    ''' 立即停止 aria2 进程。
    ''' </summary>
    Public Sub ShutdownEngine()
        SyncLock ServerLock
            ShutdownEngineInternal()
        End SyncLock
    End Sub

    ''' <summary>
    ''' 在程序退出时兜底结束 aria2c，避免留下后台进程。
    ''' </summary>
    Public Sub ShutdownEngineOnExit()
        Try
            ShutdownEngine()
        Catch
        End Try
        Try
            If ServerProcess IsNot Nothing AndAlso Not ServerProcess.HasExited Then ServerProcess.Kill()
        Catch
        End Try
    End Sub

    Private Sub ShutdownEngineInternal()
        Try
            If ServerProcess IsNot Nothing AndAlso Not ServerProcess.HasExited Then
                Try
                    Rpc("aria2.shutdown")
                Catch
                End Try
                If Not ServerProcess.WaitForExit(3000) Then ServerProcess.Kill()
            End If
        Catch ex As Exception
            Logger.Warn(ex, "关闭 aria2c 进程失败")
        Finally
            ServerProcess = Nothing
            RpcClient = Nothing
            IsDownloadingEngine = False
            ServerPort = -1
            ServerSecret = Nothing
        End Try
    End Sub

#End Region

End Module