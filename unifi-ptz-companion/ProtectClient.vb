Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Json
Imports System.Text.Json

' Handles everything to do with talking to UniFi Protect: logging in,
' keeping the session/CSRF token fresh, discovering cameras and their
' preset slots, and moving the currently-selected camera.
'
' State (selected camera, cached camera list) lives in memory only — it
' resets to "none selected" if this process restarts.
Public Class ProtectClient
    Private ReadOnly _config As AppConfig
    Private ReadOnly _httpClient As HttpClient
    Private ReadOnly _lock As New Object()

    Private _deviceToken As String
    Private _csrfToken As String
    Private _lastAuthUtc As DateTime = DateTime.MinValue
    Private ReadOnly _reauthInterval As TimeSpan = TimeSpan.FromHours(1)

    Private _selectedCameraId As String
    Private _cameraCache As List(Of CameraInfo)
    Private _cameraCacheUtc As DateTime = DateTime.MinValue
    Private ReadOnly _cameraCacheTtl As TimeSpan = TimeSpan.FromSeconds(30)

    Public Sub New(config As AppConfig)
        _config = config
        Dim handler As New HttpClientHandler() With {
            .UseCookies = True,
            .ServerCertificateCustomValidationCallback = Function(msg, cert, chain, errors) True ' Protect uses a self-signed cert locally
        }
        _httpClient = New HttpClient(handler) With {.BaseAddress = New Uri(config.NvrAddress)}
    End Sub

    ' --- Authentication ---

    Public Async Function LoginAsync() As Task
        Dim body = New With {
            .username = _config.Username,
            .password = _config.Password,
            .rememberMe = True,
            .token = ""
        }
        Dim response = Await _httpClient.PostAsJsonAsync("/api/auth/login", body)
        If Not response.IsSuccessStatusCode Then
            Dim errorBody = Await response.Content.ReadAsStringAsync()
            Throw New Exception($"Login failed (HTTP {CInt(response.StatusCode)}): {errorBody}")
        End If

        Dim json = Await response.Content.ReadFromJsonAsync(Of JsonElement)()
        _deviceToken = json.GetProperty("deviceToken").GetString()

        Dim csrfValues As IEnumerable(Of String) = Nothing
        If response.Headers.TryGetValues("X-CSRF-Token", csrfValues) Then
            _csrfToken = csrfValues.FirstOrDefault()
        End If

        _lastAuthUtc = DateTime.UtcNow
        Console.WriteLine("Authenticated to UniFi Protect")
    End Function

    Private Async Function EnsureAuthAsync() As Task
        If DateTime.UtcNow - _lastAuthUtc > _reauthInterval Then
            Await LoginAsync()
        End If
    End Function

    Private Function BuildRequest(method As HttpMethod, path As String, Optional jsonBody As Object = Nothing) As HttpRequestMessage
        Dim req As New HttpRequestMessage(method, path)
        req.Headers.Add("TOKEN", _deviceToken)
        req.Headers.Add("X-CSRF-Token", _csrfToken)
        If jsonBody IsNot Nothing Then
            req.Content = JsonContent.Create(jsonBody)
        End If
        Return req
    End Function

    ' Sends a request; on 401/403 re-authenticates once and retries.
    ' A request body has to be rebuilt on retry since HttpRequestMessage
    ' instances can't be sent twice.
    Private Async Function SendAsync(method As HttpMethod, path As String, Optional jsonBody As Object = Nothing) As Task(Of HttpResponseMessage)
        Await EnsureAuthAsync()
        Dim response = Await _httpClient.SendAsync(BuildRequest(method, path, jsonBody))
        If response.StatusCode = HttpStatusCode.Unauthorized OrElse response.StatusCode = HttpStatusCode.Forbidden Then
            Await LoginAsync()
            response = Await _httpClient.SendAsync(BuildRequest(method, path, jsonBody))
        End If
        Return response
    End Function

    ' --- Camera discovery ---

    ' Looks up a camera (with its presets) from the cache populated by
    ' GetCamerasWithPresetsAsync. Returns Nothing if not cached yet.
    Public Function GetCachedCamera(cameraId As String) As CameraInfo
        SyncLock _lock
            If _cameraCache Is Nothing Then Return Nothing
            Return _cameraCache.Find(Function(c) c.Id = cameraId)
        End SyncLock
    End Function

    Public Async Function GetCamerasWithPresetsAsync(Optional forceRefresh As Boolean = False) As Task(Of List(Of CameraInfo))
        SyncLock _lock
            If Not forceRefresh AndAlso _cameraCache IsNot Nothing AndAlso (DateTime.UtcNow - _cameraCacheUtc) < _cameraCacheTtl Then
                Return _cameraCache
            End If
        End SyncLock

        Dim camsResponse = Await SendAsync(HttpMethod.Get, "/proxy/protect/api/cameras")
        If Not camsResponse.IsSuccessStatusCode Then
            Throw New Exception($"Failed to list cameras (HTTP {CInt(camsResponse.StatusCode)})")
        End If

        Dim camsJson = Await camsResponse.Content.ReadFromJsonAsync(Of List(Of JsonElement))()
        Dim result As New List(Of CameraInfo)()

        For Each cam In camsJson
            Dim id = cam.GetProperty("id").GetString()
            Dim name = cam.GetProperty("name").GetString()
            Dim presets As New List(Of PresetInfo)()

            Dim presetsResponse = Await SendAsync(HttpMethod.Get, $"/proxy/protect/api/cameras/{id}/ptz/preset")
            If presetsResponse.IsSuccessStatusCode Then
                Dim presetsJson = Await presetsResponse.Content.ReadFromJsonAsync(Of List(Of JsonElement))()
                For Each p In presetsJson
                    Dim nameElement As JsonElement
                    Dim presetName As String = ""
                    If p.TryGetProperty("name", nameElement) Then presetName = nameElement.GetString()
                    presets.Add(New PresetInfo With {.Slot = p.GetProperty("slot").GetInt32(), .Name = presetName})
                Next
            End If

            result.Add(New CameraInfo With {.Id = id, .Name = name, .Presets = presets})
        Next

        SyncLock _lock
            _cameraCache = result
            _cameraCacheUtc = DateTime.UtcNow
        End SyncLock

        Return result
    End Function

    ' --- Selection state (what Companion's "camera select" buttons drive) ---

    Public Function SelectCamera(cameraId As String) As SelectionStatus
        SyncLock _lock
            _selectedCameraId = cameraId
        End SyncLock
        Return GetSelectedCamera()
    End Function

    Public Function GetSelectedCamera() As SelectionStatus
        SyncLock _lock
            Dim name As String = Nothing
            If _cameraCache IsNot Nothing AndAlso _selectedCameraId IsNot Nothing Then
                Dim match = _cameraCache.Find(Function(c) c.Id = _selectedCameraId)
                If match IsNot Nothing Then name = match.Name
            End If
            Return New SelectionStatus With {.SelectedCameraId = _selectedCameraId, .SelectedCameraName = name}
        End SyncLock
    End Function

    ' --- PTZ control ---

    ' If cameraId is omitted, operates on whatever was last set via SelectCamera.
    ' Slot -1 means "go to home position."
    Public Async Function GotoPresetAsync(slot As Integer, Optional cameraId As String = Nothing) As Task(Of GotoResult)
        Dim targetId As String = cameraId
        If String.IsNullOrEmpty(targetId) Then
            SyncLock _lock
                targetId = _selectedCameraId
            End SyncLock
        End If

        If String.IsNullOrEmpty(targetId) Then
            Return New GotoResult With {.Success = False, .StatusCode = 400, .Message = "No camera selected — call /select/{cameraId} first"}
        End If

        Dim response = Await SendAsync(HttpMethod.Post, $"/proxy/protect/api/cameras/{targetId}/ptz/goto/{slot}")
        Dim code = CInt(response.StatusCode)

        If response.IsSuccessStatusCode Then
            Return New GotoResult With {.Success = True, .StatusCode = code, .Message = $"Moved {targetId} to slot {slot}"}
        Else
            Return New GotoResult With {.Success = False, .StatusCode = code, .Message = $"Goto failed (HTTP {code})"}
        End If
    End Function

End Class
