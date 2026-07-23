using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AudioVisualizerPlayer.Services
{
    /// <summary>
    /// Воспроизведение через Windows.Media.Playback.MediaPlayer — вернулись
    /// сюда после долгого архитектурного эксперимента с единым AudioGraph
    /// (звук + анализ спектра через один граф с промежуточным Submix). Тот
    /// эксперимент решал красивую задачу (нулевой дрейф между звуком и
    /// визуализацией), но давал регулярные, не до конца объяснимые щелчки
    /// в звуке — причём даже в WAV, даже с полностью отключённым
    /// визуализатором/таймером позиции/эквалайзером/увеличенным буфером.
    ///
    /// РЕШАЮЩЕЕ доказательство, что дело было именно в нашей AudioGraph-
    /// архитектуре, а не в самом устройстве: тот же файл в VLC и системном
    /// плеере (оба почти наверняка построены на MediaPlayer) играет идеально
    /// на этом же телефоне. MediaPlayer — высокоуровневый, годами отточенный
    /// платформой API, здесь доказанно стабилен.
    ///
    /// ЦЕНА ВОЗВРАТА (два известных, осознанных ограничения):
    /// 1. Визуализатор больше не может читать тот же поток, что играет
    ///    звук, — MediaPlayer не даёт такого доступа. Он снова читает файл
    ///    ОТДЕЛЬНО, через свой независимый AudioGraph (см. VisualizerService).
    ///    Небольшой дрейф между звуком и визуализацией теоретически возможен
    ///    при частых паузах — гасим его Seek-ресинхронизацией на каждый
    ///    Play() (см. MainPage.OnPlaybackStateChanged → _visualizer.Start(position)).
    /// 2. EqualizerEffectDefinition и PrimaryRenderDevice (ручной выбор
    ///    устройства вывода) — часть именно AudioGraph API, с MediaPlayer
    ///    напрямую не работают. UI страницы эквалайзера и выбора устройства
    ///    остался, но сами эффекты сейчас НЕ действуют на реальный звук
    ///    (см. SetEqualizerGain/SelectedRenderDevice ниже — оба no-op).
    /// </summary>
    public class PlaybackService
    {
        private MediaPlayer _player;

        public SystemMediaTransportControls Smtc => _player?.SystemMediaTransportControls;

        /// <summary>true — сейчас играет, false — на паузе/остановлено.</summary>
        public event EventHandler<bool> PlaybackStateChanged;

        /// <summary>Файл доиграл до конца сам (не пауза от пользователя) — сюда
        /// подписывается MainPage для автоперехода к следующему треку.</summary>
        public event EventHandler TrackEnded;

        public event EventHandler NextRequested;
        public event EventHandler PreviousRequested;

        /// <summary>
        /// В отличие от AudioGraph (где AudioFileInputNode читал метаданные
        /// синхронно при создании узла), у MediaPlayer Duration/NaturalDuration
        /// доступны НЕ сразу после LoadAsync — файл открывается асинхронно.
        /// Подписывайтесь на это событие, чтобы узнать точный момент, когда
        /// Duration уже можно читать (вместо предположения "сразу после LoadAsync").
        /// </summary>
        public event EventHandler MediaOpened;

        /// <summary>
        /// Бесконечное зацикленное воспроизведение ТЕКУЩЕГО трека — если
        /// true, по окончании файла он просто перематывается в начало и
        /// играет заново вместо перехода к следующему треку в плейлисте.
        /// </summary>
        public bool LoopCurrentTrack { get; set; }

        public bool IsPlaying =>
            _player?.PlaybackSession != null &&
            _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

        public TimeSpan Position
        {
            get => _player?.PlaybackSession?.Position ?? TimeSpan.Zero;
            set { if (_player?.PlaybackSession != null) _player.PlaybackSession.Position = value; }
        }

        public TimeSpan Duration => _player?.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;

        /// <summary>
        /// НЕ ДЕЙСТВУЕТ на реальный звук — EqualizerEffectDefinition это часть
        /// AudioGraph API, с MediaPlayer напрямую не работает. Оставлен как
        /// no-op, чтобы не переписывать EqualizerPage — но эффекта не будет,
        /// пока для эквалайзера не найдётся отдельное решение.
        /// </summary>
        public void SetEqualizerGain(int bandIndex, double gainDb)
        {
            // Намеренно пусто — см. комментарий класса.
        }

        /// <summary>
        /// НЕ ДЕЙСТВУЕТ на реальный звук — PrimaryRenderDevice это часть
        /// AudioGraphSettings, с MediaPlayer напрямую не работает. Оставлено
        /// как свойство (принимает значение, но ни на что не влияет), чтобы
        /// не переписывать MainPage.OutputDeviceComboBox.
        /// </summary>
        public DeviceInformation SelectedRenderDevice { get; set; }

        public PlaybackService()
        {
            _player = new MediaPlayer { AutoPlay = false };

            // Отключаем автоматическую обработку системных кнопок лок-скрина —
            // обрабатываем Play/Pause/Next/Previous сами, единым путём что для
            // аппаратных кнопок, что для кнопок в UI (RaiseNextRequested и т.п.).
            _player.CommandManager.IsEnabled = false;

            _player.MediaEnded += OnMediaEnded;
            _player.MediaOpened += (s, a) => MediaOpened?.Invoke(this, EventArgs.Empty);
            _player.PlaybackSession.PlaybackStateChanged += OnPlaybackSessionStateChanged;

            Smtc.IsPlayEnabled = true;
            Smtc.IsPauseEnabled = true;
            Smtc.IsNextEnabled = true;
            Smtc.IsPreviousEnabled = true;
            Smtc.ButtonPressed += OnSmtcButtonPressed;
        }

        public Task LoadAsync(StorageFile file, string title, string artist, StorageFile albumArt = null)
        {
            var source = MediaSource.CreateFromStorageFile(file);
            _player.Source = source;

            var updater = Smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;

            if (albumArt != null)
            {
                updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(albumArt);
            }

            updater.Update();

            return Task.CompletedTask; // async-сигнатура сохранена для совместимости вызывающего кода
        }

        public void Play() => _player.Play();
        public void Pause() => _player.Pause();

        public void TogglePlayPause()
        {
            if (IsPlaying) Pause();
            else Play();
        }

        // Публичные методы-триггеры: событие нельзя инициировать (Invoke) снаружи
        // объявляющего класса — только через += / -=. Кнопки Next/Prev в UI
        // должны вызывать именно эти методы, а не трогать событие напрямую.
        public void RaiseNextRequested() => NextRequested?.Invoke(this, EventArgs.Empty);
        public void RaisePreviousRequested() => PreviousRequested?.Invoke(this, EventArgs.Empty);

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            if (LoopCurrentTrack)
            {
                // Просто перематываем в начало и продолжаем — трек не
                // "закончился" с точки зрения плеера, TrackEnded не стреляет,
                // автопереход к следующему треку не запускается.
                _player.PlaybackSession.Position = TimeSpan.Zero;
                _player.Play();
                return;
            }

            TrackEnded?.Invoke(this, EventArgs.Empty);
        }

        private void OnPlaybackSessionStateChanged(MediaPlaybackSession sender, object args)
        {
            bool playing = sender.PlaybackState == MediaPlaybackState.Playing;
            PlaybackStateChanged?.Invoke(this, playing);
            Smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }

        private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            // Обработчик вызывается в фоновом потоке. Play()/Pause() здесь —
            // это вызовы MediaPlayer.Play()/Pause(), thread-safe, не требуют UI-потока.
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
    }
}
