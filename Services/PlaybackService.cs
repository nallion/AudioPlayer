using System;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AudioVisualizerPlayer.Services
{
    /// <summary>
    /// Отвечает за: (1) собственно воспроизведение звука через MediaPlayer,
    /// которое продолжается в фоне/при заблокированном экране;
    /// (2) интеграцию с SystemMediaTransportControls — это то, что рисует
    /// системный UI на лок-скрине и в Action Center (play/pause/next/prev,
    /// обложка, название трека). Кастомизировать этот системный UI нельзя —
    /// туда передаются только метаданные.
    /// </summary>
    public class PlaybackService
    {
        public MediaPlayer Player { get; }
        public SystemMediaTransportControls Smtc { get; }

        public event EventHandler<MediaPlaybackState> PlaybackStateChanged;

        public PlaybackService()
        {
            Player = new MediaPlayer
            {
                AutoPlay = false,
                // CommandManager сам умеет реагировать на аппаратные кнопки
                // (гарнитура, Bluetooth) — не нужно ловить их вручную.
            };

            Smtc = Player.SystemMediaTransportControls;
            Smtc.IsPlayEnabled = true;
            Smtc.IsPauseEnabled = true;
            Smtc.IsNextEnabled = true;
            Smtc.IsPreviousEnabled = true;

            Smtc.ButtonPressed += OnSmtcButtonPressed;
            Player.PlaybackSession.PlaybackStateChanged += (s, a) =>
                PlaybackStateChanged?.Invoke(this, s.PlaybackState);
        }

        /// <summary>
        /// Загружает трек и одновременно обновляет метаданные для лок-скрина.
        /// </summary>
        public async System.Threading.Tasks.Task LoadAsync(StorageFile file, string title, string artist, StorageFile albumArt = null)
        {
            Player.Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file);

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

        public void Play() => Player.Play();
        public void Pause() => Player.Pause();

        public void TogglePlayPause()
        {
            if (Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                Pause();
            else
                Play();
        }

        // Сюда подключить реальный плейлист — сейчас просто пробрасываем наружу
        public event EventHandler NextRequested;
        public event EventHandler PreviousRequested;

        private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            // Обработчик вызывается в фоновом потоке — UI трогать напрямую нельзя,
            // но Play/Pause/Next/Prev тут работают с MediaPlayer, которому это ок.
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
