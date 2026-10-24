using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace TranssionLab.ViewModels.Mvvm;

public static class UiInteractionBridge
{
    public static Func<string, string, string?>? PickOpenFile { get; set; }
    public static Func<string, string, string, string?>? PickSaveFile { get; set; }
    public static Func<string, string?>? PickDirectory { get; set; }
    public static Action<string>? CopyTextToClipboard { get; set; }
    public static Action<Action>? InvokeOnUiThread { get; set; }
    public static Action<Exception>? OnCommandException { get; set; }

    public static void RunOnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var invoker = InvokeOnUiThread;
        if (invoker is not null)
        {
            invoker(action);
        }
        else
        {
            action();
        }
    }
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        var handler = PropertyChanged;
        if (handler is null)
            return;

        UiInteractionBridge.RunOnUiThread(() =>
            handler.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public abstract class PageViewModelBase : ObservableObject
{
    public abstract int PageNumber { get; }
    public abstract string PageTitle { get; }
    public abstract string NavigationGroup { get; }
    public abstract string IconGlyph { get; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        protected set => SetProperty(ref _isBusy, value);
    }

    private bool _isWaitingForDevice;
    public bool IsWaitingForDevice
    {
        get => _isWaitingForDevice;
        protected internal set => SetProperty(ref _isWaitingForDevice, value);
    }

    private string _statusBanner = "Ready";
    public string StatusBanner
    {
        get => _statusBanner;
        protected internal set => SetProperty(ref _statusBanner, value);
    }

    public virtual Task OnNavigatedToAsync(CancellationToken ct = default) => Task.CompletedTask;

    protected async Task RunSafeActivityAsync(Func<Task> action, string? errorPrefix = null)
    {
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException ex)
        {
            StatusBanner = string.IsNullOrWhiteSpace(ex.Message)
                ? "Cancelled — stopped waiting for device / operation aborted."
                : ex.Message;
        }
        catch (Exception ex)
        {
            StatusBanner = string.IsNullOrWhiteSpace(errorPrefix)
                ? ex.Message
                : $"{errorPrefix}: {ex.Message}";
            UiInteractionBridge.OnCommandException?.Invoke(ex);
        }
        finally
        {
            IsWaitingForDevice = false;
            IsBusy = false;
        }
    }

    protected Action<string> CreateWaitProgressCallback() =>
        msg => UiInteractionBridge.RunOnUiThread(() =>
        {
            IsWaitingForDevice = true;
            StatusBanner = msg;
        });
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
    {
        try
        {
            return _canExecute?.Invoke(parameter) ?? true;
        }
        catch
        {
            return false;
        }
    }

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        try
        {
            _execute(parameter);
        }
        catch (Exception ex)
        {
            UiInteractionBridge.OnCommandException?.Invoke(ex);
        }
    }

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler is null)
            return;

        UiInteractionBridge.RunOnUiThread(() => handler.Invoke(this, EventArgs.Empty));
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = _ => executeAsync();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    public AsyncRelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public bool IsExecuting => _isExecuting;

    public bool CanExecute(object? parameter)
    {
        try
        {
            return !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);
        }
        catch
        {
            return false;
        }
    }

    public async void Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(true);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
            return;

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Handled cleanly as user cancellation
        }
        catch (Exception ex)
        {
            UiInteractionBridge.OnCommandException?.Invoke(ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        var handler = CanExecuteChanged;
        if (handler is null)
            return;

        UiInteractionBridge.RunOnUiThread(() => handler.Invoke(this, EventArgs.Empty));
    }
}
