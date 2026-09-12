Imports System.IO
Imports System.Text.Json

' Loaded from config.json next to the exe. Created with defaults on first
' run if it doesn't exist yet, so you're not editing source to configure it.
Public Class AppConfig
    Public Property NvrAddress As String = "https://192.168.1.1"
    Public Property Username As String = "api-user"
    Public Property Password As String = "changeme"
    Public Property ListenPort As Integer = 5000

    Public Shared Function LoadOrCreate(path As String) As AppConfig
        If Not File.Exists(path) Then
            Dim defaultConfig As New AppConfig()
            File.WriteAllText(path, JsonSerializer.Serialize(defaultConfig, New JsonSerializerOptions With {.WriteIndented = True}))
            Console.WriteLine($"No config found — created {path}.")
            Console.WriteLine("Edit it with your NVR address and a local (non-SSO) admin username/password, then run this again.")
            Environment.Exit(0)
        End If
        Dim json = File.ReadAllText(path)
        Return JsonSerializer.Deserialize(Of AppConfig)(json)
    End Function
End Class

Public Class PresetInfo
    Public Property Slot As Integer
    Public Property Name As String
End Class

Public Class CameraInfo
    Public Property Id As String
    Public Property Name As String
    Public Property Presets As New List(Of PresetInfo)()
End Class

Public Class GotoResult
    Public Property Success As Boolean
    Public Property StatusCode As Integer
    Public Property Message As String
End Class

' What /status and /select return — lets you confirm from a browser or
' Companion's HTTP feedback which camera is currently active.
Public Class SelectionStatus
    Public Property SelectedCameraId As String
    Public Property SelectedCameraName As String
End Class
