using System;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Audio;
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
        /// </summary>
        public async Task LoadAsync(StorageFile file, string title, string artist, StorageFile albumArt = null)
        {
            AudioVisualizerPlayer.Helpers.Diag.Log($"LoadAsync начат для файла: {file.Name}");

            // Освобождаем предыдущий граф, если уже что-то играло
            DisposeGraph();
            AudioVisualizerPlayer.Helpers.Diag.Log("Старый граф освобождён (если был)");

            // Сначала пробуем БЕЗ явного формата — это сохраняет нормальное
            // поведение "следования за устройством по умолчанию" (наушники,
            // подключённые ВО ВРЕМЯ воспроизведения, подхватываются на лету).
            // Только если это упадёт (холодный старт с уже подключёнными
            // наушниками — см. комментарий ниже), пересоздаём граф с явным
            // PCM-форматом как запасной вариант. Раньше явный формат стоял
            // всегда, и это чинило холодный старт, но ломало переключение
            // на лету — теперь он используется только как fallback.
            Exception lastError = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                bool useExplicitFormat = attempt > 1;
                AudioVisualizerPlayer.Helpers.Diag.Log($"Попытка {attempt}, useExplicitFormat={useExplicitFormat} — начало");
                try
                {
                    await BuildGraphAsync(file, useExplicitFormat);
                    lastError = null;
                    AudioVisualizerPlayer.Helpers.Diag.Log($"Попытка {attempt} — УСПЕХ");
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    AudioVisualizerPlayer.Helpers.Diag.Log($"Попытка {attempt} — ОШИБКА: {ex}");
                    DisposeGraph(); // чистим частично созданное состояние перед повтором
                    if (attempt == 1)
                    {
                        await Task.Delay(300);
                    }
                }
            }

            if (lastError != null)
            {
                AudioVisualizerPlayer.Helpers.Diag.Log("Обе попытки провалились, бросаем исключение наверх");
                throw new InvalidOperationException(
                    $"Не удалось создать аудио-граф ни обычным способом, ни с явным форматом (граф-попытки за сессию: {_graphCreationCount}): "
                    + lastError.Message, lastError);
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
        /// useExplicitFormat=false — обычный путь, авто-согласование формата
        /// (сохраняет следование за устройством по умолчанию). true — запасной
        /// путь с явным PCM 44100/2/16, который чинит холодный старт с уже
        /// подключёнными наушниками, но ценой отключения автослежения —
        /// поэтому используется только как fallback после неудачной попытки.
        /// </summary>
        private async Task BuildGraphAsync(StorageFile file, bool useExplicitFormat)
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            if (useExplicitFormat)
            {
                settings.EncodingProperties = Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm(44100, 2, 16);
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
        }
    }
}
