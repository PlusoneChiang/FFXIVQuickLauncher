using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Serilog;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Unix.Compatibility;

namespace XIVLauncher.Common.Unix;

public class UnixDalamudRunner : IDalamudRunner
{
    private readonly CompatibilityTools compatibility;
    private readonly DirectoryInfo dotnetRuntime;

    public UnixDalamudRunner(CompatibilityTools compatibility, DirectoryInfo dotnetRuntime)
    {
        this.compatibility = compatibility;
        this.dotnetRuntime = dotnetRuntime;
    }

    private const int DEFAULT_INJECTION_DELAY_MS = 5000;

    public Process? Run(FileInfo runner, bool fakeLogin, bool noPlugins, bool noThirdPlugins, FileInfo gameExe, string gameArgs, IDictionary<string, string> environment, DalamudLoadMethod loadMethod, DalamudStartInfo startInfo)
    {
        var gameExePath = "";
        var dotnetRuntimePath = "";

        Parallel.Invoke(
            () => { gameExePath = compatibility.UnixToWinePath(gameExe.FullName); },
            () => { dotnetRuntimePath = compatibility.UnixToWinePath(dotnetRuntime.FullName); },
            () => { startInfo.LoggingPath = compatibility.UnixToWinePath(startInfo.LoggingPath); },
            () => { startInfo.WorkingDirectory = compatibility.UnixToWinePath(startInfo.WorkingDirectory); },
            () => { startInfo.ConfigurationPath = compatibility.UnixToWinePath(startInfo.ConfigurationPath); },
            () => { startInfo.PluginDirectory = compatibility.UnixToWinePath(startInfo.PluginDirectory); },
            () => { startInfo.AssetDirectory = compatibility.UnixToWinePath(startInfo.AssetDirectory); }
        );

        environment.Add("DALAMUD_RUNTIME", dotnetRuntimePath);
        environment.Add("DOTNET_ROOT", dotnetRuntimePath);
        Log.Information("[DALAMUD] Using DOTNET_ROOT: {DotnetRoot}", dotnetRuntimePath);

        // 如果是 ACLonly 模式，不需要注入 Dalamud，直接啟動遊戲
        if (loadMethod == DalamudLoadMethod.ACLonly)
        {
            return RunGameWithoutDalamud(gameExe, gameArgs, environment);
        }

        if (OperatingSystem.IsMacOS())
            environment["DOTNET_EnableWriteXorExecute"] = "0";

        // 步驟 1: 先啟動遊戲
        Log.Information("[DALAMUD] Starting game first, then inject Dalamud...");
        var gameCommand = $"\"{gameExePath}\" {gameArgs}";
        var gameProcess = compatibility.RunInPrefix(gameCommand, environment: environment, redirectOutput: false, writeLog: true);

        if (gameProcess == null)
        {
            throw new DalamudRunnerException("Failed to start game process");
        }

        // 獲取 Wine PID，使用重試機制
        int winePid = 0;
        const int maxRetries = 10;
        const int retryDelayMs = 500;

        for (int i = 0; i < maxRetries; i++)
        {
            Thread.Sleep(retryDelayMs);
            var winePids = compatibility.GetProcessIds("ffxiv_dx11.exe");

            if (winePids != null && winePids.Length > 0)
            {
                // 取最後一個（最新的）進程
                winePid = winePids[winePids.Length - 1];
                Log.Information("[DALAMUD] Found {Count} ffxiv_dx11.exe process(es), using Wine PID: {WinePid}", winePids.Length, winePid);
                break;
            }

            Log.Information("[DALAMUD] Waiting for game process... attempt {Attempt}/{MaxAttempts}", i + 1, maxRetries);
        }

        if (winePid == 0)
        {
            throw new DalamudRunnerException("Could not find game process after starting (timeout)");
        }

        Log.Information("[DALAMUD] Game started with Wine PID: {WinePid}", winePid);

        // 步驟 2: 等待延時（預設 5 秒，或從設定讀取）
        var delayMs = startInfo.DelayInitializeMs > 0 ? startInfo.DelayInitializeMs : DEFAULT_INJECTION_DELAY_MS;
        Log.Information("[DALAMUD] Waiting {DelayMs}ms before injection...", delayMs);
        Thread.Sleep(delayMs);

        // 步驟 3: 使用 inject 注入到遊戲進程
        // 根據參考專案，inject 命令格式為: Dalamud.Injector.exe inject [pid] [options...]
        // PID 直接作為數字參數傳遞，不是 --pid=
        var tsPackData = startInfo.TroubleshootingPackData ?? "";
        var tsPackB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(tsPackData));

