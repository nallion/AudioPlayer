using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        private static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".m4a" };

        private PlaybackService _playback;
        private VisualizerService _visualizer;

        private const int BarCount = 40;
        private Rectangle[] _barRectangles;

        private bool _trackLoaded = false;
        private DispatcherTimer _positionTimer;

        // Плейлист: либо один файл (выбор через "Выбрать файл"), либо все
        // поддерживаемые аудиофайлы из папки (через "Выбрать папку").
        // Previous/Next ходят по этому списку с закольцовкой на краях.
        private List<StorageFile> _playlist = new List<StorageFile>();
        private int _currentIndex = -1;

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

                // Аппаратные/лок-скрин кнопки Next/Previous (через SMTC) идут
                // через те же самые методы, что и кнопки в UI — единая логика
                // переключения треков, а не два независимых пути.
                _playback.NextRequested += async (s, a) => await PlayNextTrackAsync();
                _playback.PreviousRequested += async (s, a) => await PlayPreviousTrackAsync();

                // Позиция и длительность — раз в 500мс опрашиваем
                // AudioFileInputNode.Position через PlaybackService.
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

        // --- Боковое меню ---

        private void MenuButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            RootSplitView.IsPaneOpen = !RootSplitView.IsPaneOpen;
        }

        private async void PickFileMenuItem_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            RootSplitView.IsPaneOpen = false;

            var picker = new FileOpenPicker();
            // Без SuggestedStartLocation: указание конкретной защищённой
            // библиотеки (MusicLibrary) раньше давало UnauthorizedAccessException
            // (0x80070005) на этом устройстве — обычный режим пикера работает
            // без специальных прав вообще.
            foreach (var ext in SupportedExtensions)
                picker.FileTypeFilter.Add(ext);

            StorageFile file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                await new Windows.UI.Popups.MessageDialog(ex.ToString(), "Ошибка выбора файла").ShowAsync();
                return;
            }

            if (file == null) return;

            _playlist = new List<StorageFile> { file };
            _currentIndex = 0;

            await LoadAndPlayCurrentAsync();
        }

        private async void PickFolderMenuItem_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            RootSplitView.IsPaneOpen = false;

            var picker = new FolderPicker();
            // FolderPicker требует хотя бы один FileTypeFilter, даже для выбора
            // самой папки, а не конкретного файла внутри неё — особенность API.
            picker.FileTypeFilter.Add("*");

            StorageFolder folder;
            try
            {
                folder = await picker.PickSingleFolderAsync();
            }
            catch (Exception ex)
            {
                await new Windows.UI.Popups.MessageDialog(ex.ToString(), "Ошибка выбора папки").ShowAsync();
                return;
            }

            if (folder == null) return;

            IReadOnlyList<StorageFile> files;
            try
            {
                files = await folder.GetFilesAsync();
            }
            catch (Exception ex)
            {
                await new Windows.UI.Popups.MessageDialog(ex.ToString(), "Ошибка чтения папки").ShowAsync();
                return;
            }

            var tracks = files
                .Where(f => SupportedExtensions.Contains(System.IO.Path.GetExtension(f.Name).ToLowerInvariant()))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (tracks.Count == 0)
            {
                await new Windows.UI.Popups.MessageDialog(
                    "В выбранной папке не найдено поддерживаемых аудиофайлов (.mp3, .wav, .m4a).",
                    "Пусто").ShowAsync();
                return;
            }

            _playlist = tracks;
            _currentIndex = 0;

            await LoadAndPlayCurrentAsync();
        }

        // --- Переключение треков ---

        private async Task PlayNextTrackAsync()
        {
            if (_playlist.Count == 0) return;
            _currentIndex = (_currentIndex + 1) % _playlist.Count; // закольцовка
            await LoadAndPlayCurrentAsync();
        }

        private async Task PlayPreviousTrackAsync()
        {
            if (_playlist.Count == 0) return;
            _currentIndex = (_currentIndex - 1 + _playlist.Count) % _playlist.Count; // закольцовка
            await LoadAndPlayCurrentAsync();
        }

        private async Task LoadAndPlayCurrentAsync()
        {
            if (_currentIndex < 0 || _currentIndex >= _playlist.Count) return;

            try
            {
                await LoadTrackAsync(_playlist[_currentIndex]);
                _trackLoaded = true;
                _playback.Play();
            }
            catch (Exception ex)
            {
                await new Windows.UI.Popups.MessageDialog(ex.ToString(), "Ошибка загрузки трека").ShowAsync();
            }
        }

        /// <summary>
        /// Общая логика загрузки одного файла: чтение ID3-тегов, LoadAsync
        /// в PlaybackService, подключение визуализатора, обновление UI.
        /// Используется и при ручном выборе файла/папки, и при Next/Prev.
        /// </summary>
        private async Task LoadTrackAsync(StorageFile file)
        {
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
                throw new Exception("Чтение ID3-тегов (" + file.Name + "): " + ex.Message, ex);
            }

            try
            {
                await _playback.LoadAsync(file, title: title, artist: artist);

                TrackTitleText.Text = title;
                TrackArtistText.Text = artist;

                // Duration доступна сразу после LoadAsync — AudioFileInputNode
                // читает метаданные файла синхронно при создании узла.
                ProgressSlider.Maximum = _playback.Duration.TotalSeconds;
                DurationText.Text = FormatTime(_playback.Duration);
            }
            catch (Exception ex)
            {
                throw new Exception("_playback.LoadAsync (" + file.Name + "): " + ex.Message, ex);
            }

            try
            {
                _visualizer?.Dispose();
                _visualizer = new VisualizerService();
                _visualizer.AttachTo(_playback);
                _visualizer.LevelsChanged += OnLevelsChanged;
                // Отдельного Start()/Stop() у визуализатора нет — он просто
                // подключён вторым выходом к общему AudioGraph и получает кадры
                // ровно тогда, когда играет реальный звук (единственный
                // источник Start/Stop — _playback.Play()/Pause()).
            }
            catch (Exception ex)
            {
                throw new Exception("VisualizerService.AttachTo (" + file.Name + "): " + ex.Message, ex);
            }
        }

        private async void OnLevelsChanged(object sender, float[] bars)
        {
            // LevelsChanged прилетает из фонового потока — трогать элементы UI
            // (Rectangle.Height) можно только из UI-потока.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Реальная высота панели вместо захардкоженного числа — так
                // визуализатор всегда использует всё доступное место.
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
        // тап по треку, стрелки клавиатуры), в отличие от PointerPressed/Released.
        private void ProgressSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isProgrammaticSliderUpdate) return; // это мы сами обновили из таймера, не пользователь
            if (_playback == null) return;

            _playback.Position = TimeSpan.FromSeconds(e.NewValue);
        }

        private void PlayPauseButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (!_trackLoaded)
            {
                // Ничего не выбрано — подскажем открыть меню, а не просто
                // молча ничего не делать.
                RootSplitView.IsPaneOpen = true;
                return;
            }

            _playback.TogglePlayPause();
        }

        private async void PreviousButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            await PlayPreviousTrackAsync();
        }

        private async void NextButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            await PlayNextTrackAsync();
        }

        private void OnPlaybackStateChanged(object sender, bool isPlaying)
        {
            // Визуализатор отдельно не запускается/останавливается — он
            // подключён к общему AudioGraph, который уже сам стартует/стопится
            // внутри _playback.Play()/Pause().
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
