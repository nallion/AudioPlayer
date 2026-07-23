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
    /// соединения надёжно. Решение: между FileInput и двумя потребителями
    /// стоит промежуточный AudioSubmixNode — именно submix-узлы штатно
    /// поддерживают разветвление на несколько выходов. FileInput → Submix →
    /// (DeviceOutput И FrameOutput для VisualizerService). Дрейфа по-прежнему
    /// нет — это всё ещё один и тот же поток данных, просто с одной
    /// промежуточной точкой разветвления.
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
        /// Частоты 5 полос классического графического эквалайзера. Bandwidth
        /// подобран примерно пропорционально центральной частоте (грубо
        /// постоянная добротность) — не критично для функциональности,
        /// просто влияет на "ширину" влияния каждого слайдера.
        /// </summary>
        public static readonly double[] EqualizerFrequencies = { 60, 250, 1000, 4000, 12000 };
        // Bandwidth в этом API — не герцы, а октавы (см. официальный пример
        // Microsoft: значения вроде 1.5/2.0). 1.0 октава — стандартный,
        // безопасный выбор для графического эквалайзера на 5 полос.
        private static readonly double[] EqualizerBandwidths = { 1.0, 1.0, 1.0, 1.0, 1.2 };

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
                _equalizerBands[bandIndex].Gain = (float)gainDb;
                AudioVisualizerPlayer.Helpers.Diag.Log($"SetEqualizerGain: полоса {bandIndex} -> {gainDb} дБ, реально применено (band.Gain теперь = {_equalizerBands[bandIndex].Gain})");
            }
            else
            {
                AudioVisualizerPlayer.Helpers.Diag.Log($"SetEqualizerGain: полоса {bandIndex} -> {gainDb} дБ, НЕ применено — _equalizerBands == null: {_equalizerBands == null} (трек ещё не загружен?)");
            }
        }

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

            // Промежуточный submix-узел — на него заводим ЕДИНСТВЕННОЕ исходящее
            // соединение от FileInput (сам FileInput его надёжно поддерживает),
            // а дальше уже от submix идут ДВА соединения: в динамики и в
            // VisualizerService. Напрямую от FileInput два соединения давали
            // XAUDIO2_E_INVALID_CALL на этом устройстве.
            _submix = _graph.CreateSubmixNode();
            AudioVisualizerPlayer.Helpers.Diag.Log("  Submix создан, перед FileInput.AddOutgoingConnection(Submix)");
            _fileInput.AddOutgoingConnection(_submix);
            AudioVisualizerPlayer.Helpers.Diag.Log("  FileInput -> Submix подключено");

            // Эквалайзер вешаем на Submix — единственную точку, через которую
            // проходит ВЕСЬ звук (и в динамики, и в анализ визуализатора).
            // Граф (и Submix) пересоздаётся заново при каждой загрузке трека,
            // поэтому полосы создаём здесь же, каждый раз заново, подставляя
            // текущие сохранённые значения громкости из App.EqualizerGainsDb —
            // иначе эквалайзер сбрасывался бы на "плоский" при каждом Next/Prev.
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
                    band.FrequencyCenter = (float)EqualizerFrequencies[i];
                    band.Bandwidth = (float)EqualizerBandwidths[i];
                    band.Gain = (float)gain;
                    _equalizerBands[i] = band;
                }
                _submix.EffectDefinitions.Add(_equalizer);

                // Явно включаем — не полагаемся на предположение, что Add()
                // сам по себе активирует эффект по умолчанию.
                _submix.EnableEffectsByDefinition(_equalizer);
                AudioVisualizerPlayer.Helpers.Diag.Log("  Эквалайзер создан, подключён к Submix и явно включён");
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
            AudioVisualizerPlayer.Helpers.Diag.Log("  DeviceOutput создан, перед Submix.AddOutgoingConnection(DeviceOutput)");
            _submix.AddOutgoingConnection(_deviceOutput); // сама точка, где раньше падало на холодном старте
            AudioVisualizerPlayer.Helpers.Diag.Log("  Submix -> DeviceOutput подключено — BuildGraphAsync завершён");
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
