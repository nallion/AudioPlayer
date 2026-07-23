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
using AudioVisualizerPlayer.Helpers;
using AudioVisualizerPlayer.Models;
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
        private bool _mainPageLoadedOnce = false;
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
            // NavigationCacheMode="Enabled" переиспользует один и тот же экземпляр
            // страницы между переходами на PlaylistPage и обратно — Loaded может
            // сработать повторно (каждый раз, когда страница возвращается в
            // визуальное дерево), а конструктор только один раз. Без этой защиты
            // подписки на события ниже задвоились бы при каждом возврате.
            if (_mainPageLoadedOnce) return;
            _mainPageLoadedOnce = true;

            try
            {
                _playback = App.Playback;
                _playback.PlaybackStateChanged += OnPlaybackStateChanged;

                // Аппаратные/лок-скрин кнопки Next/Previous (через SMTC) идут
                // через те же самые методы, что и кнопки в UI — единая логика
                // переключения треков, а не два независимых пути.
                _playback.NextRequested += async (s, a) => await PlayNextTrackAsync();
                _playback.PreviousRequested += async (s, a) => await PlayPreviousTrackAsync();

                // Автопереход к следующему треку, когда текущий доиграл сам
                // (не пауза от пользователя). TrackEnded может прилететь не с
                // UI-потока — маршалим через Dispatcher, т.к. дальше внутри
                // будет трогаться UI (TrackTitleText и т.п.).
                _playback.TrackEnded += async (s, a) =>
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                    {
                        // Только если реально есть плейлист из нескольких треков —
                        // одиночный файл просто останавливается по окончании.
                        if (App.CurrentPlaylist.Count > 1)
                        {
                            await PlayNextTrackAsync();
                        }
                    });
                };

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

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Diag.Log("MainPage.OnNavigatedTo: начало");

            _positionTimer?.Start();

            // Возврат со страницы плейлиста с выбранным треком
            if (App.RequestedPlaylistIndex.HasValue)
            {
                int index = App.RequestedPlaylistIndex.Value;
                App.RequestedPlaylistIndex = null;
                Diag.Log($"MainPage.OnNavigatedTo: RequestedPlaylistIndex={index}");

                if (index >= 0 && index < App.CurrentPlaylist.Count)
                {
                    App.CurrentPlaylistIndex = index;
                    Diag.Log("MainPage.OnNavigatedTo: перед LoadAndPlayCurrentAsync()");
                    await LoadAndPlayCurrentAsync();
                    Diag.Log("MainPage.OnNavigatedTo: после LoadAndPlayCurrentAsync() — готово");
                }
                return;
            }
            Diag.Log("MainPage.OnNavigatedTo: RequestedPlaylistIndex не установлен, обычный возврат");

            // Просто возврат на страницу (без выбора трека) — если трек уже
            // играет, визуализатор был освобождён в OnNavigatedFrom и его
            // нужно подключить заново к тому же (всё ещё играющему) графу.
            if (_trackLoaded && _visualizer == null)
            {
                try
                {
                    _visualizer = new VisualizerService();
                    _visualizer.AttachTo(_playback);
                    _visualizer.LevelsChanged += OnLevelsChanged;
                }
                catch
                {
                    // Не критично — просто не будет визуализации, звук не пострадает.
                }
            }
        }

        private int _tickCounter = 0;

        private void PositionTimer_Tick(object sender, object e)
        {
            // Heartbeat: если эта строка продолжает писаться даже во время
            // "зависшего" интерфейса — значит UI-поток на самом деле жив и
            // диспетчер разгребает очередь, просто что-то на уровне
            // ввода/визуального дерева перестало пропускать касания. Если
            // heartbeat тоже останавливается ровно в момент зависания —
            // значит, это настоящий deadlock UI-потока.
            _tickCounter++;
            if (_tickCounter % 10 == 0) // раз в ~5 секунд, не заваливаем лог
            {
                Diag.Log($"HEARTBEAT: тик #{_tickCounter}, UI-поток жив, _trackLoaded={_trackLoaded}");
            }

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

        private async void PlaylistMenuItem_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            RootSplitView.IsPaneOpen = false;

            // Кандидат-фикс на проверку: даём анимации закрытия SplitView
            // время доиграть до конца, прежде чем навигировать прочь со
            // страницы — если её прервать на середине переходом Frame.Navigate,
            // невидимый light-dismiss слой Overlay-режима теоретически может
            // остаться "зависшим" в визуальном дереве и перехватывать
            // дальнейший ввод даже после возврата на страницу.
            await Task.Delay(250);

            Frame.Navigate(typeof(PlaylistPage));
        }

        private void ShowPlaylistMenuItemIfNeeded()
        {
            PlaylistMenuItem.Visibility = App.CurrentPlaylist.Count > 0
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;
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

            PlaylistItem item;
            try
            {
                item = await BuildPlaylistItemAsync(file);
            }
            catch (Exception ex)
            {
                await new Windows.UI.Popups.MessageDialog(ex.ToString(), "Ошибка чтения тегов").ShowAsync();
                return;
            }

            App.CurrentPlaylist.Clear();
            App.CurrentPlaylist.Add(item);
            App.CurrentPlaylistIndex = 0;
            ShowPlaylistMenuItemIfNeeded();

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

            var matchedFiles = files
                .Where(f => SupportedExtensions.Contains(System.IO.Path.GetExtension(f.Name).ToLowerInvariant()))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchedFiles.Count == 0)
            {
                await new Windows.UI.Popups.MessageDialog(
                    "В выбранной папке не найдено поддерживаемых аудиофайлов (.mp3, .wav, .m4a).",
                    "Пусто").ShowAsync();
                return;
            }

            // Теги читаем один раз здесь при построении плейлиста, а не заново
            // при каждом Next/Prev.
            var items = new List<PlaylistItem>();
            foreach (var f in matchedFiles)
            {
                try
                {
                    items.Add(await BuildPlaylistItemAsync(f));
                }
                catch
                {
                    // Один битый файл не должен ронять построение всего плейлиста —
                    // просто пропускаем его.
                }
            }

            if (items.Count == 0)
            {
                await new Windows.UI.Popups.MessageDialog(
                    "Не удалось прочитать ни один из найденных файлов.", "Ошибка").ShowAsync();
                return;
            }

            App.CurrentPlaylist.Clear();
            App.CurrentPlaylist.AddRange(items);
            App.CurrentPlaylistIndex = 0;
            ShowPlaylistMenuItemIfNeeded();

            await LoadAndPlayCurrentAsync();
        }

        private static async Task<PlaylistItem> BuildPlaylistItemAsync(StorageFile file)
        {
            // Стандартный, встроенный в Windows способ чтения ID3-тегов в UWP —
            // без сторонних библиотек, свойства читаются системным индексатором.
            var musicProps = await file.Properties.GetMusicPropertiesAsync();

            string title = string.IsNullOrWhiteSpace(musicProps.Title) ? file.DisplayName : musicProps.Title;
            string artist = string.IsNullOrWhiteSpace(musicProps.Artist) ? file.DisplayName : musicProps.Artist;

            return new PlaylistItem(file, title, artist);
        }

        // --- Переключение треков ---

        private async Task PlayNextTrackAsync()
        {
            if (App.CurrentPlaylist.Count == 0) return;
            App.CurrentPlaylistIndex = (App.CurrentPlaylistIndex + 1) % App.CurrentPlaylist.Count; // закольцовка
            await LoadAndPlayCurrentAsync();
        }

        private async Task PlayPreviousTrackAsync()
        {
            if (App.CurrentPlaylist.Count == 0) return;
            App.CurrentPlaylistIndex = (App.CurrentPlaylistIndex - 1 + App.CurrentPlaylist.Count) % App.CurrentPlaylist.Count; // закольцовка
            await LoadAndPlayCurrentAsync();
        }

        private async Task LoadAndPlayCurrentAsync()
        {
            Diag.Log($"LoadAndPlayCurrentAsync: начало, CurrentPlaylistIndex={App.CurrentPlaylistIndex}, Count={App.CurrentPlaylist.Count}");
            if (App.CurrentPlaylistIndex < 0 || App.CurrentPlaylistIndex >= App.CurrentPlaylist.Count) return;

            try
            {
                Diag.Log("LoadAndPlayCurrentAsync: перед LoadTrackAsync");
                await LoadTrackAsync(App.CurrentPlaylist[App.CurrentPlaylistIndex]);
                Diag.Log("LoadAndPlayCurrentAsync: после LoadTrackAsync, перед _playback.Play()");
                _trackLoaded = true;
                _playback.Play();
                Diag.Log("LoadAndPlayCurrentAsync: после _playback.Play() — готово");
            }
            catch (Exception ex)
            {
                Diag.Log("LoadAndPlayCurrentAsync: ИСКЛЮЧЕНИЕ: " + ex);
                await new Windows.UI.Popups.MessageDialog(ex.ToString(), "Ошибка загрузки трека").ShowAsync();
            }
        }

        /// <summary>
        /// Общая логика загрузки одного трека: LoadAsync в PlaybackService,
        /// подключение визуализатора, обновление UI. Title/Artist уже
        /// прочитаны заранее в BuildPlaylistItemAsync — здесь не перечитываем.
        /// </summary>
        private async Task LoadTrackAsync(PlaylistItem item)
        {
            Diag.Log($"LoadTrackAsync: начало, файл={item.File.Name}");

            // ВАЖНО: старый VisualizerService нужно освободить ДО вызова
            // _playback.LoadAsync — тот внутри себя вызывает DisposeGraph()
            // и сносит старый AudioGraph/AudioFileInputNode ПЕРВЫМ делом при
            // загрузке нового трека. Если освобождать визуализатор ПОСЛЕ
            // LoadAsync, Detach() пытается отписаться от графа, который
            // PlaybackService уже уничтожил мгновением раньше —
            // отсюда ObjectDisposedException при переключении треков.
            Diag.Log("LoadTrackAsync: перед _visualizer?.Dispose()");
            _visualizer?.Dispose();
            _visualizer = null;
            Diag.Log("LoadTrackAsync: после _visualizer?.Dispose()");

            try
            {
                Diag.Log("LoadTrackAsync: перед _playback.LoadAsync");
                await _playback.LoadAsync(item.File, title: item.Title, artist: item.Artist);
                Diag.Log("LoadTrackAsync: после _playback.LoadAsync");

                TrackTitleText.Text = item.Title;
                TrackArtistText.Text = item.Artist;

                // Duration доступна сразу после LoadAsync — AudioFileInputNode
                // читает метаданные файла синхронно при создании узла.
                ProgressSlider.Maximum = _playback.Duration.TotalSeconds;
                DurationText.Text = FormatTime(_playback.Duration);
                Diag.Log("LoadTrackAsync: UI (title/artist/duration) обновлён");
            }
            catch (Exception ex)
            {
                Diag.Log("LoadTrackAsync: ИСКЛЮЧЕНИЕ в _playback.LoadAsync: " + ex);
                throw new Exception("_playback.LoadAsync (" + item.File.Name + "): " + ex.Message, ex);
            }

            try
            {
                Diag.Log("LoadTrackAsync: перед new VisualizerService()/AttachTo");
                _visualizer = new VisualizerService();
                _visualizer.AttachTo(_playback);
                _visualizer.LevelsChanged += OnLevelsChanged;
                Diag.Log("LoadTrackAsync: после AttachTo — готово");
                // Отдельного Start()/Stop() у визуализатора нет — он просто
                // подключён вторым выходом к общему AudioGraph и получает кадры
                // ровно тогда, когда играет реальный звук (единственный
                // источник Start/Stop — _playback.Play()/Pause()).
            }
            catch (Exception ex)
            {
                Diag.Log("LoadTrackAsync: ИСКЛЮЧЕНИЕ в VisualizerService.AttachTo: " + ex);
                throw new Exception("VisualizerService.AttachTo (" + item.File.Name + "): " + ex.Message, ex);
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
            // визуализация не нужна, пока не видно MainPage (например, ушли
            // на PlaylistPage). Звук продолжит играть через _playback,
            // который живёт в App. При возврате (OnNavigatedTo) подключаем
            // визуализатор заново.
            _visualizer?.Dispose();
            _visualizer = null;
        }
    }
}
