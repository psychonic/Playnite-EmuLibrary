using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EmuLibrary.Tests.Settings
{
    // Minimal hand-rolled Playnite database fakes, just enough to exercise MappingEditReconciler without a
    // running Playnite. Only the members the reconciler actually touches - enumerating Games, Update (a no-op:
    // items are reference types mutated in place), and BufferedUpdate - are real; every other member throws so
    // a test that starts depending on unfaked behaviour fails loudly instead of silently doing nothing.

    internal sealed class NoopDisposable : IDisposable { public void Dispose() { } }

    internal sealed class FakeItemCollection<T> : IItemCollection<T> where T : DatabaseObject
    {
#pragma warning disable 67 // events are part of the interface but unused by the reconciler
        public event EventHandler<ItemCollectionChangedEventArgs<T>> ItemCollectionChanged;
        public event EventHandler<ItemUpdatedEventArgs<T>> ItemUpdated;
#pragma warning restore 67

        private readonly List<T> _items;
        public FakeItemCollection(IEnumerable<T> items = null) => _items = items?.ToList() ?? new List<T>();

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        public void Add(T item) => _items.Add(item);
        public void Update(T item) { }
        public void Update(IEnumerable<T> items) { }
        public IDisposable BufferedUpdate() => new NoopDisposable();
        public void Dispose() { }

        // Unused surface.
        public T this[Guid id] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public GameDatabaseCollection CollectionType => throw new NotImplementedException();
        public bool ContainsItem(Guid id) => throw new NotImplementedException();
        public T Get(Guid id) => throw new NotImplementedException();
        public List<T> Get(IList<Guid> ids) => throw new NotImplementedException();
        public T Add(string itemName) => throw new NotImplementedException();
        public T Add(string itemName, Func<T, string, bool> existingComparer) => throw new NotImplementedException();
        public IEnumerable<T> Add(List<string> items) => throw new NotImplementedException();
        public T Add(MetadataProperty property) => throw new NotImplementedException();
        public IEnumerable<T> Add(IEnumerable<MetadataProperty> properties) => throw new NotImplementedException();
        public IEnumerable<T> Add(List<string> items, Func<T, string, bool> existingComparer) => throw new NotImplementedException();
        public void Add(IEnumerable<T> items) => throw new NotImplementedException();
        public bool Remove(Guid id) => throw new NotImplementedException();
        public bool Remove(T item) => throw new NotImplementedException();
        public bool Remove(IEnumerable<T> items) => throw new NotImplementedException();
        public void Clear() => throw new NotImplementedException();
        public bool Contains(T item) => throw new NotImplementedException();
        public void CopyTo(T[] array, int arrayIndex) => throw new NotImplementedException();
        public void BeginBufferUpdate() => throw new NotImplementedException();
        public void EndBufferUpdate() => throw new NotImplementedException();
        public IEnumerable<T> GetClone() => throw new NotImplementedException();
    }

    internal sealed class FakeGameDatabase : IGameDatabaseAPI
    {
        public FakeGameDatabase(IEnumerable<Game> games) => Games = new FakeItemCollection<Game>(games);

        public IItemCollection<Game> Games { get; }
        public IItemCollection<Platform> Platforms { get; } = new FakeItemCollection<Platform>();
        public IDisposable BufferedUpdate() => new NoopDisposable();

        // Unused surface.
        public IItemCollection<Emulator> Emulators => throw new NotImplementedException();
        public IItemCollection<Genre> Genres => throw new NotImplementedException();
        public IItemCollection<Company> Companies => throw new NotImplementedException();
        public IItemCollection<Tag> Tags => throw new NotImplementedException();
        public IItemCollection<Category> Categories => throw new NotImplementedException();
        public IItemCollection<Series> Series => throw new NotImplementedException();
        public IItemCollection<AgeRating> AgeRatings => throw new NotImplementedException();
        public IItemCollection<Region> Regions => throw new NotImplementedException();
        public IItemCollection<GameSource> Sources => throw new NotImplementedException();
        public IItemCollection<GameFeature> Features => throw new NotImplementedException();
        public IItemCollection<GameScannerConfig> GameScanners => throw new NotImplementedException();
        public IItemCollection<CompletionStatus> CompletionStatuses => throw new NotImplementedException();
        public IItemCollection<Playnite.ImportExclusionItem> ImportExclusions => throw new NotImplementedException();
        public IItemCollection<FilterPreset> FilterPresets => throw new NotImplementedException();
        public bool IsOpen => throw new NotImplementedException();
        public string DatabasePath => throw new NotImplementedException();
#pragma warning disable 67
        public event EventHandler DatabaseOpened;
#pragma warning restore 67
        public void BeginBufferUpdate() => throw new NotImplementedException();
        public void EndBufferUpdate() => throw new NotImplementedException();
        public string AddFile(string path, Guid parentId) => throw new NotImplementedException();
        public void SaveFile(string id, string path) => throw new NotImplementedException();
        public void RemoveFile(string id) => throw new NotImplementedException();
        public string GetFileStoragePath(Guid parentId) => throw new NotImplementedException();
        public string GetFullFilePath(string databasePath) => throw new NotImplementedException();
        public Game ImportGame(GameMetadata game) => throw new NotImplementedException();
        public Game ImportGame(GameMetadata game, LibraryPlugin sourcePlugin) => throw new NotImplementedException();
        public bool GetGameMatchesFilter(Game game, FilterPresetSettings filterSettings) => throw new NotImplementedException();
        public bool GetGameMatchesFilter(Game game, FilterPresetSettings filterSettings, bool useFuzzyNameMatch) => throw new NotImplementedException();
        public IEnumerable<Game> GetFilteredGames(FilterPresetSettings filterSettings) => throw new NotImplementedException();
        public IEnumerable<Game> GetFilteredGames(FilterPresetSettings filterSettings, bool useFuzzyNameMatch) => throw new NotImplementedException();
    }

    internal sealed class FakePlayniteAPI : IPlayniteAPI
    {
        public FakePlayniteAPI(IEnumerable<Game> games) => Database = new FakeGameDatabase(games);

        public IGameDatabaseAPI Database { get; }

        // Unused surface.
        public IMainViewAPI MainView => throw new NotImplementedException();
        public IDialogsFactory Dialogs => throw new NotImplementedException();
        public IPlaynitePathsAPI Paths => throw new NotImplementedException();
        public INotificationsAPI Notifications => throw new NotImplementedException();
        public IPlayniteInfoAPI ApplicationInfo => throw new NotImplementedException();
        public IWebViewFactory WebViews => throw new NotImplementedException();
        public IResourceProvider Resources => throw new NotImplementedException();
        public IUriHandlerAPI UriHandler => throw new NotImplementedException();
        public IPlayniteSettingsAPI ApplicationSettings => throw new NotImplementedException();
        public IAddons Addons => throw new NotImplementedException();
        public IEmulationAPI Emulation => throw new NotImplementedException();
        public string ExpandGameVariables(Game game, string inputString) => throw new NotImplementedException();
        public string ExpandGameVariables(Game game, string inputString, string emulatorDir) => throw new NotImplementedException();
        public GameAction ExpandGameVariables(Game game, GameAction action) => throw new NotImplementedException();
        public void StartGame(Guid gameId) => throw new NotImplementedException();
        public void InstallGame(Guid gameId) => throw new NotImplementedException();
        public void UninstallGame(Guid gameId) => throw new NotImplementedException();
        public void AddCustomElementSupport(Plugin source, AddCustomElementSupportArgs args) => throw new NotImplementedException();
        public void AddSettingsSupport(Plugin source, AddSettingsSupportArgs args) => throw new NotImplementedException();
        public void AddConvertersSupport(Plugin source, AddConvertersSupportArgs args) => throw new NotImplementedException();
        public List<Playnite.SDK.Events.GamepadController> GetConnectedControllers() => throw new NotImplementedException();
    }
}
