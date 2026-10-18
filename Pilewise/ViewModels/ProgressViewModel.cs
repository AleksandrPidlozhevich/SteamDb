using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDb.Models;
using System;
using System.Threading;

namespace SteamDb.ViewModels;

/// <summary>
/// Owns the busy/progress bar state and cancellation of the current long-running operation.
/// Split out of <see cref="MainWindowViewModel"/> so the export commands just call
/// <see cref="Begin"/>/<see cref="End"/> and read <see cref="Token"/>.
/// </summary>
public sealed partial class ProgressViewModel : ObservableObject
{
    [ObservableProperty] private bool isBusy;

    [ObservableProperty] private double value;

    [ObservableProperty] private double maximum = 1;

    [ObservableProperty] private bool isIndeterminate;

    [ObservableProperty] private string? status;

    private CancellationTokenSource? _cts;

    /// <summary>Token for the current operation; cancelled by the Cancel command.</summary>
    public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    public void Begin(string status)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsIndeterminate = true;
        Value = 0;
        Maximum = 1;
        Status = status;
    }

    public void SetIndeterminate(string status)
    {
        IsIndeterminate = true;
        Status = status;
    }

    public void End()
    {
        IsBusy = false;
        IsIndeterminate = false;
        Value = 0;
        Status = null;
        _cts?.Dispose();
        _cts = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        Status = "Cancelling…";
    }

    /// <summary>A progress reporter that drives the bar from a store fetch's stage/total/completed.</summary>
    public IProgress<StoreFetchProgress> CreateReporter()
    {
        // Constructed on the UI thread, so callbacks marshal back to it automatically.
        return new Progress<StoreFetchProgress>(p =>
        {
            var stage = string.IsNullOrEmpty(p.Stage) ? "Working" : p.Stage;

            if (p.Total <= 0)
            {
                IsIndeterminate = true;
                Status = $"{stage}…";
                return;
            }

            IsIndeterminate = false;
            Maximum = p.Total;
            Value = p.Completed;
            Status = $"{stage}… {p.Completed}/{p.Total}";
        });
    }
}
