using Avalonia.Controls;
using ThreeBucket.UI.Services;
using ThreeBucket.UI.Views;

namespace ThreeBucket.UI;

public partial class MainWindow : Window
{
    public AppState App { get; }

    public MainWindow()
    {
        InitializeComponent();
        App = new AppState();
        Content = new MainView(App);
    }
}
