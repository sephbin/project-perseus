using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ProjectPerseus.ui
{
    /// <summary>Small modal text prompt (code-only WPF). Returns null when cancelled.</summary>
    internal static class TextPrompt
    {
        /// <param name="owner">Owner window handle (Revit main window or a panel); centred on it.</param>
        /// <param name="multiline">Multi-line box: Ctrl+Enter confirms; single line: Enter confirms.</param>
        internal static string Ask(IntPtr owner, string title, string prompt, string initial = "", bool multiline = false,
                                   string okText = "OK", string hint = null)
        {
            var box = new TextBox
            {
                Text = initial ?? "", Width = 340, Padding = new Thickness(3),
                AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MinHeight = multiline ? 64 : 0,
            };
            var ok = new Button { Content = okText, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0), IsDefault = !multiline };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            panel.Children.Add(box);
            string h = hint ?? (multiline ? "Ctrl+Enter to confirm" : null);
            if (h != null) panel.Children.Add(new TextBlock { Text = h, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            panel.Children.Add(buttons);
            var dlg = new Window
            {
                Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, WindowStartupLocation = owner != IntPtr.Zero ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            if (owner != IntPtr.Zero) new WindowInteropHelper(dlg).Owner = owner;
            string result = null;
            void Done() { result = box.Text; dlg.DialogResult = true; }
            ok.Click += (s, e) => Done();
            if (multiline)
                box.KeyDown += (s, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; Done(); } };
            dlg.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            dlg.ShowDialog();
            return result;
        }
    }
}
