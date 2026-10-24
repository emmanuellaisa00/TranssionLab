using System.Windows;
using TranssionLab.ViewModels;

namespace TranssionLab.App;

public partial class MainWindow : Window
{
    public MainWindow(MainShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await viewModel.GlobalScanDevicesCommand.ExecuteAsync().ConfigureAwait(true);
        };
    }
}
