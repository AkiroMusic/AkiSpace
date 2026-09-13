using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Services;

/// <summary>
/// Launches processes inside the child session with elevation using Windows
/// Task Scheduler COM automation (dynamic dispatch via ProgID "Schedule.Service") —
/// the BetterGI-proven technique. Creates a temporary scheduled task, runs it
/// with TASK_RUN_USE_SESSION_ID targeting the child session, waits for the task
/// to reach the Running state, then deletes the task.
/// </summary>
public sealed class ProcessLauncher
{
    // Task Scheduler 2.0 constants
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskRunUseSessionId = 4;
    private const int TaskStateRunning = 4;

    private readonly ILogger<ProcessLauncher> _logger;

    public ProcessLauncher(ILogger<ProcessLauncher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Launches <paramref name="exePath"/> inside the child session with admin rights.
    /// Returns true on success (task created, run requested, task observed running).
    /// <para>
    /// <paramref name="arguments"/> is stored on the task's exec action verbatim; it is
    /// the CALLER's responsibility to quote embedded paths/args correctly. The shipped
    /// call sites pass well-formed input (a quoted file path, and a quoted GUID-named
    /// nonce file path), so no escaping is attempted here.
    /// </para>
    /// </summary>
    public bool LaunchInChildSession(string exePath, uint sessionId, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("exePath is required", nameof(exePath));
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Program not found: {exePath}");

        var taskName = $"AkiSpace_Launch_{Guid.NewGuid():N}";
        try
        {
            dynamic taskService = Activator.CreateInstance(
                Type.GetTypeFromProgID("Schedule.Service")!)!;
            dynamic? rootFolder = null;
            dynamic? registeredTask = null;
            try
            {
                taskService.Connect(null, null, null, null);

                rootFolder = taskService.GetFolder("\\");
                dynamic taskDefinition = taskService.NewTask(0);

                // Principal: run as the interactive user with highest privileges
                taskDefinition.Principal.LogonType = TaskLogonInteractiveToken;
                taskDefinition.Principal.RunLevel = TaskRunLevelHighest;

                // Action: exec the target program
                dynamic action = taskDefinition.Actions.Create(TaskActionExec);
                action.Path = exePath;
                if (!string.IsNullOrWhiteSpace(arguments))
                    action.Arguments = arguments;

                registeredTask = rootFolder.RegisterTaskDefinition(
                    taskName, taskDefinition,
                    TaskCreateOrUpdate, null, null,
                    TaskLogonInteractiveToken, "");

                // Run in the child session
                registeredTask.RunEx(null, TaskRunUseSessionId, checked((int)sessionId), null);

                // RunEx only REQUESTS the run. Deleting the task immediately can cancel
                // a launch that hasn't spawned yet, so wait until the task reports
                // Running before removing it. Bounded so a stuck task can't hang the UI.
                if (WaitForTaskRunning(registeredTask, TimeSpan.FromSeconds(2)))
                {
                    _logger.LogInformation("Launched {Exe} in child session {SessionId} (task {Task})",
                        exePath, sessionId, taskName);
                    return true;
                }

                _logger.LogWarning(
                    "Task {Task} did not reach Running within the wait window; launch may not have started",
                    taskName);
                return false;
            }
            finally
            {
                // Always clean up the temp task, even if RunEx threw.
                try
                {
                    rootFolder?.DeleteTask(taskName, 0);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to delete temp task {Task}", taskName);
                }
                ReleaseComObject(registeredTask);
                ReleaseComObject(rootFolder);
                ReleaseComObject(taskService);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch {Exe} in child session {SessionId}", exePath, sessionId);
            // The registration path may have failed after creating the task; sweep it
            // with a fresh connection so a failed launch doesn't leave debris.
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
                try
                {
                    svc.Connect(null, null, null, null);
                    dynamic folder = svc.GetFolder("\\");
                    try { folder.DeleteTask(taskName, 0); }
                    finally { ReleaseComObject(folder); }
                }
                finally
                {
                    ReleaseComObject(svc);
                }
            }
            catch { /* ignore cleanup failure */ }
            return false;
        }
    }

    /// <summary>Polls the task state until it reports Running or the deadline passes.</summary>
    private static bool WaitForTaskRunning(dynamic registeredTask, TimeSpan deadline)
    {
        var end = Environment.TickCount64 + (long)deadline.TotalMilliseconds;
        while (Environment.TickCount64 < end)
        {
            try
            {
                if ((int)registeredTask.State == TaskStateRunning) return true;
            }
            catch
            {
                // COM hiccup while polling; retry until the deadline.
            }
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>Best-effort deterministic RCW release; GC remains the fallback.</summary>
    private static void ReleaseComObject(object? rcw)
    {
        if (rcw is null || !Marshal.IsComObject(rcw)) return;
        try { Marshal.FinalReleaseComObject(rcw); }
        catch { /* already released */ }
    }

    /// <summary>
    /// Launches a program in the current session (useful for testing the launch path).
    /// </summary>
    public bool LaunchInCurrentSession(string exePath, string? arguments = null)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        return LaunchInChildSession(exePath, (uint)sessionId, arguments);
    }
}