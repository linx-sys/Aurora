/* ============================================================
 * HotkeyController.cs — 全局键盘快捷键（从 MainWindow 拆出）
 * 空格/←→/↑↓/N/P/M/Esc；搜索框内输入不触发（Esc 除外）。
 * 全部经由 ViewModel 命令/属性（唯一播放逻辑入口），不直接碰业务。
 * ============================================================ */
using System;
using System.Windows;
using System.Windows.Input;

namespace Aurora
{
    class HotkeyController
    {
        readonly Window win;
        readonly MainViewModel vm;
        readonly IPlaybackService player;
        readonly Func<bool> isListOpen;
        readonly Action toggleList;
        readonly Action showVolumePopup;

        public HotkeyController(Window win, MainViewModel vm, IPlaybackService player,
            Func<bool> isListOpen, Action toggleList, Action showVolumePopup)
        {
            this.win = win;
            this.vm = vm;
            this.player = player;
            this.isListOpen = isListOpen;
            this.toggleList = toggleList;
            this.showVolumePopup = showVolumePopup;
        }

        public void Hook() { win.KeyDown += OnKeyDown; }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            // 焦点在文本框（搜索框）时，字母/空格/方向键都是文本编辑的一部分，
            // 不得触发全局快捷键（实测：搜索框里输 N/P 会直接切歌、↑↓ 会改音量）；
            // 仅保留 Esc 收起列表面板的交互
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (isListOpen()) toggleList();
                return;
            }

            // 与界面按钮同源：全部经由 ViewModel 命令 / 属性（唯一播放逻辑入口）
            switch (e.Key)
            {
                case Key.Space:
                    e.Handled = true;
                    if (vm.TogglePlayCommand.CanExecute(null)) vm.TogglePlayCommand.Execute(null);
                    break;
                case Key.Right:
                    if (player.Duration > TimeSpan.Zero)
                        player.Position += TimeSpan.FromSeconds(5);
                    break;
                case Key.Left:
                    if (player.Duration > TimeSpan.Zero)
                        player.Position -= TimeSpan.FromSeconds(5);
                    break;
                case Key.Up:
                    vm.Volume = UiUtil.Clamp(vm.Volume + 0.05, 0, 1);
                    showVolumePopup();
                    break;
                case Key.Down:
                    vm.Volume = UiUtil.Clamp(vm.Volume - 0.05, 0, 1);
                    showVolumePopup();
                    break;
                case Key.N:
                    if (vm.NextCommand.CanExecute(null)) vm.NextCommand.Execute(null);
                    break;
                case Key.P:
                    if (vm.PrevCommand.CanExecute(null)) vm.PrevCommand.Execute(null);
                    break;
                case Key.M:
                    if (vm.ToggleMuteCommand.CanExecute(null)) vm.ToggleMuteCommand.Execute(null);
                    showVolumePopup();
                    break;
                case Key.Escape:
                    e.Handled = true;
                    // Esc 优先收起播放列表抽屉；主界面时不再退出应用（防误触丢状态）
                    if (isListOpen()) toggleList();
                    break;
            }
        }
    }
}
