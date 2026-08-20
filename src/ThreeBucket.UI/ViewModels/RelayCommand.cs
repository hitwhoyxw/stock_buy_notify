using System;
using System.Windows.Input;

namespace ThreeBucket.UI.ViewModels;

/// <summary>极简 ICommand 实现，支持异步执行（用于按钮绑定）。</summary>
public class RelayCommand : ICommand
{
    private readonly Func<object?, System.Threading.Tasks.Task> _execute;

    public RelayCommand(Func<object?, System.Threading.Tasks.Task> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public async void Execute(object? parameter) => await _execute(parameter);
}
