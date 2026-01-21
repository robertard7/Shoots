using System.ComponentModel;
using System.Windows.Input;

namespace Shoots.UI.ViewModels;

public sealed class AsyncRelayCommand : ICommand, INotifyPropertyChanged
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
        : this(_ => executeAsync(), canExecute)
    {
    }

    public AsyncRelayCommand(Func<object?, Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting)
            return false;

        return _canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        try
        {
            _isExecuting = true;
            OnPropertyChanged(nameof(IsExecuting));
            RaiseCanExecuteChanged();
            await _executeAsync(parameter).ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            OnPropertyChanged(nameof(IsExecuting));
            RaiseCanExecuteChanged();
        }
    }

    public bool IsExecuting => _isExecuting;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
