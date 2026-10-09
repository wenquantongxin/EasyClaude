using System.Windows;

namespace EasyClaude;
public partial class CloseWindow : Window
{
    public bool ExitProgram { get; private set; }
    public CloseWindow() => InitializeComponent();
    private void Background_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Exit_Click(object sender, RoutedEventArgs e) { ExitProgram = true; DialogResult = true; }
}
