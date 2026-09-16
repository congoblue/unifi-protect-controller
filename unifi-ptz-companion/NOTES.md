# UniFi PTZ Companion — Project Notes

Context for picking this project back up (in Claude Code or otherwise)
without re-deriving decisions made along the way.

## What this is

A VB.NET WinForms app that controls a UniFi Protect PTZ camera, built
originally as a relay so Bitfocus Companion could drive presets over
HTTP. It grew into a standalone control panel: the form has its own
camera-select / preset / PTZ buttons, and the HTTP endpoints for
Companion still run in the background alongside it.

## Why it's built this way

- **Target framework is net5.0-windows, not net8/net6.** The user is on
  Visual Studio 2019, which tops out at .NET 5. This ruled out
  ASP.NET Core's minimal API (`WebApplication`, `MapGet`), which needs
  .NET 6+.
- **The background web server is a raw `HttpListener` loop, not ASP.NET
  Core.** Direct consequence of the .NET 5 constraint above.
  `HttpListener` lives in the base runtime (`System.Net`), so it isn't
  tied to any ASP.NET Core version. Routes are parsed manually by
  splitting the URL path in `Program.vb` — see `HandleRequestAsync`.
- **Auth is a local (non-SSO) UniFi account, not unifi.ui.com.** UI.com
  cloud login goes through a different flow entirely and enforces MFA,
  which a script can't satisfy. The app logs in directly against the
  console's local API (`POST /api/auth/login`), which only accepts
  genuinely local accounts. This already tripped the user up twice:
  once because an account looked local but wasn't, and once because
  `config.json` had `http://` instead of `https://` for `NvrAddress`
  (silent 401, not a connection error).
- **VB doesn't allow `Await` inside a `Catch` block** (unlike C#). Where
  this came up (`HandleRequestAsync`), the pattern is: record the error
  in the `Catch`, then `Await` the response-writing after the
  `Try/Catch` has exited.
- **Fire-and-forget `Task.Run(...)` calls are assigned to a variable**
  (e.g. `Dim listenLoopTask = Task.Run(...)`) purely to stop the VB
  compiler warning about an un-awaited call — the tasks are genuinely
  meant to run in the background, this isn't a bug.

## Confirmed vs. placeholder API endpoints

- **Confirmed and working:** camera list (`GET
  /proxy/protect/api/cameras`), preset list per camera (`GET
  /proxy/protect/api/cameras/{id}/ptz/preset`, includes preset
  names), and moving to a preset (`POST
  /proxy/protect/api/cameras/{id}/ptz/goto/{slot}`, slot `-1` = home).
  Sourced from the `unifi-ptz-better-patrol` open-source project, which
  reverse-engineers these specifically.
- **Continuous pan/tilt/zoom is not exposed by the API and has been
  removed from the app.** The UI previously had a "PTZ Control" frame
  with Up/Down/Left/Right/Zoom+/Zoom- buttons backed by a guessed
  `/proxy/protect/api/cameras/{id}/ptz/move` endpoint, but no public
  reverse-engineering of continuous move over Protect's API was ever
  found, and it isn't possible with the current API. The frame, its
  buttons, and `MovePtzAsync`/`StopPtzAsync` in `ProtectClient.vb` were
  deleted rather than kept as dead placeholder code.

## Project structure

- `UnifiPtzCompanion.vbproj` — plain `Microsoft.NET.Sdk`,
  `net5.0-windows`, `UseWindowsForms=true`, `OutputType=Exe` (kept as
  `Exe` rather than `WinExe` deliberately, so a console window stays
  visible for `Console.WriteLine` log output alongside the form).
- `Program.vb` — entry point. Starts the `HttpListener` server in the
  background, then hands the main thread to the WinForms message loop.
- `MainForm.vb` — the UI: 32 camera buttons (4x8 grid, populated from
  the real camera list on load), 10 preset buttons + a bonus Home
  button (relabeled with each camera's actual preset names on
  selection), and a log panel. (No PTZ D-pad/zoom controls — continuous
  move isn't possible with the current API, see above.)
- `ProtectClient.vb` — all UniFi Protect communication: login/session
  handling (cookie + CSRF token + re-auth on 401/403), camera/preset
  discovery (cached 30s), selection state, and goto-preset.
- `Models.vb` — plain data classes (`AppConfig`, `CameraInfo`,
  `PresetInfo`, `GotoResult`, `SelectionStatus`) plus `AppConfig`'s
  first-run `config.json` creation.

## Configuration

`config.json` is created automatically next to the built `.exe` on
first run (app then exits so it can be edited) — not checked into
source. Fields: `NvrAddress` (must be `https://`), `Username`,
`Password` (a local account, not a UI.com email), `ListenPort`
(default 5000).

## Still outstanding

- Companion-side setup (connecting Companion's Generic HTTP module to
  this app's endpoints) was covered earlier in the conversation this
  file summarizes, but isn't re-documented in detail here.
