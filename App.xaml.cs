using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using AudioVisualizerPlayer.Services;

namespace AudioVisualizerPlayer
{
    /// <summary>
    /// App-level синглтон плеера. Важно: держим ссылку здесь, а не в MainPage —
    /// иначе объект MediaPlayer будет собран сборщиком мусора при выгрузке страницы,
    /// и фоновое воспроизведение прервётся.
    /// </summary>
    public sealed partial class App : Application
    {
        public static PlaybackService Playback { get; private set; }

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Плеер создаём один раз за всё время жизни процесса
            if (Playback == null)
            {
                Playback = new PlaybackService();
            }

            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                Window.Current.Activate();
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new System.Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            // Ничего не останавливаем здесь: MediaPlayer должен продолжать играть
            // в фоне. Приложению для этого не нужен deferral — система сама держит
            // процесс живым, пока идёт воспроизведение через MediaPlayer + SMTC.
        }
    }
}
