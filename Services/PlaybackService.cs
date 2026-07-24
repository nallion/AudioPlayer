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
    /// Единый AudioGraph на воспроизведение звука — возврат к архитектуре
    /// "один граф на всё" (FileInput → Submix → DeviceOutput + FrameOutput
    /// для визуализатора), но с ПОСЛЕДНЕЙ непроверенной комбинацией: явно
    /// заданный PCM-формат 44100Гц/стерео/16бит — РЕАЛЬНАЯ нативная частота
    /// файла (он CBR), а не 48000 (которые мы форсировали раньше по ошибочной
    /// теории) и не "авто" (то и другое уже проверяли — щёлкало).
    ///
    /// Если явные 44100 (без несоответствия частоте файла, без "авто",
    /// которое могло согласовываться на что-то отличное от родной частоты)
    /// уберут щелчки — эта архитектура возвращает идеальную синхронизацию
    /// звука и визуализации ПО ПОСТРОЕНИЮ, без всех костылей независимого
    /// декодера (Seek, TrackLooped, плановые обновления), которые понадобились
    /// после перехода на MediaPlayer + отдельный визуализатор.
    ///
    /// НАПОМИНАНИЕ (см. историю): AudioFileInputNode.AddOutgoingConnection()
    /// напрямую в ДВА узла одновременно бросает XAUDIO2_E_INVALID_CALL на
    /// этом устройстве — узел чтения сжатого формата не поддерживает больше
    /// одного исходящего соединения надёжно. Решение — промежуточный
    /// AudioSubmixNode: FileInput → Submix → (DeviceOutput И FrameOutput).
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

        /// <summary>Файл доиграл до конца сам (не пауза от пользователя) — сюда
        /// подписывается MainPage для автоперехода к следующему треку.</summary>
        public event EventHandler TrackEnded;

        public event EventHandler NextRequested;
        public event EventHandler PreviousRequested;

        /// <summary>
        /// Бесконечное зацикленное воспроизведение ТЕКУЩЕГО трека — если
        /// true, по окончании файла он просто перематывается в начало и
        /// играет заново вместо перехода к следующему треку в плейлисте.
        /// </summary>
        public bool LoopCurrentTrack { get; set; }

        public bool IsPlaying { get; private set; }

        /// <summary>
        /// Доступ к общему графу и submix-узлу — VisualizerService подключает
        /// свой FrameOutputNode вторым исходящим соединением ОТ SUBMIX-узла
        /// (не от FileInput напрямую — см. комментарий к классу).
        /// </summary>
        public AudioGraph Graph => _graph;
        public AudioSubmixNode Submix => _submix;

        public TimeSpan Position
        {
            get => _fileInput?.Position ?? TimeSpan.Zero;
            set { if (_fileInput != null) _fileInput.Seek(value); }
        }

        public TimeSpan Duration => _fileInput?.Duration ?? TimeSpan.Zero;

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
        /// Загружает трек: создаёт AudioGraph с ЯВНО заданным PCM 44100Гц/
        /// стерео/16бит (реальная нативная частота файла — CBR mp3),
        /// файловый узел, submix, узел вывода на динамики, соединяет их,
        /// обновляет метаданные для лок-скрина.
        /// </summary>
        public async Task LoadAsync(StorageFile file, string title, string artist, StorageFile albumArt = null)
        {
            AudioVisualizerPlayer.Helpers.Diag.Log($"LoadAsync начат для файла: {file.Name}");

            DisposeGraph();

            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                // 48000Гц/стерео/24бит — ещё один вариант формата на пробу.
                // Формат задаётся на весь AudioGraphSettings (влияет на весь
                // граф целиком, включая Submix), отдельно "только для Submix"
                // задать нельзя — WinRT API не даёт такой детализации.
                EncodingProperties = Windows.Media.MediaProperties.AudioEncodingProperties.CreatePcm(48000, 2, 24)
            };

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

            _submix = _graph.CreateSubmixNode();
            AudioVisualizerPlayer.Helpers.Diag.Log("  Submix создан, перед FileInput.AddOutgoingConnection(Submix)");
            _fileInput.AddOutgoingConnection(_submix);
            AudioVisualizerPlayer.Helpers.Diag.Log("  FileInput -> Submix подключено");

            var deviceOutputResult = await _graph.CreateDeviceOutputNodeAsync();
            AudioVisualizerPlayer.Helpers.Diag.Log($"  CreateDeviceOutputNodeAsync status={deviceOutputResult.Status}");
            if (deviceOutputResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException("Не удалось создать вывод на устройство: " + deviceOutputResult.Status);

            _deviceOutput = deviceOutputResult.DeviceOutputNode;
            _submix.AddOutgoingConnection(_deviceOutput);
            AudioVisualizerPlayer.Helpers.Diag.Log("  Submix -> DeviceOutput подключено — граф собран");

            _fileInput.FileCompleted += (s, a) =>
            {
                if (LoopCurrentTrack)
                {
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

        public void RaiseNextRequested() => NextRequested?.Invoke(this, EventArgs.Empty);
        public void RaisePreviousRequested() => PreviousRequested?.Invoke(this, EventArgs.Empty);

        private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
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
