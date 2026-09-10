/* ============================================================
 * ViewModelBase.cs — MVVM 基类（INotifyPropertyChanged）
 * ============================================================ */
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Aurora
{
    /// <summary>所有 ViewModel 的基类，提供属性变更通知。</summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>触发属性变更通知。</summary>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>设置字段值并在变更时触发通知。返回是否发生了变更。</summary>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
