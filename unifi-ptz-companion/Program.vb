Imports System.Net
Imports System.Text
Imports System.Text.Json
Imports System.Threading.Tasks
Imports System.Windows.Forms

' UniFi Protect PTZ control app.
'
' Runs two things side by side:
'   1. A WinForms window (MainForm) with camera/preset/direction buttons.
'   2. A lightweight HttpListener-based server in the background, so
'      Bitfocus Companion can keep driving this over the network with the
'      same endpoints as before. HttpListener is part of the base .NET
'      runtime (System.Net), not ASP.NET Core, so it works the same on
'      .NET 5 as it would on .NET 8 -- no minimal-API version dependency.
' Both share one ProtectClient instance and its selection state.
Module Program

    <STAThread>
    Sub Main()
        Application.SetHighDpiMode(HighDpiMode.SystemAware)
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        Dim config = AppConfig.LoadOrCreate("config.json")
        Dim protectClient As New ProtectClient(config)

        Try
            protectClient.LoginAsync().GetAwaiter().GetResult()
        Catch ex As Exception
            Console.WriteLine($"Initial login failed: {ex.Message}")
            Console.WriteLine("The app will still open and retry authentication on the first request.")
        End Try

        Dim listener As New HttpListener()
        Dim url = $"http://localhost:{config.ListenPort}/"
        listener.Prefixes.Add(url)
        listener.Start()
        Console.WriteLine($"Companion endpoints listening on {url}")
        Task.Run(Function() ListenLoopAsync(listener, protectClient))

        Application.Run(New MainForm(protectClient))

        listener.Stop()
        listener.Close()
    End Sub

    ' Accepts requests forever until the listener is stopped (on form close).
    ' Each request is handled on its own background task so a slow request
    ' (e.g. waiting on Protect) doesn't block accepting the next one.

    ' Same routes as before:
    '   GET /cameras
    '   GET /select/{cameraId}
    '   GET /status
    '   GET /goto/{slot}
    '   GET /goto/{cameraId}/{slot}
    Private Async Function ListenLoopAsync(listener As HttpListener, client As ProtectClient) As Task
        While listener.IsListening
            Try
                Dim ctx = Await listener.GetContextAsync()
                Dim requestTask = Task.Run(Function() HandleRequestAsync(ctx, client))
            Catch ex As HttpListenerException
                Exit While ' listener was stopped
            Catch ex As ObjectDisposedException
                Exit While
            Catch ex As Exception
                Console.WriteLine($"Listener error: {ex.Message}")
            End Try
        End While
    End Function

    ' Same routes as before:
    '   GET /cameras
    '   GET /select/{cameraId}
    '   GET /status
    '   GET /goto/{slot}
    '   GET /goto/{cameraId}/{slot}
    Private Async Function HandleRequestAsync(ctx As HttpListenerContext, client As ProtectClient) As Task
        ' VB doesn't allow Await inside a Catch block, so on failure we just
        ' record what to send and write the response after the Try/Catch ends.
        Dim errorStatus As Integer = 0
        Dim errorMessage As String = Nothing

        Try
            Dim segments = ctx.Request.Url.AbsolutePath.Trim("/"c).Split("/"c)
            If segments.Length = 0 OrElse String.IsNullOrEmpty(segments(0)) Then
                Await WriteText(ctx, 404, "Not found")
                Return
            End If

            Select Case segments(0)
                Case "cameras"
                    Dim cams = Await client.GetCamerasWithPresetsAsync()
                    Await WriteJson(ctx, 200, cams)

                Case "select"
                    If segments.Length <> 2 Then
                        Await WriteText(ctx, 400, "Usage: /select/{cameraId}")
                        Return
                    End If
                    Dim status = client.SelectCamera(segments(1))
                    Await WriteJson(ctx, 200, status)

                Case "status"
                    Await WriteJson(ctx, 200, client.GetSelectedCamera())

                Case "goto"
                    Dim slot As Integer
                    If segments.Length = 2 AndAlso Integer.TryParse(segments(1), slot) Then
                        Dim result = Await client.GotoPresetAsync(slot)
                        Await WriteText(ctx, If(result.Success, 200, result.StatusCode), result.Message)
                    ElseIf segments.Length = 3 AndAlso Integer.TryParse(segments(2), slot) Then
                        Dim result = Await client.GotoPresetAsync(slot, segments(1))
                        Await WriteText(ctx, If(result.Success, 200, result.StatusCode), result.Message)
                    Else
                        Await WriteText(ctx, 400, "Usage: /goto/{slot} or /goto/{cameraId}/{slot}")
                    End If

                Case Else
                    Await WriteText(ctx, 404, "Not found")
            End Select

        Catch ex As Exception
            errorStatus = 500
            errorMessage = ex.Message
        End Try

        If errorMessage IsNot Nothing Then
            Try
                Await WriteText(ctx, errorStatus, errorMessage)
            Catch
                ' response may already be closed -- nothing more to do
            End Try
        End If
    End Function

    Private Async Function WriteJson(ctx As HttpListenerContext, statusCode As Integer, data As Object) As Task
        Dim json = JsonSerializer.Serialize(data)
        Dim bytes = Encoding.UTF8.GetBytes(json)
        ctx.Response.StatusCode = statusCode
        ctx.Response.ContentType = "application/json"
        ctx.Response.ContentLength64 = bytes.Length
        Await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
        ctx.Response.Close()
    End Function

    Private Async Function WriteText(ctx As HttpListenerContext, statusCode As Integer, text As String) As Task
        Dim bytes = Encoding.UTF8.GetBytes(text)
        ctx.Response.StatusCode = statusCode
        ctx.Response.ContentType = "text/plain"
        ctx.Response.ContentLength64 = bytes.Length
        Await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
        ctx.Response.Close()
    End Function

End Module
