using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Effects;
using Windows.Media.Render;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AudioVisualizerPlayer.Services
{
    /// <summary>
    /// Единый AudioGraph на воспроизведение звука. Раньше звук игрался через
    /// Windows.Media.Playback.MediaPlayer, а анализ спектра — через отдельный,
    /// независимый AudioGraph, читающий тот же файл заново. Это давало дрейф
    /// по времени между звуком и визуализацией (два независимых чтения одного
    /// файла со своим таймингом каждое). Теперь один AudioGraph и один
    /// AudioFileInputNode.
    ///
    /// ВАЖНО: AudioFileInputNode.AddOutgoingConnection() напрямую в ДВА узла
    /// одновременно (DeviceOutput + FrameOutput для анализа) бросает
    /// XAUDIO2_E_INVALID_CALL (0x88960001) на этом устройстве — похоже, узел
    /// чтения сжатого файла не поддерживает больше одного исходящего
    /// соединения надёжно. Раньше решением был промежуточный AudioSubmixNode
    /// (FileInput → Submix → DeviceOutput + FrameOutput), но потом
    /// обнаружились редкие, не до конца объяснённые щелчки в звуке, не
    /// связанные ни с визуализатором, ни с эквалайзером, ни с размером
    /// буфера — подозрение упало на сам Submix. ТЕКУЩЕЕ СОСТОЯНИЕ
    /// (диагностика): Submix временно убран из пути ПОЛНОСТЬЮ — FileInput
    /// подключается к DeviceOutput НАПРЯМУЮ (одно соединение, без узла
    /// разветвления), эквалайзер перенесён на FileInput (EffectDefinitions
    /// есть не только у Submix). Пока в таком виде визуализатор работать не
    /// может (второй выход убран) — он и так отключён диагностическим
    /// флагом в MainPage на время этого теста.
    ///
    /// MediaPlayer убран — вместе с ним ушли его автоматические
    /// SystemMediaTransportControls и MediaPlaybackSession.PositionChanged.
    /// Оба теперь реализованы вручную: SMTC заводится напрямую через
    /// SystemMediaTransportControls.GetForCurrentView(), а позиция для
    /// прогресс-бара читается через AudioFileInputNode.Position по таймеру
    /// (см. MainPage) вместо push-события.
    /// </summary>
    public class PlaybackService
    {
        private AudioGraph _graph;
        private AudioFileInputNode _fileInput;
        private AudioSubmixNode _submix;
        private AudioDeviceOutputNode _deviceOutput;
        private EqualizerEffectDefinition _equalizer;
        private EqualizerBand[] _equalizerBands;

        /// <summary>
        /// Частоты 4 полос классического графического эквалайзера — именно
        /// столько EqualizerEffectDefinition даёт по умолчанию (проверено по
        /// логам: Bands.Count == 4, как и в официальном примере Microsoft).
        /// Пятая полоса была лишней — ничем не управляла, слайдер для неё
        /// просто ничего не делал.
        /// </summary>
        public static readonly double[] EqualizerFrequencies = { 60, 250, 1000, 4000 };
        // Bandwidth в этом API — не герцы, а октавы (см. официальный пример
        // Microsoft: значения вроде 1.5/2.0). 1.0 октава — стандартный,
        // безопасный выбор для графического эквалайзера.
        private static readonly double[] EqualizerBandwidths = { 1.0, 1.0, 1.0, 1.2 };

        public SystemMediaTransportControls Smtc { get; }

        /// <summary>true — сейчас играет, false — на паузе/остановлено.</summary>
        public event EventHandler<bool> PlaybackStateChanged;

        public bool IsPlaying { get; private set; }

        /// <summary>
        /// Доступ к общему графу и submix-узлу — VisualizerService подключает
        /// свой FrameOutputNode вторым исходящим соединением ОТ SUBMIX-узла
        /// (не от FileInput напрямую — см. комментарий к классу про
        /// XAUDIO2_E_INVALID_CALL).
        /// </summary>
        public AudioGraph Graph => _graph;
        public AudioSubmixNode Submix => _submix;

        /// <summary>
        /// Меняет громкость полосы эквалайзера в реальном времени, без
        /// пересборки графа — EqualizerBand.Gain можно менять на лету, это
        /// как раз одно из удобств встроенного EqualizerEffectDefinition.
        /// Не сохраняет в LocalSettings сама — это делает вызывающий код
        /// (см. EqualizerPage), здесь только применение к текущему графу.
        /// </summary>
        public void SetEqualizerGain(int bandIndex, double gainDb)
        {
            if (_equalizerBands != null && bandIndex >= 0 && bandIndex < _equalizerBands.Length)
            {
                _equalizerBands[bandIndex].Gain = DbToLinearGain(gainDb);
                AudioVisualizerPlayer.Helpers.Diag.Log($"SetEqualizerGain: полоса {bandIndex} -> {gainDb} дБ (линейно {_equalizerBands[bandIndex].Gain}), применено");
            }
            else
            {
                AudioVisualizerPlayer.Helpers.Diag.Log($"SetEqualizerGain: полоса {bandIndex} -> {gainDb} дБ, НЕ применено — _equalizerBands == null: {_equalizerBands == null} (трек ещё не загружен?)");
            }
        }

        /// <summary>
        /// EqualizerBand.Gain — не децибелы, а линейный коэффициент усиления
        /// (1.0 = без изменений, а не 0.0 — именно поэтому Gain=0 бросал
        /// ArgumentException). UI/сохранённые настройки остаются в привычных
        /// дБ, конвертация происходит только здесь, в точке применения к
        /// реальному API.
        /// </summary>
        private static float DbToLinearGain(double db) => (float)Math.Pow(10.0, db / 20.0);

        /// <summary>
        /// Ручной выбор устройства вывода пользователем (см. MainPage,
        /// OutputDeviceComboBox) — null означает "авто" (системное значение
        /// по умолчанию, с динамическим автослежением). Конкретное устройство
        /// жёстко закрепляет граф за ним (PrimaryRenderDevice), теряя
        /// автослежение — но это осознанный выбор пользователя, а не
        /// случайный побочный эффект, поэтому здесь уместно в отличие от
        /// автоматической установки этого свойства.
        /// </summary>
        public DeviceInformation SelectedRenderDevice { get; set; }

        public TimeSpan Position
        {
            get => _fileInput?.Position ?? TimeSpan.Zero;
            set { if (_fileInput != null) _fileInput.Seek(value); }
        }

        public TimeSpan Duration => _fileInput?.Duration ?? TimeSpan.Zero;

        /// <summary>Файл доиграл до конца сам (не пауза от пользователя) — сюда
        /// подписывается MainPage для автоперехода к следующему треку.</summary>
        public event EventHandler TrackEnded;

        /// <summary>
        /// Бесконечное зацикленное воспроизведение ТЕКУЩЕГО трека — если
        /// true, по окончании файла он просто перематывается в начало и
        /// играет заново вместо перехода к следующему треку в плейлисте.
        /// </summary>
        public bool LoopCurrentTrack { get; set; }

        public PlaybackService()
        {
            Smtc = SystemMediaTransportControls.GetForCurrentView();
            Smtc.IsPlayEnabled = true;
            Smtc.IsPauseEnabled = true;
            Smtc.IsNextEnabled = true;
            Smtc.IsPreviousEnabled = true;
            Smtc.ButtonPressed += OnSmtcButtonPressed;
        }

        private static int _graphCreationCount = 0;

        /// <summary>
        /// Загружает трек: создаёт AudioGraph, файловый узел и узел вывода
        /// на динамики, соединяет их, и обновляет метаданные для лок-скрина.
        /// По умолчанию всегда используется явный PCM 48000Гц — на этом
        /// устройстве нативная частота DAC, судя по всему, именно 48000
        /// (щелчки в звуке при 44100), и явный формат также нужен, чтобы
        /// VisualizerService мог стабильно подключиться вторым соединением
        /// к Submix при определённых устройствах вывода (наушники). Раньше
        /// сначала пробовали "авто" ради автослежения за устройством, но
        /// раз пользователь теперь может выбрать устройство вывода вручную
        /// (см. MainPage.OutputDeviceComboBox), автослежение уже не так
        /// критично — стабильность важнее.
        /// </summary>
        public async Task LoadAsync(StorageFile file, string title, string artist, StorageFile albumArt = null, int? forceSampleRate = 48000)
        {
            AudioVisualizerPlayer.Helpers.Diag.Log($"LoadAsync начат для файла: {file.Name}, forceSampleRate={(forceSampleRate?.ToString() ?? "авто")}");

            // Освобождаем предыдущий граф, если уже что-то играло
            DisposeGraph();
            AudioVisualizerPlayer.Helpers.Diag.Log("Старый граф освобождён (если был)");

            try
            {
                await BuildGraphAsync(file, forceSampleRate);
                AudioVisualizerPlayer.Helpers.Diag.Log("BuildGraphAsync — УСПЕХ");
            }
            catch (Exception ex)
            {
                AudioVisualizerPlayer.Helpers.Diag.Log("BuildGraphAsync — ОШИБКА: " + ex);
                DisposeGraph();
                throw new InvalidOperationException(
                    $"Не удалось создать аудио-граф (граф-попытки за сессию: {_graphCreationCount}): " + ex.Message, ex);
            }

            _fileInput.FileCompleted += (s, a) =>
            {
                if (LoopCurrentTrack)
                {
                    // Просто перематываем в начало и продолжаем — трек не
                    // "закончился" с точки зрения плеера, TrackEnded не
                    // стреляет, автопереход к следующему треку не запускается.
                    _fileInput.Seek(TimeSpan.Zero);
                    return;
                }

                IsPlaying = false;
                PlaybackStateChanged?.Invoke(this, false);
                TrackEnded?.Invoke(this, EventArgs.Empty);
            };

            var updater = Smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;

            if (albumArt != null)
            {
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(albumArt);
            }

            updater.Update();
            AudioVisualizerPlayer.Helpers.Diag.Log("LoadAsync завершён успешно (SMTC обновлён)");
        }

        /// <summary>
        /// Собирает граф целиком: AudioGraph → FileInput → Submix → DeviceOutput.
        /// sampleRate == null — обычный путь, авто-согласование формата
        /// (сохраняет следование за устройством по умолчанию). Значение —
        /// явный PCM нужной частоты/2 канала/16 бит, как запасной вариант.
        /// </summary>
        private async Task BuildGraphAsync(StorageFile file, int? sampleRate)
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            if (sampleRate.HasValue)
            {
                settings.EncodingProperties = Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm((uint)sampleRate.Value, 2, 16);
            }

            // Пробовали явно задать QuantumSizeSelectionMode.ClosestToDesired +
            // DesiredSamplesPerQuantum=960 (больший квант, больше запаса) —
            // ОТКАЧЕНО: на практике щелчки стали ЧАЩЕ, а не реже (проверено
            // и на проводных наушниках, и на динамике — не Bluetooth-специфично).
            // Похоже, родной аудио-стек этого устройства ожидает буфер
            // конкретного размера, и наше "почти как хотим" вынуждало его
            // на дополнительное согласование — само по себе источник щелчков.
            // Оставляем системное значение по умолчанию (ничего не задаём).

            // PrimaryRenderDevice намеренно НЕ задаём автоматически: это
            // свойство жёстко привязывает граф к конкретному устройству
            // НАВСЕГДА, отключая динамическое автослежение — даже если
            // присвоить именно то устройство, которое сейчас и так является
            // дефолтным. Пробовали задавать его автоматически на основе
            // "текущего устройства по умолчанию" — это сломало автослежение
            // вообще во всех случаях. НО если пользователь ЯВНО выбрал
            // конкретное устройство вручную (SelectedRenderDevice, см.
            // MainPage.OutputDeviceComboBox) — это осознанный выбор, и тут
            // жёсткая привязка уместна: автоопределение наушников на этом
            // устройстве ненадёжно, ручной выбор — явная подстраховка.
            if (SelectedRenderDevice != null)
            {
                settings.PrimaryRenderDevice = SelectedRenderDevice;
                AudioVisualizerPlayer.Helpers.Diag.Log($"  Используем вручную выбранное устройство: {SelectedRenderDevice.Name}");
            }

            var graphResult = await AudioGraph.CreateAsync(settings);
            AudioVisualizerPlayer.Helpers.Diag.Log($"  AudioGraph.CreateAsync status={graphResult.Status}");
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException("Не удалось создать AudioGraph: " + graphResult.Status);

            _graph = graphResult.Graph;
            _graphCreationCount++;

            var fileInputResult = await _graph.CreateFileInputNodeAsync(file);
            AudioVisualizerPlayer.Helpers.Diag.Log($"  CreateFileInputNodeAsync status={fileInputResult.Status}");
            if (fileInputResult.Status != AudioFileNodeCreationStatus.Success)
                throw new InvalidOperationException("Не удалось открыть файл: " + fileInputResult.Status);

            _fileInput = fileInputResult.FileInputNode;

            // ВРЕМЕННО (диагностика): Submix убран из пути полностью. Раньше
            // FileInput -> Submix -> (DeviceOutput + FrameOutput) — Submix
            // был нужен только ради разветвления на два выхода для
            // визуализатора. Визуализатор сейчас отключён диагностическим
            // флагом (MainPage.DisableVisualizerForDiagnostics), и щелчки всё
            // равно продолжаются даже без него — подозрение упало на сам
            // Submix. Проверяем: FileInput подключается К DeviceOutput
            // НАПРЯМУЮ, без какого-либо промежуточного узла вообще.
            // EffectDefinitions (эквалайзер) — не эксклюзивное свойство
            // Submix, есть и у FileInput, переносим эквалайзер сюда.
            _submix = null;

            // Эквалайзер вешаем прямо на FileInput — единственную точку,
            // через которую пока проходит весь звук (Submix временно убран).
            try
            {
                _equalizer = new EqualizerEffectDefinition(_graph);

                // Bands уже создаются по умолчанию конструктором — отдельного
                // способа "добавить" полосу нет, просто настраиваем уже
                // существующие. На случай если по умолчанию их окажется
                // меньше 5 — берём сколько реально есть, не выходим за границы.
                int availableBands = _equalizer.Bands.Count;
                int bandCount = Math.Min(availableBands, EqualizerFrequencies.Length);
                AudioVisualizerPlayer.Helpers.Diag.Log($"  Эквалайзер: доступно полос по умолчанию = {availableBands}, используем {bandCount}");

                _equalizerBands = new EqualizerBand[bandCount];
                for (int i = 0; i < bandCount; i++)
                {
                    double gain = (App.EqualizerGainsDb != null && i < App.EqualizerGainsDb.Length)
                        ? App.EqualizerGainsDb[i] : 0.0;
                    var band = _equalizer.Bands[i];

                    try
                    {
                        band.FrequencyCenter = (float)EqualizerFrequencies[i];
                    }
                    catch (Exception exFreq)
                    {
                        AudioVisualizerPlayer.Helpers.Diag.Log($"  Полоса {i}: FrequencyCenter={EqualizerFrequencies[i]} — ОШИБКА: {exFreq.Message}");
                        throw;
                    }

                    try
                    {
                        band.Bandwidth = (float)EqualizerBandwidths[i];
                    }
                    catch (Exception exBw)
                    {
                        AudioVisualizerPlayer.Helpers.Diag.Log($"  Полоса {i}: Bandwidth={EqualizerBandwidths[i]} — ОШИБКА: {exBw.Message}");
                        throw;
                    }

                    try
                    {
                        band.Gain = DbToLinearGain(gain);
                    }
                    catch (Exception exGain)
                    {
                        AudioVisualizerPlayer.Helpers.Diag.Log($"  Полоса {i}: Gain={gain} дБ (линейно {DbToLinearGain(gain)}) — ОШИБКА: {exGain.Message}");
                        throw;
                    }

                    AudioVisualizerPlayer.Helpers.Diag.Log($"  Полоса {i}: FrequencyCenter={EqualizerFrequencies[i]}, Bandwidth={EqualizerBandwidths[i]}, Gain={gain} — все три применены успешно");
                    _equalizerBands[i] = band;
                }
                _fileInput.EffectDefinitions.Add(_equalizer);

                // Явно включаем — не полагаемся на предположение, что Add()
                // сам по себе активирует эффект по умолчанию.
                _fileInput.EnableEffectsByDefinition(_equalizer);
                AudioVisualizerPlayer.Helpers.Diag.Log("  Эквалайзер создан, подключён к FileInput и явно включён");
            }
            catch (Exception ex)
            {
                // Не критично — если эквалайзер почему-то не создался, звук
                // просто играет без него (плоская АЧХ), это не должно ронять
                // всю загрузку трека.
                AudioVisualizerPlayer.Helpers.Diag.Log("  Не удалось создать эквалайзер: " + ex);
                _equalizer = null;
                _equalizerBands = null;
            }

            var deviceOutputResult = await _graph.CreateDeviceOutputNodeAsync();
            AudioVisualizerPlayer.Helpers.Diag.Log($"  CreateDeviceOutputNodeAsync status={deviceOutputResult.Status}");
            if (deviceOutputResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException("Не удалось создать вывод на устройство: " + deviceOutputResult.Status);

            _deviceOutput = deviceOutputResult.DeviceOutputNode;
            AudioVisualizerPlayer.Helpers.Diag.Log("  DeviceOutput создан, перед FileInput.AddOutgoingConnection(DeviceOutput) — напрямую, без Submix");
            _fileInput.AddOutgoingConnection(_deviceOutput);
            AudioVisualizerPlayer.Helpers.Diag.Log("  FileInput -> DeviceOutput подключено напрямую — BuildGraphAsync завершён");
        }

        public void Play()
        {
            AudioVisualizerPlayer.Helpers.Diag.Log($"Play() вызван, _graph == null: {_graph == null}");
            _graph?.Start();
            IsPlaying = true;
            Smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
            PlaybackStateChanged?.Invoke(this, true);
        }

        public void Pause()
        {
            AudioVisualizerPlayer.Helpers.Diag.Log("Pause() вызван");
            _graph?.Stop();
            IsPlaying = false;
            Smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
            PlaybackStateChanged?.Invoke(this, false);
        }

        public void TogglePlayPause()
        {
            if (IsPlaying) Pause();
            else Play();
        }

        // Сюда подключить реальный плейлист — сейчас просто пробрасываем наружу
        public event EventHandler NextRequested;
        public event EventHandler PreviousRequested;

        // Публичные методы-триггеры: событие нельзя инициировать (Invoke) снаружи
        // объявляющего класса — только через += / -=. Кнопки Next/Prev в UI
        // должны вызывать именно эти методы, а не трогать событие напрямую.
        public void RaiseNextRequested() => NextRequested?.Invoke(this, EventArgs.Empty);
        public void RaisePreviousRequested() => PreviousRequested?.Invoke(this, EventArgs.Empty);

        private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            // Обработчик вызывается в фоновом потоке. Play()/Pause() здесь —
            // это вызовы AudioGraph.Start()/Stop(), они thread-safe и не требуют
            // UI-потока (в отличие от, например, обновления Rectangle.Height).
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    Pause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    NextRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    PreviousRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        private void DisposeGraph()
        {
            _graph?.Stop();
            _fileInput?.Dispose();
            _graph?.Dispose();
            _graph = null;
            _fileInput = null;
            _submix = null;
            _deviceOutput = null;
            _equalizer = null;
            _equalizerBands = null;
        }
    }
}
