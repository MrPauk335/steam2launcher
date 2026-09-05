using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace Steam2Launcher;

public partial class AuthDialog : Window
{
    public string User => TxtUser.Text.Trim();
    public string Pass => TxtPass.Password;

    public AuthDialog(string host)
    {
        InitializeComponent();
        TxtHost.Text = $"Сервер «{host}» требует логин и пароль для скачивания:";
        TxtUser.Focus();
    }

    public void SetUser(string user)
    {
        TxtUser.Text = user;
        TxtPass.Focus();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(User))
        {
            MessageBox.Show("Введите логин.", "Steam2 Лаунчер",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}