using Windows.Storage;

namespace AudioVisualizerPlayer.Models
{
    /// <summary>
    /// Один трек в плейлисте — файл + уже прочитанные ID3-теги (или имя файла,
    /// если тег пустой). Читаем теги один раз при построении плейлиста
    /// (выбор файла/папки), а не заново при каждом Next/Prev.
    /// </summary>
    public class PlaylistItem
    {
        public StorageFile File { get; }
        public string Title { get; }
        public string Artist { get; }

        public PlaylistItem(StorageFile file, string title, string artist)
        {
            File = file;
            Title = title;
            Artist = artist;
        }
    }
}
