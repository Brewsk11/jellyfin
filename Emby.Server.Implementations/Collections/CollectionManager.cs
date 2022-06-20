using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Emby.Server.Implementations.Collections
{
    /// <summary>
    /// The collection manager.
    /// </summary>
    public class CollectionManager : ICollectionManager
    {
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryMonitor _iLibraryMonitor;
        private readonly ILogger<CollectionManager> _logger;
        private readonly IProviderManager _providerManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IApplicationPaths _appPaths;

        /// <summary>
        /// Initializes a new instance of the <see cref="CollectionManager"/> class.
        /// </summary>
        /// <param name="userManager">The user manager.</param>
        /// <param name="userDataManager">The user data manager.</param>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="appPaths">The application paths.</param>
        /// <param name="localizationManager">The localization manager.</param>
        /// <param name="fileSystem">The filesystem.</param>
        /// <param name="iLibraryMonitor">The library monitor.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="providerManager">The provider manager.</param>
        public CollectionManager(
            IUserManager userManager,
            IUserDataManager userDataManager,
            IServerConfigurationManager configurationManager,
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            ILocalizationManager localizationManager,
            IFileSystem fileSystem,
            ILibraryMonitor iLibraryMonitor,
            ILoggerFactory loggerFactory,
            IProviderManager providerManager)
        {
            _userManager = userManager;
            _userDataManager = userDataManager;
            _configurationManager = configurationManager;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _iLibraryMonitor = iLibraryMonitor;
            _logger = loggerFactory.CreateLogger<CollectionManager>();
            _providerManager = providerManager;
            _localizationManager = localizationManager;
            _appPaths = appPaths;
        }

        /// <inheritdoc />
        public event EventHandler<CollectionCreatedEventArgs>? CollectionCreated;

        /// <inheritdoc />
        public event EventHandler<CollectionModifiedEventArgs>? ItemsAddedToCollection;

        /// <inheritdoc />
        public event EventHandler<CollectionModifiedEventArgs>? ItemsRemovedFromCollection;

        private IEnumerable<Folder> FindFolders(string path)
        {
            return _libraryManager
                .RootFolder
                .Children
                .OfType<Folder>()
                .Where(i => _fileSystem.AreEqual(path, i.Path) || _fileSystem.ContainsSubPath(i.Path, path));
        }

        internal async Task<Folder?> EnsureLibraryFolder(string path, bool createIfNeeded)
        {
            var existingFolder = FindFolders(path).FirstOrDefault();
            if (existingFolder != null)
            {
                return existingFolder;
            }

            if (!createIfNeeded)
            {
                return null;
            }

            Directory.CreateDirectory(path);

            var libraryOptions = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo(path) },
                EnableRealtimeMonitor = false,
                SaveLocalMetadata = true
            };

            var name = _localizationManager.GetLocalizedString("Collections");

            await _libraryManager.AddVirtualFolder(name, CollectionTypeOptions.BoxSets, libraryOptions, true).ConfigureAwait(false);

            return FindFolders(path).First();
        }

        internal string GetCollectionsFolderPath()
        {
            return Path.Combine(_appPaths.DataPath, "collections");
        }

        private Task<Folder?> GetCollectionsFolder(bool createIfNeeded)
        {
            return EnsureLibraryFolder(GetCollectionsFolderPath(), createIfNeeded);
        }

        private IEnumerable<BoxSet> GetCollections(User user)
        {
            var folder = GetCollectionsFolder(false).GetAwaiter().GetResult();

            return folder == null
                ? Enumerable.Empty<BoxSet>()
                : folder.GetChildren(user, true).OfType<BoxSet>();
        }

        /// <inheritdoc />
        public async Task<BoxSet> CreateCollectionAsync(CollectionCreationOptions options)
        {
            var name = options.Name;

            // Need to use the [boxset] suffix
            // If internet metadata is not found, or if xml saving is off there will be no collection.xml
            // This could cause it to get re-resolved as a plain folder
            var folderName = _fileSystem.GetValidFilename(name) + " [boxset]";

            var parentFolder = await GetCollectionsFolder(true).ConfigureAwait(false);

            if (parentFolder == null)
            {
                throw new ArgumentException(nameof(parentFolder));
            }

            var path = Path.Combine(parentFolder.Path, folderName);

            _iLibraryMonitor.ReportFileSystemChangeBeginning(path);

            try
            {
                Directory.CreateDirectory(path);

                var collection = new BoxSet
                {
                    Name = name,
                    Path = path,
                    IsLocked = options.IsLocked,
                    ProviderIds = options.ProviderIds,
                    DateCreated = DateTime.UtcNow
                };

                parentFolder.AddChild(collection);

                if (options.ItemIdList.Count > 0)
                {
                    await AddToCollectionAsync(
                        collection.Id,
                        options.ItemIdList.Select(x => new Guid(x)),
                        false,
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            // The initial adding of items is going to create a local metadata file
                            // This will cause internet metadata to be skipped as a result
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                        }).ConfigureAwait(false);
                }
                else
                {
                    _providerManager.QueueRefresh(collection.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
                }

                CollectionCreated?.Invoke(this, new CollectionCreatedEventArgs
                {
                    Collection = collection,
                    Options = options
                });

                return collection;
            }
            finally
            {
                // Refresh handled internally
                _iLibraryMonitor.ReportFileSystemChangeComplete(path, false);
            }
        }

        /// <inheritdoc />
        public Task AddToCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds)
            => AddToCollectionAsync(collectionId, itemIds, true, new MetadataRefreshOptions(new DirectoryService(_fileSystem)));

        private async Task AddToCollectionAsync(Guid collectionId, IEnumerable<Guid> ids, bool fireEvent, MetadataRefreshOptions refreshOptions)
        {
            if (_libraryManager.GetItemById(collectionId) is not BoxSet collection)
            {
                throw new ArgumentException("No collection exists with the supplied Id");
            }

            var list = new List<LinkedChild>();
            var itemList = new List<BaseItem>();

            var linkedChildrenList = collection.GetLinkedChildren();
            var currentLinkedChildrenIds = linkedChildrenList.Select(i => i.Id).ToList();

            foreach (var id in ids)
            {
                var item = _libraryManager.GetItemById(id);

                if (item == null)
                {
                    throw new ArgumentException("No item exists with the supplied Id");
                }

                if (!currentLinkedChildrenIds.Contains(id))
                {
                    itemList.Add(item);

                    list.Add(LinkedChild.Create(item));
                    linkedChildrenList.Add(item);
                }
            }

            if (list.Count > 0)
            {
                var newList = collection.LinkedChildren.ToList();
                newList.AddRange(list);
                collection.LinkedChildren = newList.ToArray();

                collection.UpdateRatingToItems(linkedChildrenList);

                await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

                refreshOptions.ForceSave = true;
                _providerManager.QueueRefresh(collection.Id, refreshOptions, RefreshPriority.High);

                if (fireEvent)
                {
                    ItemsAddedToCollection?.Invoke(this, new CollectionModifiedEventArgs(collection, itemList));
                }
            }
        }

        /// <inheritdoc />
        public async Task RemoveFromCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds)
        {
            if (_libraryManager.GetItemById(collectionId) is not BoxSet collection)
            {
                throw new ArgumentException("No collection exists with the supplied Id");
            }

            var list = new List<LinkedChild>();
            var itemList = new List<BaseItem>();

            foreach (var guidId in itemIds)
            {
                var childItem = _libraryManager.GetItemById(guidId);

                var child = collection.LinkedChildren.FirstOrDefault(i => (i.ItemId.HasValue && i.ItemId.Value.Equals(guidId)) || (childItem != null && string.Equals(childItem.Path, i.Path, StringComparison.OrdinalIgnoreCase)));

                if (child == null)
                {
                    _logger.LogWarning("No collection title exists with the supplied Id");
                    continue;
                }

                list.Add(child);

                if (childItem != null)
                {
                    itemList.Add(childItem);
                }
            }

            if (list.Count > 0)
            {
                collection.LinkedChildren = collection.LinkedChildren.Except(list).ToArray();
            }

            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            _providerManager.QueueRefresh(
                collection.Id,
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    ForceSave = true
                },
                RefreshPriority.High);

            ItemsRemovedFromCollection?.Invoke(this, new CollectionModifiedEventArgs(collection, itemList));
        }

        /// <inheritdoc />
        public IEnumerable<BaseItem> CollapseItemsWithinBoxSets(IEnumerable<BaseItem> items, User user)
        {
            var results = new Dictionary<Guid, BaseItem>();

            var allBoxSets = GetCollections(user).ToList();

            foreach (var item in items)
            {
                if (item is ISupportsBoxSetGrouping)
                {
                    var itemId = item.Id;

                    var itemIsInBoxSet = false;
                    foreach (var boxSet in allBoxSets)
                    {
                        if (!boxSet.ContainsLinkedChildByItemId(itemId))
                        {
                            continue;
                        }

                        itemIsInBoxSet = true;

                        results.TryAdd(boxSet.Id, boxSet);
                    }

                    // skip any item that is in a box set
                    if (itemIsInBoxSet)
                    {
                        continue;
                    }

                    var alreadyInResults = false;

                    // this is kind of a performance hack because only Video has alternate versions that should be in a box set?
                    if (item is Video video)
                    {
                        foreach (var childId in video.GetLocalAlternateVersionIds())
                        {
                            if (!results.ContainsKey(childId))
                            {
                                continue;
                            }

                            alreadyInResults = true;
                            break;
                        }
                    }

                    if (alreadyInResults)
                    {
                        continue;
                    }
                }

                results[item.Id] = item;
            }

            return results.Values;
        }

        public QueryResult<BaseItem> GetNextUp(NextUpMoviesQuery query, DtoOptions options)
        {
            var user = _userManager.GetUserById(query.UserId);

            if (user == null)
            {
                throw new ArgumentException("User not found");
            }

            string presentationUniqueKey = null;
            if (!string.IsNullOrEmpty(query.SeriesId))
            {
                if (_libraryManager.GetItemById(query.SeriesId) is Series series)
                {
                    presentationUniqueKey = GetUniqueSeriesKey(series);
                }
            }

            if (!string.IsNullOrEmpty(presentationUniqueKey))
            {
                return GetResult(GetNextUpEpisodes(query, user, new[] { presentationUniqueKey }, options), query);
            }

            BaseItem[] parents;

            if (query.ParentId.HasValue)
            {
                var parent = _libraryManager.GetItemById(query.ParentId.Value);

                if (parent != null)
                {
                    parents = new[] { parent };
                }
                else
                {
                    parents = Array.Empty<BaseItem>();
                }
            }
            else
            {
                parents = _libraryManager.GetUserRootFolder().GetChildren(user, true)
                   .Where(i => i is Folder)
                   .Where(i => !user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes).Contains(i.Id))
                   .ToArray();
            }

            return GetNextUp(query, parents, options);
        }

        public QueryResult<BaseItem> GetNextUp(NextUpMoviesQuery request, BaseItem[] parentsFolders, DtoOptions options)
        {
            var user = _userManager.GetUserById(request.UserId);

            if (user == null)
            {
                throw new ArgumentException("User not found");
            }

            string presentationUniqueKey = null;
            int? limit = null;
            if (!string.IsNullOrEmpty(request.SeriesId))
            {
                if (_libraryManager.GetItemById(request.SeriesId) is Series series)
                {
                    presentationUniqueKey = GetUniqueSeriesKey(series);
                    limit = 1;
                }
            }

            if (!string.IsNullOrEmpty(presentationUniqueKey))
            {
                return GetResult(GetNextUpEpisodes(request, user, new[] { presentationUniqueKey }, options), request);
            }

            if (limit.HasValue)
            {
                limit = limit.Value + 10;
            }

            var items = _libraryManager
                .GetItemList(
                    new InternalItemsQuery(user)
                    {
                        IncludeItemTypes = new[] { BaseItemKind.Episode },
                        OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                        SeriesPresentationUniqueKey = presentationUniqueKey,
                        Limit = limit,
                        DtoOptions = new DtoOptions { Fields = new[] { ItemFields.SeriesPresentationUniqueKey }, EnableImages = false },
                        GroupBySeriesPresentationUniqueKey = true
                    },
                    parentsFolders.ToList())
                .Cast<Episode>()
                .Where(episode => !string.IsNullOrEmpty(episode.SeriesPresentationUniqueKey))
                .Select(GetUniqueSeriesKey)
                .ToList();

            // Avoid implicitly captured closure
            var episodes = GetNextUpEpisodes(request, user, items, options);

            return GetResult(episodes, request);
        }

        public IEnumerable<Episode> GetNextUpEpisodes(NextUpMoviesQuery request, User user, IReadOnlyList<string> seriesKeys, DtoOptions dtoOptions)
        {
            // Avoid implicitly captured closure
            var currentUser = user;

            var allNextUp = seriesKeys
                .Select(i => GetNextUp(i, currentUser, dtoOptions, false));

            if (request.EnableRewatching)
            {
                allNextUp = allNextUp.Concat(
                    seriesKeys.Select(i => GetNextUp(i, currentUser, dtoOptions, true))
                )
                .OrderByDescending(i => i.Item1);
            }

            // If viewing all next up for all series, remove first episodes
            // But if that returns empty, keep those first episodes (avoid completely empty view)
            var alwaysEnableFirstEpisode = !string.IsNullOrEmpty(request.SeriesId);
            var anyFound = false;

            return allNextUp
                .Where(i =>
                {
                    if (request.DisableFirstEpisode)
                    {
                        return i.Item1 != DateTime.MinValue;
                    }

                    if (alwaysEnableFirstEpisode || (i.Item1 != DateTime.MinValue && i.Item1.Date >= request.NextUpDateCutoff))
                    {
                        anyFound = true;
                        return true;
                    }

                    if (!anyFound && i.Item1 == DateTime.MinValue)
                    {
                        return true;
                    }

                    return false;
                })
                .Select(i => i.Item2())
                .Where(i => i != null);
        }

        private static string GetUniqueSeriesKey(Episode episode)
        {
            return episode.SeriesPresentationUniqueKey;
        }

        private static string GetUniqueSeriesKey(Series series)
        {
            return series.GetPresentationUniqueKey();
        }

        /// <summary>
        /// Gets the next up.
        /// </summary>
        /// <returns>Task{Episode}.</returns>
        private Tuple<DateTime, Func<Episode>> GetNextUp(string seriesKey, User user, DtoOptions dtoOptions, bool rewatching)
        {
            var lastQuery = new InternalItemsQuery(user)
            {
                AncestorWithPresentationUniqueKey = null,
                SeriesPresentationUniqueKey = seriesKey,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Descending) },
                IsPlayed = true,
                Limit = 1,
                ParentIndexNumberNotEquals = 0,
                DtoOptions = new DtoOptions
                {
                    Fields = new[] { ItemFields.SortName },
                    EnableImages = false
                }
            };

            if (rewatching)
            {
                // find last watched by date played, not by newest episode watched
                lastQuery.OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) };
            }

            var lastWatchedEpisode = _libraryManager.GetItemList(lastQuery).Cast<Episode>().FirstOrDefault();

            Func<Episode> getEpisode = () =>
            {
                var nextQuery = new InternalItemsQuery(user)
                {
                    AncestorWithPresentationUniqueKey = null,
                    SeriesPresentationUniqueKey = seriesKey,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                    Limit = 1,
                    IsPlayed = rewatching,
                    IsVirtualItem = false,
                    ParentIndexNumberNotEquals = 0,
                    MinSortName = lastWatchedEpisode?.SortName,
                    DtoOptions = dtoOptions
                };

                Episode nextEpisode;
                if (rewatching)
                {
                    nextQuery.Limit = 2;
                    // get watched episode after most recently watched
                    nextEpisode = _libraryManager.GetItemList(nextQuery).Cast<Episode>().ElementAtOrDefault(1);
                }
                else
                {
                    nextEpisode = _libraryManager.GetItemList(nextQuery).Cast<Episode>().FirstOrDefault();
                }

                if (_configurationManager.Configuration.DisplaySpecialsWithinSeasons)
                {
                    var consideredEpisodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
                    {
                        AncestorWithPresentationUniqueKey = null,
                        SeriesPresentationUniqueKey = seriesKey,
                        ParentIndexNumber = 0,
                        IncludeItemTypes = new[] { BaseItemKind.Episode },
                        IsPlayed = rewatching,
                        IsVirtualItem = false,
                        DtoOptions = dtoOptions
                    })
                    .Cast<Episode>()
                    .Where(episode => episode.AirsBeforeSeasonNumber != null || episode.AirsAfterSeasonNumber != null)
                    .ToList();

                    if (lastWatchedEpisode != null)
                    {
                        // Last watched episode is added, because there could be specials that aired before the last watched episode
                        consideredEpisodes.Add(lastWatchedEpisode);
                    }

                    if (nextEpisode != null)
                    {
                        consideredEpisodes.Add(nextEpisode);
                    }

                    var sortedConsideredEpisodes = _libraryManager.Sort(consideredEpisodes, user, new[] { (ItemSortBy.AiredEpisodeOrder, SortOrder.Ascending) })
                        .Cast<Episode>();
                    if (lastWatchedEpisode != null)
                    {
                        sortedConsideredEpisodes = sortedConsideredEpisodes.SkipWhile(episode => !episode.Id.Equals(lastWatchedEpisode.Id)).Skip(1);
                    }

                    nextEpisode = sortedConsideredEpisodes.FirstOrDefault();
                }

                if (nextEpisode != null)
                {
                    var userData = _userDataManager.GetUserData(user, nextEpisode);

                    if (userData.PlaybackPositionTicks > 0)
                    {
                        return null;
                    }
                }

                return nextEpisode;
            };

            if (lastWatchedEpisode != null)
            {
                var userData = _userDataManager.GetUserData(user, lastWatchedEpisode);

                var lastWatchedDate = userData.LastPlayedDate ?? DateTime.MinValue.AddDays(1);

                return new Tuple<DateTime, Func<Episode>>(lastWatchedDate, getEpisode);
            }

            // Return the first episode
            return new Tuple<DateTime, Func<Episode>>(DateTime.MinValue, getEpisode);
        }

        private static QueryResult<BaseItem> GetResult(IEnumerable<BaseItem> items, NextUpMoviesQuery query)
        {
            int totalCount = 0;

            if (query.EnableTotalRecordCount)
            {
                var list = items.ToList();
                totalCount = list.Count;
                items = list;
            }

            if (query.StartIndex.HasValue)
            {
                items = items.Skip(query.StartIndex.Value);
            }

            if (query.Limit.HasValue)
            {
                items = items.Take(query.Limit.Value);
            }

            return new QueryResult<BaseItem>(
                query.StartIndex,
                totalCount,
                items.ToArray());
        }
    }
}
