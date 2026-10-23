using XTONE.Models;

namespace XTONE.Dialogs;

public partial class CreatePlaylistWindow : Window
{
    public CustomPlaylist? CreatedPlaylist { get; private set; }

    public CreatePlaylistWindow(IEnumerable<Track> tracks)
    {
        InitializeComponent();
        PlaylistTracks.ItemsSource = tracks.OrderBy(track => track.Artist).ThenBy(track => track.Title).ToList();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var selected = PlaylistTracks.SelectedItems.Cast<Track>().Select(track => track.Path).ToList();
        if (name.Length == 0)
        {
            ErrorText.Text = "Give the playlist a name.";
            NameBox.Focus();
            return;
        }
        if (selected.Count == 0)
        {
            ErrorText.Text = "Choose at least one track.";
            return;
        }
        CreatedPlaylist = new CustomPlaylist { Name = name, TrackPaths = selected };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
