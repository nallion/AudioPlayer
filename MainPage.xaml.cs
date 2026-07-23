using System;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
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

        private bool _trackLoaded = false;
        private DispatcherTimer _positionTimer;

        // true, когда мы САМИ меняем ProgressSlider.Value из таймера позиции —
        // чтобы ProgressSlider_ValueChanged не принял это за перемотку от пользователя.
        private bool _isProgrammaticSliderUpdate = false;

        public MainPage()
        {
            InitializeComponent();
            CreateVisualizerBars();
            Loaded += MainPage_Loaded;
        }

        private void CreateVisualizerBars()
        {
            _barRectangles = new Rectangle[BarCount];

            for (int i = 0; i < BarCount; i++)
            {
                VisualizerPanel.ColumnDefinitions.Add(new Windows.UI.Xaml.Controls.ColumnDefinition
                {
                    Width = new Windows.UI.Xaml.GridLength(1, Windows.UI.Xaml.GridUnitType.Star)
                });

                // Цвет по частоте: hue проходит по спектру радуги от низких
                // частот (i=0) к высоким (i=BarCount-1). Диапазон 0..300 градусов
                // (а не полные 360) — иначе конец шкалы вернулся бы обратно
                // к тому же красному, с которого начали.
                double hue = 300.0 * i / (BarCount - 1);
                var barBrush = new SolidColorBrush(ColorFromHsv(hue, 0.85, 1.0));

                var rect = new Rectangle
                {
                    Height = 4, // стартовая минимальная высота
                    Fill = barBrush,
                    Margin = new Windows.UI.Xaml.Thickness(1, 0, 1, 0),
                    VerticalAlignment = Windows.UI.Xaml.VerticalAlignment.Bottom,
                    HorizontalAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch
                };
                Windows.UI.Xaml.Controls.Grid.SetColumn(rect, i);
                _barRectangles[i] = rect;
                VisualizerPanel.Children.Add(rect);
            }
        }

        private static Color ColorFromHsv(double hue, double saturation, double value)
        {
            int hi = (int)(hue / 60) % 6;
            double f = hue / 60 - Math.Floor(hue / 60);

            byte v = (byte)(value * 255);
            byte p = (byte)(value * (1 - saturation) * 255);
            byte q = (byte)(value * (1 - f * saturation) * 255);
            byte t = (byte)(value * (1 - (1 - f) * saturation) * 255);

            switch (hi)
            {
                case 0: return Color.FromArgb(255, v, t, p);
                case 1: return Color.FromArgb(255, q, v, p);
                case 2: return Color.FromArgb(255, p, v, t);
                case 3: return Color.FromArgb(255, p, q, v);
                case 4: return Color.FromArgb(255, t, p, v);
                default: return Color.FromArgb(255, v, p, q);
            }
        }

        private void MainPage_Loaded(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                _playback = App.Playback;
                _playback.PlaybackStateChanged += OnPlaybackStateChanged;
                _playback.NextRequested += (s, a) => { /* переключение трека в плейлисте */ };
                _playback.PreviousRequested += (s, a) => { /* переключение трека в плейлисте */ };

                // Позиция и длительность теперь без MediaPlayer — раз в 500мс
                // опрашиваем AudioFileInputNode.Position через PlaybackService.
                // Раньше это были push-события MediaPlaybackSession.PositionChanged/
                // MediaOpened, но их источника (MediaPlayer) больше нет: единый
                // AudioGraph отдаёт позицию через простое свойство, без событий.
                _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _positionTimer.Tick += PositionTimer_Tick;
                _positionTimer.Start();
            }
            catch (Exception ex)
            {
                var dialog = new Windows.UI.Popups.MessageDialog(
                    "ШАГ 0 (подписка на события PlaybackService): " + ex, "Ошибка при запуске");
                _ = dialog.ShowAsync();
            }
        }

        private void PositionTimer_Tick(object sender, object e)
        {
            if (_playback == null || !_trackLoaded) return;

            _isProgrammaticSliderUpdate = true;
            var position = _playback.Position;
            ProgressSlider.Value = position.TotalSeconds;
            ElapsedText.Text = FormatTime(position);
            _isProgrammaticSliderUpdate = false;
        }

        private static string FormatTime(TimeSpan t) =>
            $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

        private async System.Threading.Tasks.Task LoadDemoTrackAsync()
        {
            // Диагностика: .NET Native даёт стек-трейс из голых адресов
            // (SharedLibrary!<BaseAddress>+0x...) без номеров строк — по нему
            // нельзя понять, какая именно строка бросает исключение. Помечаем
            // каждый потенциально опасный шаг явно, чтобы это было видно
            // прямо в тексте диалога.
            FileOpenPicker picker;
            try
            {
                picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".mp3");
                picker.FileTypeFilter.Add(".wav");
                picker.FileTypeFilter.Add(".m4a");
            }
            catch (Exception ex)
            {
                throw new Exception("ШАГ 1 (создание FileOpenPicker): " + ex.Message, ex);
            }

            StorageFile file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                throw new Exception("ШАГ 2 (PickSingleFileAsync): " + ex.Message, ex);
            }

            if (file == null) return;

            string title, artist;
            try
            {
                // Стандартный, встроенный в Windows способ чтения ID3-тегов в UWP —
                // без сторонних библиотек, свойства читаются системным индексатором.
                var musicProps = await file.Properties.GetMusicPropertiesAsync();

                title = string.IsNullOrWhiteSpace(musicProps.Title) ? file.DisplayName : musicProps.Title;
                artist = string.IsNullOrWhiteSpace(musicProps.Artist) ? file.DisplayName : musicProps.Artist;
            }
            catch (Exception ex)
            {
                throw new Exception("ШАГ 2.5 (GetMusicPropertiesAsync): " + ex.Message, ex);
            }

            try
            {
                await _playback.LoadAsync(file, title: title, artist: artist);

                TrackTitleText.Text = title;
                TrackArtistText.Text = artist;

                // Duration доступна сразу после LoadAsync — AudioFileInputNode
                // читает метаданные файла синхронно при создании узла, никакого
                // отдельного асинхронного события (в отличие от MediaOpened)
                // дожидаться не нужно.
                ProgressSlider.Maximum = _playback.Duration.TotalSeconds;
                DurationText.Text = FormatTime(_playback.Duration);
            }
            catch (Exception ex)
            {
                throw new Exception("ШАГ 3 (_playback.LoadAsync): " + ex.Message, ex);
            }

            try
            {
                _visualizer?.Dispose();
                _visualizer = new VisualizerService();
                _visualizer.AttachTo(_playback);
                _visualizer.LevelsChanged += OnLevelsChanged;
                // Отдельного Start()/Stop() у визуализатора больше нет — он
                // просто подключён вторым выходом к общему AudioGraph, и
                // получает кадры ровно тогда, когда играет реальный звук
                // (единственный источник Start/Stop — _playback.Play()/Pause()).
            }
            catch (Exception ex)
            {
                throw new Exception("ШАГ 4 (VisualizerService.AttachTo): " + ex.Message, ex);
            }
        }

        private async void OnLevelsChanged(object sender, float[] bars)
        {
            // LevelsChanged прилетает из фонового потока AudioGraph — трогать
            // элементы UI (Rectangle.Height) можно только из UI-потока.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Реальная высота панели вместо захардкоженного числа — так
                // визуализатор всегда использует всё доступное место, а не
                // фиксированную полоску независимо от размера экрана/раскладки.
                double panelHeight = VisualizerPanel.ActualHeight;
                if (panelHeight <= 0) panelHeight = 200; // пока layout не посчитан при самом первом кадре

                for (int i = 0; i < BarCount && i < bars.Length; i++)
                {
                    double h = Math.Max(4.0, bars[i] * panelHeight);
                    _barRectangles[i].Height = h;
                }
            });
        }

        // ValueChanged — единственное событие Slider, которое гарантированно
        // срабатывает при ЛЮБОМ изменении значения (перетаскивание пальцем,
        // тап по треку, стрелки клавиатуры), в отличие от PointerPressed/Released,
        // которые висят на внешнем элементе, а не на внутреннем Thumb.
        private void ProgressSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isProgrammaticSliderUpdate) return; // это мы сами обновили из таймера, не пользователь
            if (_playback == null) return;

            _playback.Position = TimeSpan.FromSeconds(e.NewValue);
        }

        private async void PlayPauseButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (!_trackLoaded)
            {
                // Пикер вызывается здесь, а не в MainPage_Loaded, специально:
                // на момент клика окно приложения гарантированно активно
                // (Window.Current.Activate() уже отработал) — раньше пикер падал
                // с UnauthorizedAccessException (0x80070005) именно из-за гонки:
                // Page.Loaded срабатывает синхронно внутри Frame.Navigate(),
                // то есть ДО Window.Current.Activate() в App.OnLaunched, и брокер
                // пикера отказывал, потому что окно ещё не в foreground.
                try
                {
                    await LoadDemoTrackAsync();
                    _trackLoaded = true;
                    _playback.Play(); // автоплей сразу после выбора файла
                }
                catch (Exception ex)
                {
                    var dialog = new Windows.UI.Popups.MessageDialog(ex.ToString(), "Ошибка при выборе файла");
                    await dialog.ShowAsync();
                }
                return;
            }

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

        private void OnPlaybackStateChanged(object sender, bool isPlaying)
        {
            // Визуализатор больше не нужно отдельно останавливать/запускать —
            // он подключён к общему AudioGraph, который уже сам стартует/стопится
            // внутри _playback.Play()/Pause(). QuantumStarted естественным
            // образом перестаёт приходить, когда граф на паузе, и это разом
            // касается и звука, и анализа — без риска рассинхронизации.
            var dispatcherUnused = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                PlayPauseIcon.Symbol = isPlaying ? Symbol.Pause : Symbol.Play;
            });
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _positionTimer?.Stop();
            // Освобождаем анализирующее соединение при уходе со страницы —
            // визуализация не нужна в фоне, а звук продолжит играть через
            // _playback, который живёт в App.
            _visualizer?.Dispose();
        }
    }
}
