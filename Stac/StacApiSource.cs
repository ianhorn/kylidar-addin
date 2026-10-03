/*
 * One STAC API endpoint the dock pane can search. Kylidar uses only the built-in KyFromAbove
 * catalog, but the search code takes a list of sources.
 *
 * Ported from kyfromabove-ext (same class, same name) -- see that add-in's StacApiSource.cs.
 * Each source owns its own StacClient; that's safe since StacClient's HttpClient/
 * JsonSerializerOptions are static/shared, so many StacClient instances pointed at different
 * BaseUri values cost nothing extra in sockets/connections.
 */
using System;

namespace KylidarAddin.Stac
{
    public class StacApiSource
    {
        public string Name { get; set; }
        public StacClient Client { get; }

        /// <summary>True for the built-in KyFromAbove catalog. Can't be removed via the UI.</summary>
        public bool IsDefault { get; }

        /// <summary>Convenience: the source's catalog base URL.</summary>
        public string BaseUri => Client.BaseUri;

        /// <summary>True for any non-default source -- used to show/hide the "remove" button.</summary>
        public bool CanRemove => !IsDefault;

        /// <summary>Wrap an existing StacClient.</summary>
        public StacApiSource(string name, StacClient client, bool isDefault = false)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            Name = string.IsNullOrWhiteSpace(name) ? Client.BaseUri : name;
            IsDefault = isDefault;
        }

        /// <summary>Create a new source (and its own StacClient) pointed at a base URL. Used for user-added sources.</summary>
        public StacApiSource(string name, string baseUri, bool isDefault = false)
            : this(name, new StacClient { BaseUri = baseUri }, isDefault)
        {
        }

        public override string ToString() => Name;
    }
}
