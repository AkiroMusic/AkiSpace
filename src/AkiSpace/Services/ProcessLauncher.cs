using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Services;

/// <summary>
/// Launches processes inside the child session with elevation using Windows
/// Task Scheduler COM automation (dynamic dispatch via ProgID "Schedule.Service") —
/// the BetterGI-proven technique. Creates a temporary scheduled task, runs it
/// with TASK_RUN_USE_SESSION_ID targeting the child session, then deletes the task.
/// </summary>
public sealed class ProcessLauncher
{
    // Task Scheduler 2.0 constants
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelHighest = 1;
    private const int TaskActionExec = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskRunUseSessionId = 4;

    private readonly ILogger<ProcessLauncher> _logger;

    public ProcessLauncher(ILogger<ProcessLauncher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Launches <paramref name="exePath"/> inside the child session with admin rights.
    /// Returns true on success (task created, run requested, deleted).
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
            taskService.Connect(null, null, null, null);

            dynamic rootFolder = taskService.GetFolder("\\");
            dynamic taskDefinition = taskService.NewTask(0);

            // Principal: run as the interactive user with highest privileges
            taskDefinition.Principal.LogonType = TaskLogonInteractiveToken;
            taskDefinition.Principal.RunLevel = TaskRunLevelHighest;

            // Action: exec the target program
            dynamic action = taskDefinition.Actions.Create(TaskActionExec);
            action.Path = exePath;
            if (!string.IsNullOrWhiteSpace(arguments))
                action.Arguments = arguments;

            // Register (interactive token: no password needed)
            dynamic registeredTask = rootFolder.RegisterTaskDefinition(
                taskName, taskDefinition,
                TaskCreateOrUpdate, null, null,
                TaskLogonInteractiveToken, "");

            // Run in the child session
            registeredTask.RunEx(null, TaskRunUseSessionId, checked((int)sessionId), null);

            _logger.LogInformation("Launched {Exe} in child session {SessionId} (task {Task})", exePath, sessionId, taskName);

            // Cleanup: delete the temp task
            rootFolder.DeleteTask(taskName, 0);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch {Exe} in child session {SessionId}", exePath, sessionId);
            try
            {
                dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
                svc.Connect(null, null, null, null);
                svc.GetFolder("\\").DeleteTask(taskName, 0);
            }
            catch { /* ignore cleanup failure */ }
            return false;
        }
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