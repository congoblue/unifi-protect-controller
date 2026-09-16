Imports System.Drawing
Imports System.Windows.Forms

Public Class MainForm
    Inherits Form

    Private ReadOnly _protectClient As ProtectClient
    Private ReadOnly _cameraButtons(31) As Button
    Private ReadOnly _presetButtons(9) As Button
    Private _homeButton As Button
    Private ReadOnly _logBox As TextBox
    Private ReadOnly _lblSelected As Label

    Public Sub New(protectClient As ProtectClient)
        _protectClient = protectClient

        Me.Text = "UniFi PTZ Control"
        Me.ClientSize = New Size(1000, 740)
        Me.FormBorderStyle = FormBorderStyle.FixedSingle
        Me.MaximizeBox = False

        _lblSelected = New Label() With {
            .Text = "Selected: (none)",
            .Location = New Point(12, 12),
            .Size = New Size(970, 20),
            .Font = New Font(Me.Font, FontStyle.Bold)
        }
        Me.Controls.Add(_lblSelected)

        Dim grpCameras = BuildCameraGroup()
        grpCameras.Location = New Point(12, 40)
        Me.Controls.Add(grpCameras)

        Dim grpPresets = BuildPresetGroup()
        grpPresets.Location = New Point(524, 40)
        Me.Controls.Add(grpPresets)

        _logBox = New TextBox() With {
            .Multiline = True,
            .ReadOnly = True,
            .ScrollBars = ScrollBars.Vertical,
            .Location = New Point(12, 482),
            .Size = New Size(974, 237),
            .Font = New Font("Consolas", 8.5F)
        }
        Me.Controls.Add(_logBox)

        AddHandler Me.Load, AddressOf MainForm_Load
    End Sub

    ' --- Layout builders ---

    Private Function BuildCameraGroup() As GroupBox
        Dim grp As New GroupBox() With {.Text = "Cameras", .Size = New Size(500, 430)}
        Const btnW As Integer = 110, btnH As Integer = 40, gap As Integer = 8
        For i As Integer = 0 To 31
            Dim col = i Mod 4
            Dim row = i \ 4
            Dim btn As New Button() With {
                .Text = $"Cam {i + 1}",
                .Location = New Point(10 + col * (btnW + gap), 25 + row * (btnH + gap)),
                .Size = New Size(btnW, btnH),
                .Enabled = False, ' enabled once mapped to a real camera on load
                .Tag = Nothing
            }
            AddHandler btn.Click, AddressOf CameraButton_Click
            _cameraButtons(i) = btn
            grp.Controls.Add(btn)
        Next
        Return grp
    End Function

    Private Function BuildPresetGroup() As GroupBox
        Dim grp As New GroupBox() With {.Text = "Presets", .Size = New Size(462, 240)}
        Const btnW As Integer = 80, btnH As Integer = 40, gap As Integer = 8
        For i As Integer = 1 To 10
            Dim col = (i - 1) Mod 5
            Dim row = (i - 1) \ 5
            Dim btn As New Button() With {
                .Text = $"Preset {i}",
                .Location = New Point(10 + col * (btnW + gap), 25 + row * (btnH + gap)),
                .Size = New Size(btnW, btnH),
                .Tag = i - 1 ' Protect's preset slots are 0-referenced; buttons are labeled 1-10
            }
            AddHandler btn.Click, AddressOf PresetButton_Click
            _presetButtons(i - 1) = btn
            grp.Controls.Add(btn)
        Next

        ' Bonus button beyond the requested 1-10 -- Protect treats slot -1 as
        ' "home position," which is handy enough to include. Remove if unwanted.
        Dim homeBtn As New Button() With {
            .Text = "Home",
            .Location = New Point(10, 121),
            .Size = New Size(4 * 80 + 3 * gap, btnH),
            .Tag = -1
        }
        AddHandler homeBtn.Click, AddressOf PresetButton_Click
        _homeButton = homeBtn
        grp.Controls.Add(homeBtn)

        Return grp
    End Function

    ' --- Event handlers ---

    Private Async Sub MainForm_Load(sender As Object, e As EventArgs)
        Log("Loading cameras...")
        Try
            Dim cams = Await _protectClient.GetCamerasWithPresetsAsync()
            For i As Integer = 0 To _cameraButtons.Length - 1
                If i < cams.Count Then
                    _cameraButtons(i).Tag = cams(i).Id
                    _cameraButtons(i).Text = cams(i).Name
                    _cameraButtons(i).Enabled = True
                Else
                    _cameraButtons(i).Text = $"(unused {i + 1})"
                    _cameraButtons(i).Enabled = False
                End If
            Next
            Log($"Loaded {cams.Count} camera(s).")
            If cams.Count > _cameraButtons.Length Then
                Log($"Note: {cams.Count - _cameraButtons.Length} camera(s) beyond the first {_cameraButtons.Length} weren't shown.")
            End If
        Catch ex As Exception
            Log($"Failed to load cameras: {ex.Message}")
        End Try
    End Sub

    Private Sub CameraButton_Click(sender As Object, e As EventArgs)
        Dim btn = CType(sender, Button)
        Dim camId = TryCast(btn.Tag, String)
        If String.IsNullOrEmpty(camId) Then Return

        Dim status = _protectClient.SelectCamera(camId)
        HighlightSelected(btn)
        _lblSelected.Text = $"Selected: {status.SelectedCameraName} ({status.SelectedCameraId})"
        Log($"Selected camera '{status.SelectedCameraName}'")
        UpdatePresetLabels(camId)
    End Sub

    ' Relabels the preset buttons with this camera's actual preset names
    ' (falling back to "Preset N" for slots that don't have one). Different
    ' cameras can name the same slot number differently, so this runs every
    ' time the selection changes.
    Private Sub UpdatePresetLabels(cameraId As String)
        Dim cam = _protectClient.GetCachedCamera(cameraId)
        If cam Is Nothing Then Return

        For i As Integer = 1 To 10
            Dim preset = cam.Presets.Find(Function(p) p.Slot = i - 1)
            If preset IsNot Nothing AndAlso Not String.IsNullOrEmpty(preset.Name) Then
                _presetButtons(i - 1).Text = $"{i}: {preset.Name}"
            Else
                _presetButtons(i - 1).Text = $"Preset {i}"
            End If
        Next

        Dim homePreset = cam.Presets.Find(Function(p) p.Slot = -1)
        _homeButton.Text = If(homePreset IsNot Nothing AndAlso Not String.IsNullOrEmpty(homePreset.Name), $"Home: {homePreset.Name}", "Home")
    End Sub

    Private Async Sub PresetButton_Click(sender As Object, e As EventArgs)
        Dim btn = CType(sender, Button)
        Dim slot = CInt(btn.Tag)
        Log($"{btn.Text} (slot {slot}): sending...")
        Dim result = Await _protectClient.GotoPresetAsync(slot)
        Log(result.Message)
    End Sub

    ' --- Helpers ---

    Private Sub HighlightSelected(selected As Button)
        For Each b In _cameraButtons
            b.BackColor = SystemColors.Control
        Next
        selected.BackColor = Color.LightGreen
    End Sub

    Private Sub Log(msg As String)
        Dim line = $"[{DateTime.Now:T}] {msg}"
        If _logBox.InvokeRequired Then
            _logBox.Invoke(Sub() _logBox.AppendText(line & Environment.NewLine))
        Else
            _logBox.AppendText(line & Environment.NewLine)
        End If
    End Sub

End Class
