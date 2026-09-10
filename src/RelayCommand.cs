/* ============================================================
 * RelayCommand.cs — MVVM 命令实现（ICommand）
 * ============================================================ */
using System;
using System.Windows.Input;

namespace Aurora
{
    /// <summary>通用命令实现，将执行逻辑委托给 Action。</summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(execute != null ? new Action<object?>(_ => execute()) : (Action<object?>)null!,
                   canExecute != null ? new Func<object?, bool>(_ => canExecute()) : (Func<object?, bool>)null!)
        {
        }

        public bool CanExecute(object? parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object? parameter)
        {
            _execute(parameter);
        }

        /// <summary>手动触发 CanExecute 重新评估。</summary>
        public void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
