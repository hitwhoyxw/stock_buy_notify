using Avalonia.Controls;
using ThreeBucket.UI.ViewModels;

namespace ThreeBucket.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
