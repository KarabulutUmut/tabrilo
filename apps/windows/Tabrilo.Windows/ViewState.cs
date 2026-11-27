using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Tabrilo.Windows;

internal sealed class ActionCommand(Action<object?> execute, Predicate<object?>? available = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => available?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

internal sealed record InputItem(string Path, string Label, bool Clipboard);

internal sealed class ViewState : INotifyPropertyChanged
{
    private string status = "Drop, paste or choose files.";
    private bool busy;
    public ICommand Dispatch { get; }
    public ICommand CancelJob { get; }
    public ObservableCollection<InputItem> Inputs { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public ViewState(Action<string> dispatch, Action cancel)
    {
        Dispatch = new ActionCommand(id => dispatch((string)id!));
        CancelJob = new ActionCommand(_ => cancel(), _ => Busy);
    }

    public string Status
    {
        get => status;
        set { status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); }
    }

    public bool Busy
    {
        get => busy;
        set
        {
            busy = value;
            PropertyChanged?.Invoke(this, new(nameof(Busy)));
            PropertyChanged?.Invoke(this, new(nameof(ProgressVisibility)));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public Visibility ProgressVisibility => Busy ? Visibility.Visible : Visibility.Collapsed;
}
