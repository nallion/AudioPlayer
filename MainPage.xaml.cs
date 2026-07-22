using System;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using AudioVisualizerPlayer.Services;

namespace AudioVisualizerPlayer
{
    public sealed partial class MainPage : Page
    {
        private PlaybackService _playback;
        private VisualizerService _visualizer;

        // Последние уровни баров — обновляются в фоновом потоке (QuantumStarted),
        // читаются в потоке отрисовки CanvasControl. float[] присваивается атомарно,
        // поэтому отдельная блокировка не нужна.
        private float[] _barLevels = new float[40];

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
        }

        private async void MainPage_Loaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _playback = App.Playback;
            _playback.PlaybackStateChanged += OnPlaybackStateChanged;
            _playback.NextRequested += (s, a) => { /* переключение трека в плейлисте */ };
            _playback.PreviousRequested += (s, a) => { /* переключение трека в плейлисте */ };

            // Для демонстрации — выбор файла через FilePicker.
            // В реальном приложении здесь будет плейлист/библиотека.
            await LoadDemoTrackAsync();
        }

        private async System.Threading.Tasks.Task LoadDemoTrackAsync()
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary
            };
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".m4a");

            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null) return;

            await _playback.LoadAsync(file, title: file.DisplayName, artist: "Unknown Artist");

            _visualizer?.Dispose();
            _visualizer = new VisualizerService();
            await _visualizer.InitializeAsync(file);
            _visualizer.LevelsChanged += (s, bars) => _barLevels = bars;
            _visualizer.Start();

            VisualizerCanvas.Invalidate();
        }

        private void PlayPauseButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _playback.TogglePlayPause();
        }

        private void PreviousButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _playback.PreviousRequested?.Invoke(this, EventArgs.Empty);
        }

        private void NextButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            _playback.NextRequested?.Invoke(this, EventArgs.Empty);
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

        // --- Визуализатор: отрисовка баров ---
        private void VisualizerCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var levels = _barLevels;
            if (levels == null || levels.Length == 0) return;

            float canvasWidth = (float)sender.ActualWidth;
            float canvasHeight = (float)sender.ActualHeight;

            int barCount = levels.Length;
            float gap = 3f;
            float barWidth = (canvasWidth - gap * (barCount - 1)) / barCount;

            var accent = Color.FromArgb(255, 0x00, 0xA2, 0xE8);
            var accentLight = Color.FromArgb(255, 0x6F, 0xE0, 0xFF);

            for (int i = 0; i < barCount; i++)
            {
                float h = Math.Max(4f, levels[i] * canvasHeight);
                float x = i * (barWidth + gap);
                float y = canvasHeight - h;

                // Простой вертикальный градиент имитируем двумя прямоугольниками,
                // чтобы не создавать CanvasLinearGradientBrush на каждый Draw —
                // для честного градиента вынесите brush в CanvasControl.CreateResources.
                args.DrawingSession.FillRectangle(x, y, barWidth, h * 0.5f, accentLight);
                args.DrawingSession.FillRectangle(x, y + h * 0.5f, barWidth, h * 0.5f, accent);
            }

            // Перерисовываем непрерывно, пока страница активна
            sender.Invalidate();
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
