Imports System.Drawing
Imports System.Windows.Forms

Public Class MainForm
    Inherits Form

    Private ReadOnly _protectClient As ProtectClient
    Private ReadOnly _cameraButtons(15) As Button
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

        Dim grpPtz = BuildPtzGroup()
        grpPtz.Location = New Point(12, 292)
        Me.Controls.Add(grpPtz)

        _logBox = New TextBox() With {
            .Multiline = True,
            .ReadOnly = True,
            .ScrollBars = ScrollBars.Vertical,
            .Location = New Point(12, 579),
            .Size = New Size(974, 140),
            .Font = New Font("Consolas", 8.5F)
        }
        Me.Controls.Add(_logBox)

        AddHandler Me.Load, AddressOf MainForm_Load
    End Sub

    ' --- Layout builders ---

    Private Function BuildCameraGroup() As GroupBox
        Dim grp As New GroupBox() With {.Text = "Cameras", .Size = New Size(500, 240)}
        Const btnW As Integer = 110, btnH As Integer = 40, gap As Integer = 8
        For i As Integer = 0 To 15
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
                .Tag = i
            }
            AddHandler btn.Click, AddressOf PresetButton_Click
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
        grp.Controls.Add(homeBtn)

        Return grp
    End Function

    Private Function BuildPtzGroup() As GroupBox
        Dim grp As New GroupBox() With {.Text = "PTZ Control", .Size = New Size(500, 240)}
        Const cell As Integer = 70, cellH As Integer = 40, gap As Integer = 8

        Dim btnUp = MakeDirectionButton("Up", 10 + (cell + gap), 25, cell, cellH)
        Dim btnLeft = MakeDirectionButton("Left", 10, 25 + (cellH + gap), cell, cellH)
        Dim btnRight = MakeDirectionButton("Right", 10 + 2 * (cell + gap), 25 + (cellH + gap), cell, cellH)
        Dim btnDown = MakeDirectionButton("Down", 10 + (cell + gap), 25 + 2 * (cellH + gap), cell, cellH)

        grp.Controls.Add(btnUp)
        grp.Controls.Add(btnLeft)
        grp.Controls.Add(btnRight)
        grp.Controls.Add(btnDown)

        Dim btnZoomIn As New Button() With {
            .Text = "Zoom +",
            .Location = New Point(260, 25),
            .Size = New Size(150, 40),
            .Tag = "zoomin"
        }
        Dim btnZoomOut As New Button() With {
            .Text = "Zoom -",
            .Location = New Point(260, 73),
            .Size = New Size(150, 40),
            .Tag = "zoomout"
        }
        AddHandler btnZoomIn.MouseDown, AddressOf DirectionButton_MouseDown
        AddHandler btnZoomIn.MouseUp, AddressOf DirectionButton_MouseUp
        AddHandler btnZoomOut.MouseDown, AddressOf DirectionButton_MouseDown
        AddHandler btnZoomOut.MouseUp, AddressOf DirectionButton_MouseUp
        grp.Controls.Add(btnZoomIn)
        grp.Controls.Add(btnZoomOut)

        Dim note As New Label() With {
            .Text = "Direction/zoom use a placeholder endpoint -- see ProtectClient.vb",
            .Location = New Point(10, 130),
            .Size = New Size(460, 60),
            .ForeColor = Color.DarkRed
        }
        grp.Controls.Add(note)

        Return grp
    End Function

    Private Function MakeDirectionButton(direction As String, x As Integer, y As Integer, w As Integer, h As Integer) As Button
        Dim btn As New Button() With {
            .Text = direction,
            .Location = New Point(x, y),
            .Size = New Size(w, h),
            .Tag = direction.ToLowerInvariant()
        }
        AddHandler btn.MouseDown, AddressOf DirectionButton_MouseDown
        AddHandler btn.MouseUp, AddressOf DirectionButton_MouseUp
        Return btn
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
            If cams.Count > 16 Then
                Log($"Note: {cams.Count - 16} camera(s) beyond the first 16 weren't shown.")
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
    End Sub

    Private Async Sub PresetButton_Click(sender As Object, e As EventArgs)
        Dim btn = CType(sender, Button)
        Dim slot = CInt(btn.Tag)
        Log($"Preset {slot}: sending...")
        Dim result = Await _protectClient.GotoPresetAsync(slot)
        Log(result.Message)
    End Sub

    Private Async Sub DirectionButton_MouseDown(sender As Object, e As MouseEventArgs)
        Dim btn = CType(sender, Button)
        Dim direction = CStr(btn.Tag)
        Log($"{direction}: start")
        Dim result = Await _protectClient.MovePtzAsync(direction)
        Log(result.Message)
    End Sub

    Private Async Sub DirectionButton_MouseUp(sender As Object, e As MouseEventArgs)
        Log("stop")
        Dim result = Await _protectClient.StopPtzAsync()
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
