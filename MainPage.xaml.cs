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

                // ВАЖНО: подписываемся здесь, ДО того как LoadAsync/Play вообще
                // вызовутся. LoadAsync() устанавливает Player.Source, что запускает
                // открытие медиа асинхронно сразу же — MediaOpened вполне может
                // успеть сработать раньше, чем мы подпишемся, если сделать это
                // после LoadDemoTrackAsync()/Play() (так было раньше — из-за этого
                // ProgressSlider.Maximum никогда не обновлялся с дефолтных 100,
                // а перемотка считалась от неправильного диапазона).
                _playback.Player.PlaybackSession.PositionChanged += OnPositionChanged;
                _playback.Player.MediaOpened += OnMediaOpened;
            }
            catch (Exception ex)
            {
                var dialog = new Windows.UI.Popups.MessageDialog(
                    "ШАГ 0 (подписка на события PlaybackService): " + ex, "Ошибка при запуске");
                _ = dialog.ShowAsync();
            }
        }

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

            try
            {
                await _playback.LoadAsync(file, title: file.DisplayName, artist: "Unknown Artist");
            }
            catch (Exception ex)
            {
                throw new Exception("ШАГ 3 (_playback.LoadAsync): " + ex.Message, ex);
            }

            try
            {
                _visualizer?.Dispose();
                _visualizer = new VisualizerService();
                await _visualizer.InitializeAsync(file);
                _visualizer.LevelsChanged += OnLevelsChanged;
                WriteUiDiagnostics("Подписка на LevelsChanged выполнена (ШАГ 4).");
                // Start() здесь больше не вызываем — теперь единственный источник
                // запуска/остановки анализа это OnPlaybackStateChanged, который
                // сработает сразу следом за _playback.Play() в вызывающем коде.
                // Так Start() гарантированно вызывается ровно один раз и всегда
                // синхронно с реальным состоянием MediaPlayer.
            }
            catch (Exception ex)
            {
                throw new Exception("ШАГ 4 (VisualizerService.InitializeAsync): " + ex.Message, ex);
            }
        }

        private static void WriteUiDiagnostics(string text)
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var path = System.IO.Path.Combine(folder.Path, "ui_diagnostics.log");
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + "\n");
            }
            catch
            {
            }
        }

        private static int _levelsChangedCallCount = 0;

        private async void OnLevelsChanged(object sender, float[] bars)
        {
            _barLevels = bars;

            int callNum = System.Threading.Interlocked.Increment(ref _levelsChangedCallCount);
            if (callNum <= 3)
            {
                int previewCount = Math.Min(5, bars.Length);
                var preview = new float[previewCount];
                Array.Copy(bars, preview, previewCount);
                WriteUiDiagnostics($"OnLevelsChanged вызван #{callNum}. bars.Length={bars.Length}, " +
                    $"bars[0..4]=[{string.Join(", ", preview)}], " +
                    $"_barRectangles == null: {_barRectangles == null}");
            }

            // LevelsChanged прилетает из фонового потока AudioGraph — трогать
            // элементы UI (Rectangle.Height) можно только из UI-потока.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (callNum <= 3)
                {
                    WriteUiDiagnostics($"Dispatcher-лямбда #{callNum} реально выполняется на UI-потоке.");
                }

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

                if (callNum <= 3)
                {
                    WriteUiDiagnostics($"Цикл обновления Height завершён #{callNum}. " +
                        $"_barRectangles[0].Height теперь = {_barRectangles[0].Height}, " +
                        $"ActualHeight VisualizerPanel = {VisualizerPanel.ActualHeight}, " +
                        $"Visibility VisualizerPanel = {VisualizerPanel.Visibility}");
                }
            });
        }

        private bool _trackLoaded = false;

        private void OnMediaOpened(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            var duration = sender.PlaybackSession.NaturalDuration;
            var dispatcherUnused = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ProgressSlider.Maximum = duration.TotalSeconds;
                DurationText.Text = FormatTime(duration);
            });
        }

        // true, когда мы САМИ меняем ProgressSlider.Value из OnPositionChanged —
        // чтобы ProgressSlider_ValueChanged не принял это за перемотку от пользователя.
        private bool _isProgrammaticSliderUpdate = false;

        private void OnPositionChanged(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        {
            var position = sender.Position;
            var dispatcherUnused = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _isProgrammaticSliderUpdate = true;
                ProgressSlider.Value = position.TotalSeconds;
                _isProgrammaticSliderUpdate = false;
                ElapsedText.Text = FormatTime(position);
            });
        }

        private static string FormatTime(TimeSpan t) =>
            $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

        // ValueChanged — единственное событие Slider, которое гарантированно
        // срабатывает при ЛЮБОМ изменении значения (перетаскивание пальцем,
        // тап по треку, стрелки клавиатуры), в отличие от PointerPressed/Released,
        // которые висят на внешнем элементе, а не на внутреннем Thumb, и могут
        // не сработать надёжно при touch-перетаскивании — это и было вероятной
        // причиной нерабочей перемотки.
        private void ProgressSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isProgrammaticSliderUpdate) return; // это мы сами обновили из OnPositionChanged, не пользователь
            if (_playback?.Player?.PlaybackSession == null) return;

            _playback.Player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
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

        private void OnPlaybackStateChanged(object sender, MediaPlaybackState state)
        {
            // Синхронизируем анализирующий AudioGraph с реальным состоянием
            // MediaPlayer — иначе граф крутит файл сам по себе независимо от
            // паузы, отсюда и "визуализатор прыгает даже когда аудио на паузе".
            // Не через Dispatcher — Start()/Stop() у AudioGraph не трогают UI.
            if (state == MediaPlaybackState.Playing)
            {
                _visualizer?.Start(_playback.Player.PlaybackSession.Position);
            }
            else
            {
                _visualizer?.Stop();
            }

            // PlayPauseIcon — уже элемент UI, здесь Dispatcher обязателен.
            var dispatcherUnused = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
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
