using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace Pilewise.Services;

/// <summary>Starts Pilewise (hidden in the tray) when the user logs in.</summary>
public interface IAutoStart
{
    /// <summary>False where it isn't supported (e.g. the exe path is unknown).</summary>
    bool IsSupported { get; }

    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

/// <summary>
/// Login-item registration per OS, always launching with <see cref="MinimizedArgument"/>:
/// Windows — <c>HKCU\…\Run</c> value; macOS — a LaunchAgent plist in <c>~/Library/LaunchAgents</c>;
/// Linux — an XDG autostart <c>.desktop</c> file. The file contents are built by pure, tested helpers.
/// </summary>
public sealed class AutoStart : IAutoStart
{
    public const string MinimizedArgument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Pilewise";
    private const string LaunchAgentLabel = "com.aleksandrpidlozhevich.pilewise";

    private readonly ILogService _log;
    private readonly string? _executable;

    public AutoStart(ILogService log)
    {
        _log = log;
        _executable = ResolveExecutable();
    }

    public bool IsSupported => _executable != null &&
                               (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux());

    public bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return ReadRunValue() != null;
                var file = AgentFile();
                return file != null && File.Exists(file);
            }
            catch (Exception ex)
            {
                _log.WriteWarning($"Auto-start: couldn't read the login item: {ex.Message}");
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported) return;

        if (OperatingSystem.IsWindows())
        {
            WriteRunValue(enabled ? WindowsCommand(_executable!) : null);
        }
        else
        {
            var file = AgentFile()!;
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, OperatingSystem.IsMacOS()
                    ? LaunchAgentPlist(LaunchAgentLabel, _executable!)
                    : DesktopEntry(_executable!));
            }
            else if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        _log.WriteInfo($"Auto-start {(enabled ? "enabled" : "disabled")} ({_executable}).");
    }

    // ---- pure builders (tested) -------------------------------------------------------------

    /// <summary>The Run-key command: the quoted exe plus the minimized flag.</summary>
    public static string WindowsCommand(string executable) => $"\"{executable}\" {MinimizedArgument}";

    /// <summary>An XDG autostart entry (Exec quoted per the Desktop Entry spec).</summary>
    public static string DesktopEntry(string executable) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Pilewise\n" +
        "Comment=Sync your game library in the background\n" +
        $"Exec={QuoteExec(executable)} {MinimizedArgument}\n" +
        "Terminal=false\n" +
        "X-GNOME-Autostart-enabled=true\n";

    /// <summary>A launchd agent that runs the app once at login.</summary>
    public static string LaunchAgentPlist(string label, string executable) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
         <plist version="1.0">
         <dict>
             <key>Label</key>
             <string>{SecurityElement.Escape(label)}</string>
             <key>ProgramArguments</key>
             <array>
                 <string>{SecurityElement.Escape(executable)}</string>
                 <string>{MinimizedArgument}</string>
             </array>
             <key>RunAtLoad</key>
             <true/>
         </dict>
         </plist>

         """;

    // Desktop Entry spec: quote the argument, escaping " ` $ and \ inside the quotes.
    private static string QuoteExec(string value)
    {
        var escaped = new StringBuilder();
        foreach (var ch in value)
        {
            if (ch is '"' or '`' or '$' or '\\') escaped.Append('\\');
            escaped.Append(ch);
        }

        return $"\"{escaped}\"";
    }

    // ---- platform plumbing ------------------------------------------------------------------

    // The AppImage itself on Linux (the running binary sits in a temporary mount); otherwise the exe.
    // Velopack keeps the install path stable across updates (Windows: …\Pilewise\current\Pilewise.exe).
    private static string? ResolveExecutable()
    {
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (OperatingSystem.IsLinux() && !string.IsNullOrEmpty(appImage)) return appImage;
        return Environment.ProcessPath;
    }

    private static string? AgentFile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "LaunchAgents", LaunchAgentLabel + ".plist");
        if (OperatingSystem.IsLinux())
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config)) config = Path.Combine(home, ".config");
            return Path.Combine(config, "autostart", "pilewise.desktop");
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRunValue()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void WriteRunValue(string? command)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (command != null) key.SetValue(RunValue, command);
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
