using System;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using Windows.UI.Xaml.Navigation;
using AudioVisualizerPlayer.Services;

namespace AudioVisualizerPlayer
{
    public sealed partial class MainPage : Page
    {
        private PlaybackService _playback;
        private VisualizerService _visualizer;

        private const int BarCount = 40;
        private Rectangle[] _barRectangles;

        // Последние уровни баров — обновляются в фоновом потоке (QuantumStarted),
        // читаются при обновлении Height каждого Rectangle. float[] присваивается
        // атомарно, поэтому отдельная блокировка не нужна.
        private float[] _barLevels = new float[BarCount];

        public MainPage()
        {
            InitializeComponent();
            CreateVisualizerBars();
            Loaded += MainPage_Loaded;
        }

        private void CreateVisualizerBars()
        {
            _barRectangles = new Rectangle[BarCount];
            var accentBrush = new SolidColorBrush(Color.FromArgb(255, 0x00, 0xA2, 0xE8));

            for (int i = 0; i < BarCount; i++)
            {
                var rect = new Rectangle
                {
                    Width = 300.0 / BarCount - 3,
                    Height = 4, // стартовая минимальная высота
                    Fill = accentBrush,
                    Margin = new Windows.UI.Xaml.Thickness(1, 0, 2, 0),
                    VerticalAlignment = Windows.UI.Xaml.VerticalAlignment.Bottom
                };
                _barRectangles[i] = rect;
                VisualizerPanel.Children.Add(rect);
            }
        }

        private async void MainPage_Loaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                _playback = App.Playback;
                _playback.PlaybackStateChanged += OnPlaybackStateChanged;
                _playback.NextRequested += (s, a) => { /* переключение трека в плейлисте */ };
                _playback.PreviousRequested += (s, a) => { /* переключение трека в плейлисте */ };

                // Для демонстрации — выбор файла через FilePicker.
                // В реальном приложении здесь будет плейлист/библиотека.
                await LoadDemoTrackAsync();
            }
            catch (Exception ex)
            {
                var dialog = new Windows.UI.Popups.MessageDialog(ex.ToString(), "Ошибка при запуске");
                await dialog.ShowAsync();
            }
        }

        private async System.Threading.Tasks.Task LoadDemoTrackAsync()
        {
            // SuggestedStartLocation=MusicLibrary убран: на этом устройстве
            // доступ к библиотеке "Музыка" требует ещё и отдельного пользовательского
            // разрешения (Settings → Privacy → Music Library), которое не выдаётся
            // автоматически даже при наличии capability musicLibrary в манифесте —
            // вместо диалога согласия FileOpenPicker сразу бросал
            // UnauthorizedAccessException (0x80070005). Без SuggestedStartLocation
            // пикер открывается в обычном режиме, который никаких специальных
            // прав не требует вообще.
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".m4a");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await _playback.LoadAsync(file, title: file.DisplayName, artist: "Unknown Artist");

            _visualizer?.Dispose();
            _visualizer = new VisualizerService();
            await _visualizer.InitializeAsync(file);
            _visualizer.LevelsChanged += OnLevelsChanged;
            _visualizer.Start();
        }

        private async void OnLevelsChanged(object sender, float[] bars)
        {
            _barLevels = bars;
            // LevelsChanged прилетает из фонового потока AudioGraph — трогать
            // элементы UI (Rectangle.Height) можно только из UI-потока.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                for (int i = 0; i < BarCount && i < bars.Length; i++)
                {
                    double h = Math.Max(4.0, bars[i] * 70.0);
                    _barRectangles[i].Height = h;
                }
            });
        }

        private void PlayPauseButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _playback.TogglePlayPause();
        }

        private void PreviousButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _playback.RaisePreviousRequested();
        }

        private void NextButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _playback.RaiseNextRequested();
        }

        private async void OnPlaybackStateChanged(object sender, MediaPlaybackState state)
        {
            // PlaybackStateChanged прилетает не из UI-потока — обязательно через Dispatcher
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                PlayPauseIcon.Symbol = state == MediaPlaybackState.Playing
                    ? Symbol.Pause
                    : Symbol.Play;
            });
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Освобождаем AudioGraph при уходе со страницы — визуализация не нужна
            // в фоне, а звук продолжит играть через _playback, который живёт в App.
            _visualizer?.Dispose();
        }
    }
}
