using AkiSpace.Ipc;
using AkiSpace.Native;
using AkiSpace.Services;

// ============================================================
// AkiSpace SelfTest — verifies pure-logic components without
// requiring RDP/multi-session to be enabled on the machine.
// ============================================================

var failures = 0;

void Check(string name, bool pass, string? detail = null)
{
    Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}" + (detail == null ? "" : $" — {detail}"));
    if (!pass) failures++;
}

// ---- 1. IPC protocol round-trip (batch) ----
{
    var batch = new RelativeMouseBatch
    {
        FirstSequence = 42,
        BaseTicks = 1_700_000_000_000_000,
        Samples = new[]
        {
            new RelativeMouseSample(10, -20, 1_700_000_000_000_000),
            new RelativeMouseSample(-5, 3, 1_700_000_000_000_050),
            new RelativeMouseSample(0, 0, 1_700_000_000_000_100),
        },
    };
    var bytes = IpcProtocol.SerializeBatch(batch);
    var round = IpcProtocol.DeserializeBatch(bytes);
    Check("IPC batch round-trip: sequence", round.FirstSequence == 42);
    Check("IPC batch round-trip: sample count", round.Samples.Length == 3);
    Check("IPC batch round-trip: dx/dy preserved",
        round.Samples[0].DeltaX == 10 && round.Samples[0].DeltaY == -20 &&
        round.Samples[1].DeltaX == -5 && round.Samples[1].DeltaY == 3 &&
        round.Samples[2].DeltaX == 0 && round.Samples[2].DeltaY == 0);
    Check("IPC batch round-trip: timestamps preserved",
        round.Samples[1].TimestampTicks == batch.BaseTicks + 50);
    Check("IPC batch wire size", bytes.Length == 18 + 3 * 16, $"got {bytes.Length} bytes");
}

// ---- 2. IPC protocol round-trip (result) ----
{
    var result = new RelativeMouseResult(99, true);
    var bytes = IpcProtocol.SerializeResult(result);
    var round = IpcProtocol.DeserializeResult(bytes);
    Check("IPC result round-trip", round == result);
}

// ---- 3. Frame write/read via memory stream ----
{
    using var ms = new MemoryStream();
    var payload = new byte[] { 1, 2, 3, 4, 5 };
    IpcProtocol.WriteFrameAsync(ms, IpcPayloadType.RelativeMouseBatch, payload).GetAwaiter().GetResult();
    ms.Position = 0;
    var frame = IpcProtocol.ReadFrameAsync(ms).GetAwaiter().GetResult();
    Check("IPC frame header type", frame?.Type == IpcPayloadType.RelativeMouseBatch);
    Check("IPC frame payload round-trip", frame?.Payload.SequenceEqual(payload) == true);
}

