using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace Steam2Launcher;

public partial class EditDialog : Window
{
    public string GameName => TxtName.Text.Trim();
    public string GameUrl => TxtUrl.Text.Trim();
    public string GameInstallDir => TxtInstallDir.Text.Trim();
    public string GameExePath => TxtExe.Text.Trim();
    public bool ExeChosen { get; private set; }

    public EditDialog(string name, string url, string installDir = "", string exePath = "")
    {
        InitializeComponent();
        TxtName.Text = name;
        TxtUrl.Text = url;
        TxtInstallDir.Text = installDir;
        TxtExe.Text = exePath;
    }

    private void BtnPickExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "Исполняемые файлы (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|Все файлы|*.*"
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtExe.Text = dlg.FileName;
            ExeChosen = true;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtName.Text) || string.IsNullOrWhiteSpace(TxtUrl.Text))
        {
            MessageBox.Show("Укажите название и ссылку.", "Steam2 Лаунчер",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ExeChosen = !string.IsNullOrWhiteSpace(TxtExe.Text);
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
