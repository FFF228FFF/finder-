using System.Windows;

namespace ArtFinder
{
    public partial class ApiSettingsWindow : Window
    {
        public string E621Login     { get; private set; }
        public string E621ApiKey    { get; private set; }
        public string Rule34Login   { get; private set; }
        public string Rule34ApiKey  { get; private set; }

        public ApiSettingsWindow(string e621Login, string e621ApiKey,
                                 string rule34Login, string rule34ApiKey)
        {
            InitializeComponent();
            TxtE621Login.Text     = e621Login;
            PwdE621Key.Password   = e621ApiKey;
            TxtRule34Login.Text   = rule34Login;
            PwdRule34Key.Password = rule34ApiKey;

            E621Login    = e621Login;
            E621ApiKey   = e621ApiKey;
            Rule34Login  = rule34Login;
            Rule34ApiKey = rule34ApiKey;
        }

        private void BtnSave_Click(object s, RoutedEventArgs e)
        {
            E621Login    = TxtE621Login.Text.Trim();
            E621ApiKey   = PwdE621Key.Password.Trim();
            Rule34Login  = TxtRule34Login.Text.Trim();
            Rule34ApiKey = PwdRule34Key.Password.Trim();
            DialogResult = true;
        }

        private void BtnCancel_Click(object s, RoutedEventArgs e) => DialogResult = false;
    }
}
