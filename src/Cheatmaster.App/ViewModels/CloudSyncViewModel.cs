using System.Diagnostics;
using Cheatmaster.App.Infrastructure;
using Cheatmaster.App.Services;
using Cheatmaster.Core.Sync;

namespace Cheatmaster.App.ViewModels;

/// <summary>
/// Drives the cloud-backup dialog: device-flow sign-in, then backing the library up to the
/// user's own private repository.
/// </summary>
public sealed class CloudSyncViewModel : ObservableObject
{
    private readonly GitHubSyncService _sync = new();
    private readonly AppSettings _settings;

    private CancellationTokenSource? _cancellation;
    private DeviceCodePrompt? _prompt;
    private string _status = string.Empty;
    private string _userCode = string.Empty;
    private bool _isBusy;
    private bool _isWaitingForAuthorisation;

    public CloudSyncViewModel(AppSettings settings)
    {
        _settings = settings;

        SignInCommand = new RelayCommand(async () => await SignInAsync(), () => !IsSignedIn && !IsBusy);
        SignOutCommand = new RelayCommand(SignOut, () => IsSignedIn && !IsBusy);
        SyncCommand = new RelayCommand(async () => await SyncAsync(), () => IsSignedIn && !IsBusy);
        OpenRepositoryCommand = new RelayCommand(OpenRepository, () => IsSignedIn);
        CopyCodeCommand = new RelayCommand(CopyCode, () => IsWaitingForAuthorisation);
        OpenVerificationCommand = new RelayCommand(OpenVerification, () => IsWaitingForAuthorisation);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);

        Status = IsSignedIn
            ? "Your library is backed up to a private repository on your GitHub account."
            : "Sign in with GitHub to back your cheat library up to a private repository. Nothing is shared publicly.";
    }

    public RelayCommand SignInCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public RelayCommand SyncCommand { get; }
    public RelayCommand OpenRepositoryCommand { get; }
    public RelayCommand CopyCodeCommand { get; }
    public RelayCommand OpenVerificationCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool IsSignedIn => _sync.IsSignedIn;
    public string Username => _sync.Username;
    public string RepositoryName => GitHubSyncService.RepositoryName;

    public string AccountLine => IsSignedIn ? $"Signed in as {Username}" : "Not signed in";

    public string LastSyncText => _settings.LastSyncUtc is { } when
        ? "Last backup " + when.ToLocalTime().ToString("d MMM yyyy, HH:mm")
        : "Never backed up";

    public bool AutoBackup
    {
        get => _settings.AutoBackup;
        set
        {
            if (_settings.AutoBackup == value) return;
            _settings.AutoBackup = value;
            _settings.Save();
            Raise();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string UserCode
    {
        get => _userCode;
        private set => Set(ref _userCode, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            RaiseAll();
        }
    }

    public bool IsWaitingForAuthorisation
    {
        get => _isWaitingForAuthorisation;
        private set
        {
            if (!Set(ref _isWaitingForAuthorisation, value)) return;
            RaiseAll();
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(IsSignedIn));
        Raise(nameof(AccountLine));
        Raise(nameof(LastSyncText));
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        SyncCommand.RaiseCanExecuteChanged();
        OpenRepositoryCommand.RaiseCanExecuteChanged();
        CopyCodeCommand.RaiseCanExecuteChanged();
        OpenVerificationCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private async Task SignInAsync()
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        IsBusy = true;
        try
        {
            _prompt = await _sync.StartSignInAsync(_cancellation.Token);
            UserCode = _prompt.UserCode;
            IsWaitingForAuthorisation = true;
            Status = "Enter this code on GitHub, then come back. Waiting…";

            OpenVerification();

            bool ok = await _sync.CompleteSignInAsync(_prompt, _cancellation.Token);
            if (ok)
            {
                Status = $"Signed in as {Username}. Backing up now…";
                IsWaitingForAuthorisation = false;
                await SyncCoreAsync(_cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Sign-in cancelled.";
        }
        catch (SyncException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Status = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            IsWaitingForAuthorisation = false;
            UserCode = string.Empty;
            IsBusy = false;
        }
    }

    private void SignOut()
    {
        _sync.SignOut();
        Status = "Signed out. The backup repository is untouched.";
        RaiseAll();
    }

    private async Task SyncAsync()
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();

        IsBusy = true;
        try
        {
            await SyncCoreAsync(_cancellation.Token);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncCoreAsync(CancellationToken ct)
    {
        var progress = new Progress<string>(message => Status = message);
        try
        {
            // Reading and writing the library is disk work; keep all of it off the UI thread.
            var outcome = await Task.Run(() => _sync.SyncAsync(progress, ct), ct).ConfigureAwait(true);
            _settings.LastSyncUtc = DateTimeOffset.UtcNow;
            _settings.Save();
            Status = outcome.Message;
            Raise(nameof(LastSyncText));
        }
        catch (OperationCanceledException)
        {
            Status = "Backup cancelled.";
        }
        catch (SyncException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Status = "Backup failed: " + ex.Message;
        }
    }

    private void OpenRepository() => Launch(_sync.RepositoryUrl);

    private void OpenVerification() => Launch(_prompt?.VerificationUri ?? "https://github.com/login/device");

    private void CopyCode()
    {
        try
        {
            System.Windows.Clipboard.SetText(UserCode);
            Status = "Code copied. Paste it on GitHub.";
        }
        catch (Exception ex)
        {
            Status = "Could not copy the code: " + ex.Message;
        }
    }

    private void Launch(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = "Could not open the browser: " + ex.Message;
        }
    }
}