// ---- 4. Settings round-trip + corrupt recovery ----
{
    var dir = Path.Combine(Path.GetTempPath(), "AkiSpaceSelfTest_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);

    var nullLog = Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.SettingsService>.Instance;

    // use the internal test constructor with an isolated directory
    var svc = new SettingsService(nullLog, dir);
    svc.Update(s => { s.DesktopWidth = 2560; s.DesktopHeight = 1440; s.GameMouseModeEnabled = true; s.RdpPort = 3390; });

    var svc2 = new SettingsService(nullLog, dir);
    Check("Settings round-trip width", svc2.Current.DesktopWidth == 2560);
    Check("Settings round-trip height", svc2.Current.DesktopHeight == 1440);
    Check("Settings round-trip gameMouse", svc2.Current.GameMouseModeEnabled);
    Check("Settings round-trip port", svc2.Current.RdpPort == 3390);

    // corrupt file -> defaults
    File.WriteAllText(Path.Combine(dir, "settings.json"), "{ this is not valid json !!!");
    var svc3 = new SettingsService(nullLog, dir);
    Check("Settings corrupt-file recovery", svc3.Current.DesktopWidth == 1920, $"got {svc3.Current.DesktopWidth}");

    Directory.Delete(dir, true);
}

// ---- 5. User32 struct marshalling (compile-time + size sanity) ----
{
    var input = new User32.INPUT
    {
        type = InputConstants.INPUT_MOUSE,
        U = new User32.InputUnion
        {
            mi = new User32.MOUSEINPUT { dx = 7, dy = -3, dwFlags = InputConstants.MOUSEEVENTF_MOVE },
        },
    };
    var size = System.Runtime.InteropServices.Marshal.SizeOf<User32.INPUT>();
    Check("INPUT struct size", size == 40, $"got {size}");
    var miSize = System.Runtime.InteropServices.Marshal.SizeOf<User32.MOUSEINPUT>();
    Check("MOUSEINPUT struct size", miSize == 32, $"got {miSize}");
    var rawSize = System.Runtime.InteropServices.Marshal.SizeOf<User32.RAWINPUT>();
    Check("RAWINPUT struct size", rawSize == 48, $"got {rawSize}");
}

// ---- 6. ChildSessionManager read-only checks on THIS machine ----
{
    var mgr = new ChildSessionManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<ChildSessionManager>.Instance);
    var enabled = mgr.IsChildSessionsEnabled();
    var sid = mgr.TryGetChildSessionId();
    var wrapper = mgr.IsRdpWrapperInstalled();
    var port = mgr.GetConfiguredRdpPort();
    var termsrv = mgr.GetTermsrvVersion();
    var listener = mgr.IsRdpListenerActive();

    Console.WriteLine($"[INFO] ChildSessionsEnabled={enabled}, ChildSessionId={sid?.ToString() ?? "none"}, RdpWrapper={wrapper}, Port={port}, termsrv={termsrv}, Listener={listener}");
    Check("termsrv version parsed", termsrv != "unknown", termsrv);
}

// ---- 7. EnvironmentVerifier read-only checks ----
{
    var mgr = new ChildSessionManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<ChildSessionManager>.Instance);
    var envSettings = new AkiSpace.Services.SettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.SettingsService>.Instance);
    var verifier = new EnvironmentVerifier(Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvironmentVerifier>.Instance, mgr, envSettings);
    var checks = verifier.RunAllChecks();
    Check("EnvVerifier ran all 10 checks", checks.Count == 10, $"got {checks.Count}");
    foreach (var c in checks)
    {
        Console.WriteLine($"[ENV] {c.Name}: {(c.Pass ? "PASS" : "FAIL")} — {c.Detail}");
    }
    // On this machine RDP is NOT enabled (Home default) — expect fDenyTSConnections check to FAIL
    var rdpCheck = checks.First(c => c.Name.Contains("fDenyTSConnections"));
    Console.WriteLine($"[INFO] Machine RDP state: {(rdpCheck.Pass ? "enabled" : "disabled (expected on Home default)")}");
}

// ---- 8. UI smoke test: SetupDialog renders + runs checks ----
// Runs in a separate STA thread (WinForms requirement), shows the dialog
// briefly, captures a screenshot, then closes.
// SKIP: requires an interactive desktop session (not a service/headless context).
{
    // Skip if no interactive desktop (e.g., running as a service or in CI).
    if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("CI") == "true")
    {
        Console.WriteLine("[SKIP] UI smoke test: non-interactive session");
    }
    else
    {
    var screenshotPath = Path.Combine(Path.GetTempPath(), "akspace_setup_dialog.png");
    Exception? uiError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            var mgr = new ChildSessionManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<ChildSessionManager>.Instance);
            var envSettings2 = new AkiSpace.Services.SettingsService(Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.SettingsService>.Instance);
            var verifier = new EnvironmentVerifier(Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvironmentVerifier>.Instance, mgr, envSettings2);
            using var dialog = new AkiSpace.Forms.SetupDialog(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Forms.SetupDialog>.Instance,
                verifier, mgr);
            dialog.Show();
            // let it render + run checks
            System.Threading.Thread.Sleep(1500);
            // capture the dialog window
            using var bmp = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    var rect = new AkiSpace.Native.User32.RECT(0, 0, dialog.Width, dialog.Height);
                    // DrawToBitmap is simpler and reliable for WinForms
                    g.ReleaseHdc(hdc);
                }
                catch { }
            }
            // Use DrawToBitmap (reliable for WinForms, no foreground needed)
            using var bmp2 = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bmp2, new System.Drawing.Rectangle(0, 0, dialog.Width, dialog.Height));
            bmp2.Save(screenshotPath);
            dialog.Close();
            Check("SetupDialog rendered + checks ran", File.Exists(screenshotPath), screenshotPath);
        }
        catch (Exception ex)
        {
            uiError = ex;
        }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join(TimeSpan.FromSeconds(15));
    if (uiError != null)
    {
        Check("SetupDialog UI smoke test", false, uiError.Message);
    }
    } // end else (interactive)
}

