using System.Collections.ObjectModel;
using Microsoft.Win32;
using XTONE.Models;

namespace XTONE.Dialogs;

public partial class CreateAlbumWindow : Window
{
    private readonly ObservableCollection<SelectableTrack> _tracks;
    private string? _coverPath;
    public CustomAlbum? CreatedAlbum { get; private set; }

    public CreateAlbumWindow(IEnumerable<Track> tracks)
    {
        InitializeComponent();
        var ordered = tracks.OrderBy(track => track.Artist).ThenBy(track => track.Title).ToList();
        _tracks = new ObservableCollection<SelectableTrack>(ordered.Select(track => new SelectableTrack(track)));
        TrackList.ItemsSource = _tracks;
        TrackCountText.Text = $"{ordered.Count} available";
    }

    private void ChooseCover_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Choose album artwork",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.webp;*.bmp"
        };
        if (picker.ShowDialog(this) == true)
        {
            _coverPath = picker.FileName;
            CoverPathText.Text = Path.GetFileName(_coverPath);
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        var selected = _tracks.Where(track => track.IsSelected).Select(track => track.Track.Path).ToList();
        if (title.Length == 0)
        {
            ValidationText.Text = "Give this album a title.";
            TitleBox.Focus();
            return;
        }
        if (selected.Count == 0)
        {
            ValidationText.Text = "Choose at least one track.";
            return;
        }

        CreatedAlbum = new CustomAlbum
        {
            Title = title,
            Artist = string.IsNullOrWhiteSpace(ArtistBox.Text) ? "Various artists" : ArtistBox.Text.Trim(),
            Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
            ArtworkPath = _coverPath,
            TrackPaths = selected
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class SelectableTrack(Track track)
    {
        public Track Track { get; } = track;
        public bool IsSelected { get; set; }
    }
}
