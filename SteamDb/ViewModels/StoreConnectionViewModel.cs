using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamDb.Models;
using SteamDb.Services;
using System;
using System.Threading.Tasks;

namespace SteamDb.ViewModels;

/// <summary>
/// One store's connect state (Epic / GOG / Xbox): the bindable connected/code-input flags and the
/// connect command, wrapping a shared <see cref="StoreConnector"/>. Collapses the per-store
/// property triples that used to live directly on <see cref="MainWindowViewModel"/>.
/// </summary>
public sealed partial class StoreConnectionViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowCodeInput))]
    private bool isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnectButton))]
    [NotifyPropertyChangedFor(nameof(ShowCodeInput))]
    private bool isCodeInputVisible;

    [ObservableProperty] private string? authorizationCode;

    private readonly StoreConnector _connector;
    private readonly bool _supportsCodeInput;
    private readonly string _connectingStatus;
    private readonly Action<string>? _beginBusy;
    private readonly Action? _endBusy;
    private readonly Func<StoreConnectionViewModel, Task>? _onConnected;
    private readonly Action? _openInfo;

    public string Name { get; }

    /// <summary>Label for the connect button, e.g. "Connect Epic".</summary>
    public string ConnectLabel => $"Connect {Name}";

    /// <summary>Placeholder for the authorization-code field (paste-flow stores only).</summary>
    public string? CodePlaceholder { get; }

    /// <summary>Initial state: show the compact "Connect" button.</summary>
    public bool ShowConnectButton => !IsConnected && !IsCodeInputVisible;

    /// <summary>After clicking Connect (paste flow): show the authorization-code field.</summary>
    public bool ShowCodeInput => _supportsCodeInput && !IsConnected && IsCodeInputVisible;

    /// <summary>Whether this store has a help link wired up (controls the info button's visibility).</summary>
    public bool HasInfo => _openInfo != null;

    public StoreConnectionViewModel(
        string name,
        Func<IStoreClient> clientFactory,
        Func<string?, string?> extractCode,
        Func<IClipboard?> getClipboard,
        Func<string, Exception, Task> showError,
        ILogService log,
        IWebAuthenticator? webAuth = null,
        bool supportsCodeInput = false,
        string? codePlaceholder = null,
        string? connectingStatus = null,
        Action<string>? beginBusy = null,
        Action? endBusy = null,
        Func<StoreConnectionViewModel, Task>? onConnected = null,
        Action? openInfo = null)
    {
        Name = name;
        CodePlaceholder = codePlaceholder;
        _supportsCodeInput = supportsCodeInput;
        _connectingStatus = connectingStatus ?? $"Opening {name} login…";
        _beginBusy = beginBusy;
        _endBusy = endBusy;
        _onConnected = onConnected;
        _openInfo = openInfo;

        _connector = new StoreConnector(
            name,
            clientFactory,
            extractCode,
            v => IsConnected = v,
            v => IsCodeInputVisible = v,
            c => AuthorizationCode = c,
            () => IsConnected,
            getClipboard,
            showError,
            log,
            webAuth);
    }

    /// <summary>Restore a previously cached session (called on startup).</summary>
    public Task InitializeFromCacheAsync() => _connector.InitializeFromCacheAsync();

    [RelayCommand]
    private async Task StartConnect()
    {
        _beginBusy?.Invoke(_connectingStatus);
        try
        {
            await _connector.StartConnectAsync();

            if (IsConnected && _onConnected != null)
                await _onConnected(this);
        }
        finally
        {
            _endBusy?.Invoke();
        }
    }

    [RelayCommand]
    private void Disconnect() => _connector.SignOut();

    // Opens this store's help page (the relevant README section on GitHub).
    [RelayCommand]
    private void OpenInfo() => _openInfo?.Invoke();

    // Epic paste flow: authorize automatically as soon as a valid code is typed/pasted.
    partial void OnAuthorizationCodeChanged(string? value)
    {
        _connector.OnCodeChanged(value);
    }
}