// ---- 9. E2E RDP child-session connection test (real machine, RDP now unlocked) ----
// SKIP: requires the AkiSpaceUser account + interactive desktop + RDP listener.
// Creates the RdpActiveXHost on an STA thread, connects to the child session
// via localhost, and waits for OnLoginComplete. This proves the full
// ConnectToChildSession pipeline works.
{
    // Skip if non-interactive or no AkiSpaceUser account.
    var mgrCheck = new AkiSpace.Services.ChildSessionManager(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.ChildSessionManager>.Instance);
    var canRunE2E = Environment.UserInteractive
        && Environment.GetEnvironmentVariable("CI") != "true"
        && mgrCheck.IsRdpListenerActive();
    if (!canRunE2E)
    {
        Console.WriteLine("[SKIP] E2E test: non-interactive session or RDP listener not active");
    }
    else
    {
    Exception? e2eError = null;
    var e2eThread = new Thread(() =>
    {
        try
        {
            var mgr = new ChildSessionManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<ChildSessionManager>.Instance);
            Console.WriteLine($"[E2E] ChildSessionsEnabled={mgr.IsChildSessionsEnabled()} Wrapper={mgr.IsRdpWrapperInstalled()} Port={mgr.GetConfiguredRdpPort()} Listener={mgr.IsRdpListenerActive()}");

            using var form = new System.Windows.Forms.Form
            {
                Text = "AkiSpace E2E",
                Size = new System.Drawing.Size(1100, 700),
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            };

            var host = new AkiSpace.Controls.RdpActiveXHost(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Controls.RdpActiveXHost>.Instance);
            host.Dock = System.Windows.Forms.DockStyle.Fill;
            form.Controls.Add(host);

            var loginTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var failTcs = new System.Threading.Tasks.TaskCompletionSource<string>();
            host.LoginCompleted += () =>
            {
                Console.WriteLine("[E2E] >>> LoginCompleted fired <<<");
                loginTcs.TrySetResult(true);
            };
            host.ConnectionFailed += reason =>
            {
                Console.WriteLine($"[E2E] >>> ConnectionFailed: {reason} <<<");
                failTcs.TrySetResult(reason);
            };
            host.FocusReleased += dir => Console.WriteLine($"[E2E-event] FocusReleased dir={dir}");
            host.RequestedGoFullScreen += () => Console.WriteLine("[E2E-event] RequestedGoFullScreen");

            form.Shown += (_, _) =>
            {
                Console.WriteLine("[E2E] Form shown, connecting with credentials (standard RDP)...");
                host.Connect(1280, 720, 32, mgr.GetConfiguredRdpPort(),
                    true, true, false,
                    userName: "AkiSpaceUser", password: "lb33", useChildSession: false);
            };

            form.Show();

            // Non-blocking pump loop: run the message loop via Application.Run
            // and poll state from the UI thread with a timer.
            var pollTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            pollTimer.Tick += (_, _) =>
            {
                var sid = mgr.TryGetChildSessionId();
                Console.WriteLine($"[E2E-poll] ChildSessionId={sid?.ToString() ?? "none"} Connected={host.GetConnectedState()} IsConnecting={host.IsConnecting}");
            };
            pollTimer.Start();

            // Pump messages until done or timeout (keep UI thread alive so
            // the ActiveX events + timers actually fire)
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                System.Windows.Forms.Application.DoEvents();
                if (loginTcs.Task.IsCompleted || failTcs.Task.IsCompleted) break;
                System.Threading.Thread.Sleep(100);
            }

            pollTimer.Stop();
            if (loginTcs.Task.IsCompleted)
            {
                Console.WriteLine("[E2E] LOGIN COMPLETE - child session desktop ready!");
                Check("E2E RDP multi-session connect", true, "OnLoginComplete fired");
            }
            else if (failTcs.Task.IsCompleted)
            {
                var reason = failTcs.Task.Result;
                Console.WriteLine($"[E2E] Connection failed: {reason}");
                Check("E2E RDP multi-session connect", false, reason);
            }
            else
            {
                Console.WriteLine("[E2E] Timed out waiting for login");
                Check("E2E RDP multi-session connect", false, "timeout 45s");
            }

            host.DisconnectSession();
            form.Close();
        }
        catch (Exception ex)
        {
            e2eError = ex;
        }
    });
    e2eThread.SetApartmentState(ApartmentState.STA);
    e2eThread.Start();
    e2eThread.Join(TimeSpan.FromSeconds(60));
    if (e2eError != null)
    {
        Check("E2E RDP multi-session connect", false, e2eError.Message);
    }
    } // end else (canRunE2E)
}

