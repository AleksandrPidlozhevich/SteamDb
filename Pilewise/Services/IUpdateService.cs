using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDb.Services;

/// <summary>Checks GitHub Releases for a newer version and installs it (see <see cref="VelopackUpdateService"/>).</summary>
public interface IUpdateService
{
    /// <summary>The running app's version, e.g. <c>1.1.0</c>.</summary>
    string CurrentVersion { get; }

    /// <summary>Installed through Velopack (the GitHub release installer), so it can update itself;
    /// false for <c>dotnet run</c> / development builds.</summary>
    bool IsInstalled { get; }

    /// <summary>Why the last check failed (offline, GitHub rate limit…), or null when it worked.</summary>
    string? LastCheckError { get; }

    /// <summary>
    /// Returns the newer version available for this platform, or null when up to date, when the app
    /// wasn't installed through Velopack (e.g. <c>dotnet run</c>), or when the check fails.
    /// </summary>
    Task<string?> CheckForUpdateAsync();

    /// <summary>Downloads the update found by <see cref="CheckForUpdateAsync"/>, then restarts into it.</summary>
    Task DownloadAndRestartAsync(IProgress<int>? progress = null, CancellationToken ct = default);
}
