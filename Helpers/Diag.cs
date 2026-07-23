using System;

namespace AudioVisualizerPlayer.Helpers
{
    /// <summary>
    /// Целевая диагностика конкретного бага: после выбора трека на PlaylistPage
    /// интерфейс зависает. Пишем в отдельный лог nav_diagnostics.log каждый шаг
    /// пути PlaylistPage.ItemClick → GoBack → MainPage.OnNavigatedTo →
    /// LoadAndPlayCurrentAsync → LoadTrackAsync, чтобы увидеть, на каком именно
    /// шаге выполнение останавливается (Device Portal → File Explorer →
    /// LocalAppData → AudioVisualizerPlayer → LocalState → nav_diagnostics.log).
    /// </summary>
    public static class Diag
    {
        public static void Log(string text)
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var path = System.IO.Path.Combine(folder.Path, "nav_diagnostics.log");
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + "\n");
            }
            catch
            {
            }
        }
    }
}