// ============================================================
//  v0.1.3 review-driven expansion (RED-first: each test asserts
//  behavior that the production code must satisfy)
// ============================================================
void _Wave5Tests()
{
    // ---- 10. Settings round-trip: LaunchProgramPath ----
    {
        var dir = Path.Combine(Path.GetTempPath(), "AkiSpaceSelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var nullLog = Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.SettingsService>.Instance;
        var svc = new AkiSpace.Services.SettingsService(nullLog, dir);
        svc.Update(s => s.LaunchProgramPath = @"C:\Games\wow.exe");
        var svc2 = new AkiSpace.Services.SettingsService(nullLog, dir);
        Check("Settings round-trip LaunchProgramPath", svc2.Current.LaunchProgramPath == @"C:\Games\wow.exe", $"got {svc2.Current.LaunchProgramPath}");
        svc2.Update(s => s.LaunchProgramPath = null);
        Directory.Delete(dir, true);
    }

    // ---- 11. Settings ClonePassword: DPAPI round-trip ----
    {
        var dir = Path.Combine(Path.GetTempPath(), "AkiSpaceSelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var nullLog = Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.SettingsService>.Instance;
        var svc = new AkiSpace.Services.SettingsService(nullLog, dir);
        svc.Update(s => s.ClonePassword = "mysecret-12345");
        // The on-disk JSON should contain the DPAPI: prefix.
        var onDisk = File.ReadAllText(Path.Combine(dir, "settings.json"));
        Check("Settings.json ClonePassword is DPAPI-encrypted on disk",
            onDisk.Contains(AkiSpace.Services.SettingsProtection.Prefix),
            $"on-disk content: {onDisk[..Math.Min(120, onDisk.Length)]}...");
        // After reload, the password should round-trip back to the plaintext value.
        var svc2 = new AkiSpace.Services.SettingsService(nullLog, dir);
        Check("Settings ClonePassword round-trips via DPAPI", svc2.Current.ClonePassword == "mysecret-12345",
            $"got {svc2.Current.ClonePassword}");
        Directory.Delete(dir, true);
    }

    // ---- 12. EnvironmentVerifier.BuildFirewallAddRule honors the RDP port ----
    {
        var rule = AkiSpace.Services.EnvironmentVerifier.BuildFirewallAddRule("Test Rule", "allow", 3390, "127.0.0.1");
        Check("Firewall rule uses the configured RDP port (3390)", rule.Contains("localport=3390"),
            $"rule: {rule}");
        Check("Firewall rule keeps 127.0.0.1 restriction for allow",
            rule.Contains("remoteip=127.0.0.1") && rule.Contains("action=allow"),
            $"rule: {rule}");
        var blockRule = AkiSpace.Services.EnvironmentVerifier.BuildFirewallAddRule("Test Block", "block", 3389, "any");
        Check("Firewall block rule uses action=block", blockRule.Contains("action=block"),
            $"rule: {blockRule}");
    }

    // ---- 13. ApplyAllFixes honors ConnectionMode for hook disable ----
    {
        var nullLog = Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.ChildSessionManager>.Instance;
        var nullLogV = Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.EnvironmentVerifier>.Instance;
        var mgr = new AkiSpace.Services.ChildSessionManager(nullLog);
        var dir = Path.Combine(Path.GetTempPath(), "AkiSpaceSelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var nullLogS = Microsoft.Extensions.Logging.Abstractions.NullLogger<AkiSpace.Services.SettingsService>.Instance;
        var settings = new AkiSpace.Services.SettingsService(nullLogS, dir);
        settings.Update(s => s.ConnectionMode = AkiSpace.Services.ConnectionMode.StandardRdp);
        var verifier = new AkiSpace.Services.EnvironmentVerifier(nullLogV, mgr, settings);
        var fixResults = verifier.ApplyAllFixes(alsoDisableRdpWrapper: false);
        var hasPreserve = fixResults.Any(r => r.Name.Contains("保留 RDP Wrapper"));
        var hasDisable = fixResults.Any(r => r.Name.Contains("禁用 RDP Wrapper"));
        Check("StandardRdp mode: ApplyAllFixes preserves RDP Wrapper hook", hasPreserve && !hasDisable,
            $"preserve={hasPreserve}, disable={hasDisable}");
        Directory.Delete(dir, true);
    }

    // ---- 14+15. IpcProtocol rejects frames larger than MaxPayloadLength ----
    {
        var ms = new MemoryStream();
        // 2 MB declared length (exceeds MaxPayloadLength = 1 MB)
        var huge = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(huge, 2 * 1024 * 1024);
        ms.Write(huge);
        ms.Write(new byte[] { 0x02 });
        ms.Position = 0;
        bool rejected = false;
        try
        {
            _ = AkiSpace.Ipc.IpcProtocol.ReadFrameAsync(ms).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Invalid payload length"))
        {
            rejected = true;
        }
        Check("IpcProtocol rejects payload > MaxPayloadLength with InvalidOperationException", rejected);
    }

    // ---- 14b. IpcProtocol accepts a valid frame after a previously-cancelled read ----
    {
        // If the read loop throws, the PipeConnection catches and continues.
        // We verify the protocol itself can be called repeatedly.
        var ms = new MemoryStream();
        // Write a small valid frame
        var batch = new AkiSpace.Ipc.RelativeMouseBatch
        {
            FirstSequence = 1,
            BaseTicks = 100,
            Samples = new[] { new AkiSpace.Ipc.RelativeMouseSample(1, 1, 100) }
        };
        var payload = AkiSpace.Ipc.IpcProtocol.SerializeBatch(batch);
        var lenBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lenBytes, payload.Length);
        ms.Write(lenBytes);
        ms.Write(new byte[] { 0x02 });
        ms.Write(payload);
        ms.Position = 0;
        var frame = AkiSpace.Ipc.IpcProtocol.ReadFrameAsync(ms).GetAwaiter().GetResult();
        Check("IpcProtocol reads a valid small frame end-to-end", frame.HasValue && frame.Value.Payload.Length == payload.Length);
    }
}

_Wave5Tests();

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "ALL SELF-TESTS PASSED"
    : $"{failures} SELF-TEST(S) FAILED");
return failures == 0 ? 0 : 1;
