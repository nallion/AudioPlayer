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
    /// AudioFileInputNode — звук идёт в AudioDeviceOutputNode, а анализ
    /// (VisualizerService) подключается вторым исходящим соединением от ТОГО ЖЕ
    /// AudioFileInputNode. Дрейфа больше нет в принципе — это один и тот же
    /// поток данных, а не два независимых.
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
        private AudioDeviceOutputNode _deviceOutput;

        public SystemMediaTransportControls Smtc { get; }

        /// <summary>true — сейчас играет, false — на паузе/остановлено.</summary>
        public event EventHandler<bool> PlaybackStateChanged;

        public bool IsPlaying { get; private set; }

        /// <summary>
        /// Доступ к общему графу и файловому узлу — именно за это и затевался
        /// весь рефакторинг: VisualizerService подключает свой FrameOutputNode
        /// вторым исходящим соединением от этого же FileInput, вместо того чтобы
        /// открывать файл заново в отдельном графе.
        /// </summary>
        public AudioGraph Graph => _graph;
        public AudioFileInputNode FileInput => _fileInput;

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
            // Освобождаем предыдущий граф, если уже что-то играло
            DisposeGraph();

            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException($"Не удалось создать AudioGraph (это граф №{_graphCreationCount + 1} за сессию): " + graphResult.Status);

            _graph = graphResult.Graph;
            _graphCreationCount++;

            var fileInputResult = await _graph.CreateFileInputNodeAsync(file);
            if (fileInputResult.Status != AudioFileNodeCreationStatus.Success)
                throw new InvalidOperationException($"Не удалось открыть файл (граф №{_graphCreationCount} за сессию): " + fileInputResult.Status);

            _fileInput = fileInputResult.FileInputNode;

            var deviceOutputResult = await _graph.CreateDeviceOutputNodeAsync();
            if (deviceOutputResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new InvalidOperationException($"Не удалось создать вывод на устройство (граф №{_graphCreationCount} за сессию): " + deviceOutputResult.Status);

            _deviceOutput = deviceOutputResult.DeviceOutputNode;

            // Реальный звук в динамики. VisualizerService добавит ВТОРОЕ
            // исходящее соединение от этого же _fileInput в свой FrameOutputNode —
            // AudioFileInputNode поддерживает несколько исходящих соединений сразу.
            _fileInput.AddOutgoingConnection(_deviceOutput);

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
        }

        public void Play()
        {
            _graph?.Start();
            IsPlaying = true;
            Smtc.PlaybackStatus = MediaPlaybackStatus.Playing;
            PlaybackStateChanged?.Invoke(this, true);
        }

        public void Pause()
        {
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
            _deviceOutput = null;
        }
    }
}
