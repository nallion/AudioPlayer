using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
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

        // ВРЕМЕННЫЙ диагностический флаг — проверяем, трещит ли звук вообще
        // без визуализатора (ни FFT, ни фоновых Task.Run, только голое
        // воспроизведение через Submix -> DeviceOutput). После теста либо
        // вернуть в false, либо убрать совсем.
        private const bool DisableVisualizerForDiagnostics = true;

        // ВРЕМЕННЫЙ диагностический флаг — проверяем, не даёт ли щелчки сам
        // DispatcherTimer (PositionTimer_Tick, читает _fileInput.Position и
        // обновляет UI каждые 500мс). Визуализатор уже исключили — идём по
        // списку дальше.
        private const bool DisablePositionTimerForDiagnostics = true;
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

                // Экран заблокирован/приложение свёрнуто — визуализатор
                // никто не видит, а его фоновый FFT (Task.Run) продолжает
                // грузить CPU в урезанном фоновом состоянии процесса, что
                // может провоцировать подглючивания звука именно в момент
                // блокировки. Отключаем визуализатор на это время и
                // подключаем заново при возврате — звук не затрагивается,
                // он играет отдельно от визуализатора.
                App.EnteredBackground += (s, a) =>
                {
                    // Раньше здесь был _visualizer?.Dispose() — то есть
                    // удаление живого соединения Submix->FrameOutput прямо
                    // из играющего графа. Судя по всему, именно это давало
                    // гарантированные щелчки на блокировке экрана — само
                    // соединение не самое стабильное место на этой платформе,
                    // трогать его на живом графе лишний раз не стоит. Теперь
                    // только флаг — топология графа не меняется вообще.
                    if (_visualizer != null) _visualizer.IsPaused = true;
                };
                App.LeavingBackground += (s, a) =>
                {
                    if (_visualizer != null) _visualizer.IsPaused = false;
                };

                // Позиция и длительность — раз в 500мс опрашиваем
                // AudioFileInputNode.Position через PlaybackService.
                _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _positionTimer.Tick += PositionTimer_Tick;
                if (!DisablePositionTimerForDiagnostics)
                {
                    _positionTimer.Start();
                }

                // Автоопределение наушников на этом устройстве ненадёжно —
                // список устройств вывода заполняем один раз при старте,
                // выбор пользователя применяется вручную (см. OutputDeviceComboBox_SelectionChanged).
                _ = PopulateOutputDevicesAsync();
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

            if (!DisablePositionTimerForDiagnostics)
            {
                _positionTimer?.Start();
            }

            // Возврат со страницы плейлиста с выбранным треком
            if (App.RequestedPlaylistIndex.HasValue)
            {
                int index = App.RequestedPlaylistIndex.Value;
                App.RequestedPlaylistIndex = null;

                if (index >= 0 && index < App.CurrentPlaylist.Count)
                {
                    App.CurrentPlaylistIndex = index;
                    await LoadAndPlayCurrentAsync();
                }
                return;
            }

            // Просто возврат на страницу (без выбора трека) — если трек уже
            // играет, визуализатор был освобождён в OnNavigatedFrom и его
            // нужно подключить заново к тому же (всё ещё играющему) графу.
            if (!DisableVisualizerForDiagnostics && _trackLoaded && _visualizer == null)
            {
                try
                {
                    _visualizer = new VisualizerService();
                    await _visualizer.AttachToAsync(_playback);
                    _visualizer.LevelsChanged += OnLevelsChanged;
                }
                catch
                {
                    // Не критично — просто не будет визуализации, звук не пострадает.
                }
            }

            // Бегущая строка тоже была остановлена в OnNavigatedFrom — заводим заново.
            if (_trackLoaded)
            {
                StartTitleMarqueeIfNeeded();
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

        // --- Бегущая строка для названия трека ---

        private Storyboard _titleMarqueeStoryboard;

        private void TitleClipContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // UWP не обрезает контент, вышедший за пределы панели через
            // RenderTransform, автоматически — нужен явный Clip по размеру
            // самого контейнера.
            TitleClipContainer.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };

            // Пересчитываем бегущую строку при изменении размера контейнера
            // (например, поворот экрана) — не только при смене трека.
            StartTitleMarqueeIfNeeded();
        }

        private void StartTitleMarqueeIfNeeded()
        {
            _titleMarqueeStoryboard?.Stop();
            _titleMarqueeStoryboard = null;

            double containerWidth = TitleClipContainer.ActualWidth;
            if (containerWidth <= 0) return; // ещё не посчитан layout — SizeChanged вызовет нас снова

            TrackTitleText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = TrackTitleText.DesiredSize.Width;

            double overflow = textWidth - containerWidth;

            if (overflow <= 0)
            {
                // Помещается целиком — прячем вторую копию и разделитель,
                // просто центрируем единственную копию через transform.
                TitleMarqueeGap.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                TrackTitleText2.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                TitleMarqueeTransform.X = (containerWidth - textWidth) / 2.0;
                return;
            }

            // Не помещается — настоящая кольцевая прокрутка: показываем вторую
            // копию текста через разделитель (TitleMarqueeGap), и двигаем ВЕСЬ
            // StackPanel (обе копии сразу) равномерно влево на одно "звено"
            // цикла = textWidth + ширина разделителя. Ровно в момент, когда
            // первая копия целиком уезжает за левый край, вторая копия как раз
            // оказывается там, где была первая изначально — поэтому сброс
            // анимации в начало цикла не заметен глазу, и получается иллюзия
            // бесконечно ползущей ленты (текст "заползает" за левый край и
            // "выползает" из-за правого).
            TitleMarqueeGap.Visibility = Windows.UI.Xaml.Visibility.Visible;
            TrackTitleText2.Visibility = Windows.UI.Xaml.Visibility.Visible;

            double gapWidth = TitleMarqueeGap.Width;
            double period = textWidth + gapWidth; // одно "звено" цикла

            const double pixelsPerSecond = 40.0;
            double durationSeconds = period / pixelsPerSecond;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = -period,
                Duration = new Duration(TimeSpan.FromSeconds(durationSeconds)),
                // Без Easing — постоянная скорость, как у настоящей бегущей ленты.
            };
            Storyboard.SetTarget(animation, TitleMarqueeTransform);
            Storyboard.SetTargetProperty(animation, "X");

            _titleMarqueeStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            _titleMarqueeStoryboard.Children.Add(animation);
            _titleMarqueeStoryboard.Begin();
        }

        // --- Боковое меню ---

        private void MenuButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            RootSplitView.IsPaneOpen = !RootSplitView.IsPaneOpen;
        }

        // --- Ручной выбор устройства вывода звука ---

        private class OutputDeviceOption
        {
            public string Name { get; set; }
            public DeviceInformation Device { get; set; } // null = "Авто" (системное значение по умолчанию)
        }

        private bool _populatingOutputDevices = false;
        private const string SelectedOutputDeviceIdSettingKey = "SelectedOutputDeviceId";

        private async Task PopulateOutputDevicesAsync()
        {
            _populatingOutputDevices = true; // не реагируем на программное заполнение как на выбор пользователя
            try
            {
                var options = new List<OutputDeviceOption>
                {
                    new OutputDeviceOption { Name = "Авто (системное значение по умолчанию)", Device = null }
                };

                try
                {
                    var devices = await DeviceInformation.FindAllAsync(
                        Windows.Media.Devices.MediaDevice.GetAudioRenderSelector());
                    foreach (var d in devices)
                    {
                        options.Add(new OutputDeviceOption { Name = d.Name, Device = d });
                    }
                }
                catch (Exception ex)
                {
                    Diag.Log("Не удалось перечислить устройства вывода: " + ex);
                }

                OutputDeviceComboBox.ItemsSource = options;

                // Восстанавливаем ранее сохранённый выбор — переживает
                // перезапуск приложения (ApplicationData.LocalSettings).
                int restoredIndex = 0; // по умолчанию — "Авто"
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SelectedOutputDeviceIdSettingKey, out object savedIdObj)
                    && savedIdObj is string savedId && !string.IsNullOrEmpty(savedId))
                {
                    int foundIndex = options.FindIndex(o => o.Device != null && o.Device.Id == savedId);
                    if (foundIndex >= 0)
                    {
                        restoredIndex = foundIndex;
                    }
                    else
                    {
                        Diag.Log($"Сохранённое устройство вывода (id={savedId}) сейчас не найдено в системе — используем Авто.");
                    }
                }

                OutputDeviceComboBox.SelectedIndex = restoredIndex;
                // Применяем восстановленный выбор к PlaybackService напрямую —
                // SelectionChanged не сработает содержательно, пока
                // _populatingOutputDevices == true (см. обработчик ниже).
                if (_playback != null)
                {
                    _playback.SelectedRenderDevice = options[restoredIndex].Device;
                }
            }
            finally
            {
                _populatingOutputDevices = false;
            }
        }

        private async void OutputDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_populatingOutputDevices) return; // это мы сами заполнили список, не пользователь выбрал
            if (_playback == null) return;

            var selected = OutputDeviceComboBox.SelectedItem as OutputDeviceOption;
            if (selected == null) return;

            _playback.SelectedRenderDevice = selected.Device;
            Diag.Log($"Пользователь выбрал устройство вывода: {selected.Name}");

            // Сохраняем выбор — переживёт перезапуск приложения. "Авто"
            // (Device == null) сохраняем как отсутствие значения / пустую
            // строку, чтобы при следующем запуске корректно восстановилось
            // именно "Авто", а не последнее реальное устройство.
            ApplicationData.Current.LocalSettings.Values[SelectedOutputDeviceIdSettingKey] =
                selected.Device?.Id ?? string.Empty;

            // Применяем выбор сразу — если что-то уже загружено, перегружаем
            // текущий трек с новым устройством и продолжаем с той же позиции.
            if (_trackLoaded && App.CurrentPlaylistIndex >= 0 && App.CurrentPlaylistIndex < App.CurrentPlaylist.Count)
            {
                var wasPlaying = _playback.IsPlaying;
                var previousPosition = _playback.Position;

                try
                {
                    await LoadTrackAsync(App.CurrentPlaylist[App.CurrentPlaylistIndex]);
                    _playback.Position = previousPosition;
                    if (wasPlaying) _playback.Play();
                }
                catch (Exception ex)
                {
                    await new Windows.UI.Popups.MessageDialog(ex.ToString(), "Не удалось применить устройство вывода").ShowAsync();
                }
            }
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

        private async void EqualizerMenuItem_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            RootSplitView.IsPaneOpen = false;

            // Та же задержка перед навигацией, что и для PlaylistMenuItem —
            // без неё анимация закрытия SplitView может не успеть доиграть,
            // и это ловит ввод на возврате (см. историю бага с зависанием).
            await Task.Delay(250);

            Frame.Navigate(typeof(EqualizerPage));
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
            if (App.CurrentPlaylistIndex < 0 || App.CurrentPlaylistIndex >= App.CurrentPlaylist.Count) return;

            Diag.Log($"LoadAndPlayCurrentAsync: индекс {App.CurrentPlaylistIndex}, файл {App.CurrentPlaylist[App.CurrentPlaylistIndex].File.Name}");
            try
            {
                await LoadTrackAsync(App.CurrentPlaylist[App.CurrentPlaylistIndex]);
                _trackLoaded = true;
                _playback.Play();
                Diag.Log("LoadAndPlayCurrentAsync: Play() вызван успешно");
            }
            catch (Exception ex)
            {
                Diag.Log("LoadAndPlayCurrentAsync: ИСКЛЮЧЕНИЕ (звук тоже не запустится): " + ex);
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

            // ВАЖНО: старый VisualizerService нужно освободить ДО вызова
            // _playback.LoadAsync — тот внутри себя вызывает DisposeGraph()
            // и сносит старый AudioGraph/AudioFileInputNode ПЕРВЫМ делом при
            // загрузке нового трека. Если освобождать визуализатор ПОСЛЕ
            // LoadAsync, Detach() пытается отписаться от графа, который
            // PlaybackService уже уничтожил мгновением раньше —
            // отсюда ObjectDisposedException при переключении треков.
            _visualizer?.Dispose();
            _visualizer = null;

            try
            {
                await _playback.LoadAsync(item.File, title: item.Title, artist: item.Artist);

                TrackTitleText.Text = item.Title;
                TrackTitleText2.Text = item.Title; // вторая копия для кольцевой бегущей строки
                TrackArtistText.Text = item.Artist;
                StartTitleMarqueeIfNeeded();

                // Duration доступна сразу после LoadAsync — AudioFileInputNode
                // читает метаданные файла синхронно при создании узла.
                ProgressSlider.Maximum = _playback.Duration.TotalSeconds;
                DurationText.Text = FormatTime(_playback.Duration);
            }
            catch (Exception ex)
            {
                throw new Exception("_playback.LoadAsync (" + item.File.Name + "): " + ex.Message, ex);
            }

            try
            {
                if (!DisableVisualizerForDiagnostics)
                {
                    _visualizer = new VisualizerService();
                    await _visualizer.AttachToAsync(_playback);
                    _visualizer.LevelsChanged += OnLevelsChanged;
                }
                // Отдельного Start()/Stop() у визуализатора нет — он просто
                // подключён вторым выходом к общему AudioGraph и получает кадры
                // ровно тогда, когда играет реальный звук (единственный
                // источник Start/Stop — _playback.Play()/Pause()).
            }
            catch (Exception ex)
            {
                // Визуализация — не критичная часть воспроизведения: если она
                // не подключилась, просто логируем и продолжаем — звук должен
                // заиграть сам, без ручного повторного нажатия. Пересборка с
                // явным форматом больше не нужна — LoadAsync теперь всегда
                // использует явные 48000Гц с самого начала.
                Diag.Log("VisualizerService.AttachTo провалился, звук продолжит играть без визуализации: " + ex);
                _visualizer?.Dispose();
                _visualizer = null;
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

        // Дебаунс перемотки: ValueChanged срабатывает много раз в секунду во
        // время протягивания пальцем по слайдеру. Раньше каждый такой тик сразу
        // слал реальный _fileInput.Seek() — десятки быстрых Seek() подряд за
        // один жест сбивали декодер mp3 в переходное состояние (тишина и в
        // динамиках, и в визуализаторе, хотя позиция продолжала расти — decoder
        // "завис", а логическое время в графе — нет). Теперь копим последнее
        // желаемое значение и реально сикаем только когда движение утихло на
        // ~200мс — то есть один настоящий Seek() на весь жест, а не на каждый
        // его промежуточный кадр.
        private DispatcherTimer _seekDebounceTimer;
        private double _pendingSeekSeconds;

        private void EnsureSeekDebounceTimer()
        {
            if (_seekDebounceTimer != null) return;

            _seekDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _seekDebounceTimer.Tick += (s, e) =>
            {
                _seekDebounceTimer.Stop();
                if (_playback != null)
                {
                    _playback.Position = TimeSpan.FromSeconds(_pendingSeekSeconds);
                }
            };
        }

        // ValueChanged — единственное событие Slider, которое гарантированно
        // срабатывает при ЛЮБОМ изменении значения (перетаскивание пальцем,
        // тап по треку, стрелки клавиатуры), в отличие от PointerPressed/Released.
        private void ProgressSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_isProgrammaticSliderUpdate) return; // это мы сами обновили из таймера, не пользователь
            if (_playback == null) return;

            EnsureSeekDebounceTimer();
            _pendingSeekSeconds = e.NewValue;

            // Каждый новый тик сбрасывает таймер заново — реальный Seek()
            // произойдёт только через 200мс ПОСЛЕ того, как значения
            // перестанут меняться (палец остановился/отпущен).
            _seekDebounceTimer.Stop();
            _seekDebounceTimer.Start();
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

        private void LoopButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            if (_playback == null) return;

            _playback.LoopCurrentTrack = !_playback.LoopCurrentTrack;
            LoopIcon.Foreground = _playback.LoopCurrentTrack
                ? (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["AppAccentBrush"]
                : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White);
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
            _titleMarqueeStoryboard?.Stop();
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
