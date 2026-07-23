using System;
using System.Collections.Generic;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using AudioVisualizerPlayer.Models;
using AudioVisualizerPlayer.Services;

namespace AudioVisualizerPlayer
{
    /// <summary>
    /// App-level синглтон плеера. Важно: держим ссылку здесь, а не в MainPage —
    /// иначе объект MediaPlayer будет собран сборщиком мусора при выгрузке страницы,
    /// и фоновое воспроизведение прервётся.
    /// </summary>
    public sealed partial class App : Application
    {
        public static PlaybackService Playback { get; private set; }

        // Общий плейлист — доступен и MainPage, и PlaylistPage, без передачи
        // через параметры навигации. CurrentIndex — какой трек сейчас играет.
        public static List<PlaylistItem> CurrentPlaylist { get; } = new List<PlaylistItem>();
        public static int CurrentPlaylistIndex { get; set; } = -1;

        // Когда пользователь тапает трек на PlaylistPage, страница кладёт сюда
        // выбранный индекс и уходит назад (GoBack) — MainPage.OnNavigatedTo
        // подхватывает значение и запускает нужный трек.
        public static int? RequestedPlaylistIndex { get; set; }

        public App()
        {
            // ВАЖНО: подписка на UnhandledException должна идти ДО InitializeComponent().
            // Если краш происходит в самом InitializeComponent() (парсинг App.xaml —
            // самая первая вещь, которую вообще выполняет приложение), а подписка
            // была бы после неё, обработчик просто не успел бы зарегистрироваться,
            // и мы бы никогда не увидели ни диалог, ни crash.log — ровно то,
            // что происходит сейчас.
            UnhandledException += OnUnhandledException;

            try
            {
                InitializeComponent();
                Suspending += OnSuspending;
            }
            catch (Exception ex)
            {
                // Обычный try/catch работает на уровне IL независимо от того, успела
                // ли инициализироваться событийная система XAML — на случай если
                // именно от этого зависит срабатывание UnhandledException выше.
                // WriteLogSync — не-async версия записи, конструктор не может быть async.
                WriteCrashLogSync(ex.ToString());
                throw;
            }
        }

        private static void WriteCrashLogSync(string text)
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var path = System.IO.Path.Combine(folder.Path, "crash.log");
                System.IO.File.WriteAllText(path, text);
            }
            catch
            {
                // Если и это не удалось — сделать уже ничего нельзя.
            }
        }

        private async void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // e.Handled = true не даёт процессу мгновенно рухнуть — успеваем записать
            // причину в файл. Смотреть его можно через Device Portal → File Explorer →
            // LocalAppData → AudioVisualizerPlayer → LocalState → crash.log,
            // либо через WDRT. Это единственный надёжный способ узнать причину
            // краша, если он происходит ДО того, как успела открыться MainPage
            // (например, в конструкторе PlaybackService/MediaPlayer).
            e.Handled = true;
            try
            {
                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "crash.log", Windows.Storage.CreationCollisionOption.ReplaceExisting);
                await Windows.Storage.FileIO.WriteTextAsync(file, e.Exception?.ToString() ?? e.Message);
            }
            catch
            {
                // Если и запись лога не удалась — ничего не поделать, но хотя бы
                // не роняем процесс молча за счёт e.Handled = true выше.
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                // Плеер создаём один раз за всё время жизни процесса
                if (Playback == null)
                {
                    Playback = new PlaybackService();
                }

                Frame rootFrame = Window.Current.Content as Frame;

                if (rootFrame == null)
                {
                    rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;
                    Window.Current.Content = rootFrame;
                }

                if (e.PrelaunchActivated == false)
                {
                    if (rootFrame.Content == null)
                    {
                        rootFrame.Navigate(typeof(MainPage), e.Arguments);
                    }
                    Window.Current.Activate();
                }
            }
            catch (Exception ex)
            {
                // Тот же приём: обычный try/catch, а не только событийный
                // UnhandledException — на случай если PlaybackService/MediaPlayer
                // валится именно здесь, до того как открылась MainPage.
                WriteCrashLogSync(ex.ToString());
                throw;
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new System.Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            // Ничего не останавливаем здесь: MediaPlayer должен продолжать играть
            // в фоне. Приложению для этого не нужен deferral — система сама держит
            // процесс живым, пока идёт воспроизведение через MediaPlayer + SMTC.
        }
    }
}
