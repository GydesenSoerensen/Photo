using Microsoft.Extensions.DependencyInjection;
using PhotoGallery.Data;           // IPhotoRepository, SqlitePhotoRepository
using PhotoGallery.Imaging;        // IExifReader, GdiExifReader
using System.Windows;

namespace PhotoGallery
{
    public partial class App : Application
    {
        // This will hold our DI container
        public static IServiceProvider Services { get; private set; } = null!;

        private void OnStartup(object sender, StartupEventArgs e)
        {
            // 1. Configure DI
            var sc = new ServiceCollection();

            // register your services
            sc.AddSingleton<IPhotoRepository, SqlitePhotoRepository>();
            sc.AddSingleton<IExifReader, GdiExifReader>();


            // register MainWindow so it can get ctor-injected
            sc.AddSingleton<MainWindow>();

            // build the container
            Services = sc.BuildServiceProvider();
            PhotoDb.Initialize();
            // 2. Resolve and show the main window
            var main = Services.GetRequiredService<MainWindow>();
            MainWindow = main;   // tell WPF which is the “main” window
            main.Show();

            //base.OnStartup(e);
        }
    }
}
