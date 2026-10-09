using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EasyClaude;

public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Easy Claude {version?.ToString(3)}";
    }

    public event Action? CloseRequested;

    internal void FocusReturnButton() => ReturnButton.Focus();

    private void Return_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } && FindName(section) is FrameworkElement target)
            HelpScroll.ScrollToVerticalOffset(target.TranslatePoint(new Point(), HelpDocument).Y);
    }

    private void Help_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseRequested?.Invoke();
    }
}
