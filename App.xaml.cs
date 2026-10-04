using System.Windows;
using System.Windows.Threading;

namespace ArtFinder
{
    public partial class App : Application
    {
        public App()
        {
            // Необработанная ошибка в обработчике UI раньше молча закрывала
            // приложение — теперь показывается сообщение, а работа продолжается.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (s, e) => e.SetObserved();
        }

        private static void OnDispatcherUnhandledException(object s, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            MessageBox.Show($"Непредвиденная ошибка:\n{e.Exception.Message}", "ArtFinder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
