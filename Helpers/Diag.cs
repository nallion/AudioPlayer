using System;

namespace AudioVisualizerPlayer.Helpers
{
    /// <summary>
    /// Целевая диагностика бага с наушниками: холодный старт с уже подключёнными
    /// наушниками не работает, переключение треков при подключённых наушниках
    /// роняет и звук, и визуализацию. Пишем в audio_diagnostics.log каждый шаг
    /// построения графа, чтобы увидеть точное место и причину сбоя (Device
    /// Portal → File Explorer → LocalAppData → AudioVisualizerPlayer →
    /// LocalState → audio_diagnostics.log).
    /// </summary>
    public static class Diag
    {
        public static void Log(string text)
        {
            try
            {
                var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var path = System.IO.Path.Combine(folder.Path, "audio_diagnostics.log");
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + "\n");
            }
            catch
            {
            }
        }
    }
}
