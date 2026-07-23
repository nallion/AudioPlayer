using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using AudioVisualizerPlayer.Helpers;
using AudioVisualizerPlayer.Models;

namespace AudioVisualizerPlayer
{
    /// <summary>
    /// Список треков текущего плейлиста (App.CurrentPlaylist — общий,
    /// заполняется в MainPage при выборе файла/папки). Тап по треку кладёт
    /// выбранный индекс в App.RequestedPlaylistIndex и возвращает назад —
    /// MainPage.OnNavigatedTo подхватывает его и запускает нужный трек.
    /// </summary>
    public sealed partial class PlaylistPage : Page
    {
        public PlaylistPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            TracksList.ItemsSource = App.CurrentPlaylist;

            if (App.CurrentPlaylistIndex >= 0 && App.CurrentPlaylistIndex < App.CurrentPlaylist.Count)
            {
                TracksList.SelectedIndex = App.CurrentPlaylistIndex;
            }
        }

        private void TracksList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = (PlaylistItem)e.ClickedItem;
            int index = App.CurrentPlaylist.IndexOf(item);
            Diag.Log($"PlaylistPage.ItemClick: index={index}, item={item?.Title}");
            if (index < 0) return;

            App.RequestedPlaylistIndex = index;

            Diag.Log($"PlaylistPage: перед Frame.GoBack(), CanGoBack={Frame.CanGoBack}");
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            Diag.Log("PlaylistPage: после Frame.GoBack() (синхронная часть ItemClick завершена)");
        }
    }
}