        var injectArguments = new List<string>
        {
            $"\"{runner.FullName}\"",
            DalamudInjectorArgs.INJECT,
            winePid.ToString(),  // PID 直接作為數字參數
            DalamudInjectorArgs.WorkingDirectory(startInfo.WorkingDirectory),
            DalamudInjectorArgs.ConfigurationPath(startInfo.ConfigurationPath),
            DalamudInjectorArgs.LoggingPath(startInfo.LoggingPath),
            DalamudInjectorArgs.PluginDirectory(startInfo.PluginDirectory),
            DalamudInjectorArgs.AssetDirectory(startInfo.AssetDirectory),
            DalamudInjectorArgs.ClientLanguage((int)startInfo.Language),
            DalamudInjectorArgs.DelayInitialize(0), // 已經延時過了，不需要再延時
            DalamudInjectorArgs.TsPackB64(tsPackB64),
        };

        if (fakeLogin)
            injectArguments.Add(DalamudInjectorArgs.FAKE_ARGUMENTS);

        if (noPlugins)
            injectArguments.Add(DalamudInjectorArgs.NO_PLUGIN);

        if (noThirdPlugins)
            injectArguments.Add(DalamudInjectorArgs.NO_THIRD_PARTY);

        Log.Information("[DALAMUD] Inject arguments: {Args}", string.Join(" ", injectArguments));
        Log.Information("[DALAMUD] Target Wine PID: {WinePid}", winePid);

        var dalamudProcess = compatibility.RunInPrefix(string.Join(" ", injectArguments), environment: environment, redirectOutput: true, writeLog: true);

        // 等待注入完成並讀取輸出
        DalamudConsoleOutput? dalamudConsoleOutput = null;
        var timeout = DateTime.Now.AddSeconds(30);

        while (dalamudConsoleOutput == null && DateTime.Now < timeout)
        {
            var output = dalamudProcess.StandardOutput.ReadLine();
            if (output == null)
            {
                if (dalamudProcess.HasExited)
                {
                    Log.Warning("[DALAMUD] Injector process exited with code: {ExitCode}", dalamudProcess.ExitCode);
                    break;
                }
                continue;
            }
            Console.WriteLine(output);

            try
            {
                dalamudConsoleOutput = JsonSerializer.Deserialize(output, DalamudConsoleOutputJsonContext.Default.DalamudConsoleOutput);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Couldn't parse Dalamud output: {output}");
            }
        }

        // 持續讀取輸出
        new Thread(() =>
        {
            while (!dalamudProcess.StandardOutput.EndOfStream)
            {
                var output = dalamudProcess.StandardOutput.ReadLine();
                if (output != null)
                    Console.WriteLine(output);
            }
        }).Start();

        try
        {
            // 使用我們已知的 Wine PID 來獲取 Unix PID
            var unixPid = compatibility.GetUnixProcessId(winePid);

            if (unixPid == 0)
            {
                Log.Error("Could not retrieve Unix process ID for Wine PID {WinePid}", winePid);
                return null;
            }

            var returnProcess = Process.GetProcessById(unixPid);
            Log.Information("[DALAMUD] Injection complete. Game running with Unix PID {UnixPid}, Wine PID {WinePid}", unixPid, winePid);
            return returnProcess;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not retrieve game Process information");
            return null;
        }
    }

    private Process? RunGameWithoutDalamud(FileInfo gameExe, string gameArgs, IDictionary<string, string> environment)
    {
        var gameExePath = compatibility.UnixToWinePath(gameExe.FullName);
        var gameCommand = $"\"{gameExePath}\" {gameArgs}";

        Log.Information("[DALAMUD] ACLonly mode - starting game without Dalamud injection");
        var gameProcess = compatibility.RunInPrefix(gameCommand, environment: environment, redirectOutput: false, writeLog: true);

        if (gameProcess == null)
        {
            throw new DalamudRunnerException("Failed to start game process");
        }

        Thread.Sleep(1000);
        var winePids = compatibility.GetProcessIds("ffxiv_dx11.exe");
        if (winePids == null || winePids.Length == 0)
        {
            throw new DalamudRunnerException("Could not find game process after starting");
        }

        var winePid = winePids[0];
        var unixPid = compatibility.GetUnixProcessId(winePid);

        if (unixPid == 0)
        {
            Log.Error("Could not retrieve Unix process ID");
            return null;
        }

        return Process.GetProcessById(unixPid);
    }
}
