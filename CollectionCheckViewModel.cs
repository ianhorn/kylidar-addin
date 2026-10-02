/*
 * Checkbox view model for one collection discovered from a "Bring Your Own API" source (see
 * KylidarDockpaneViewModel.LoadSourceCollectionsAsync). The built-in KyFromAbove source doesn't
 * use this -- its three known LiDAR collections stay as the dedicated Phase 1/2/3 checkboxes,
 * since that mapping is already known rather than needing to be discovered.
 *
 * Adapted from kyfromabove-ext's CollectionCheckViewModel.cs (trimmed to this add-in's smaller
 * StacCollection model -- no Extent/Assets, which this add-in's checklist doesn't need).
 */
using ArcGIS.Desktop.Framework.Contracts;
using KylidarAddin.Stac;

namespace KylidarAddin
{
    /// <summary>A checkable STAC collection entry in a "Bring Your Own" source's collection list.</summary>
    public class CollectionCheckViewModel : PropertyChangedBase
    {
        private bool _isChecked;

        public CollectionCheckViewModel(StacCollection collection, StacApiSource source)
        {
            Id = collection.Id;
            Title = collection.TitleOrId;
            Source = source;
        }

        /// <summary>Which API source this collection came from -- every entry here is from a
        /// non-default source, so searches know which source's client to query.</summary>
        public StacApiSource Source { get; }
        public string Id { get; }
        public string Title { get; }

        /// <summary>Title with the source name appended, so collections from different "bring your
        /// own" sources aren't ambiguous once merged into one list.</summary>
        public string DisplayLabel => $"{Title} · {Source.Name}";

        public bool IsChecked
        {
            get => _isChecked;
            set => SetProperty(ref _isChecked, value, () => IsChecked);
        }

        public override string ToString() => $"{Title} ({Id})";
    }
}
