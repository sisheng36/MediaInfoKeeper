using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    /// <summary>
    /// 监听媒体库路径下的新入库 .strm 文件，记录 Created 与 Changed 事件日志。
    /// </summary>
    public sealed class StrmFileWatcher : IDisposable
    {
        private readonly ILibraryMonitor libraryMonitor;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly LibraryService libraryService;
        private readonly ILogger logger;
        private readonly object syncRoot = new object();
        private readonly TimeSpan directoryReportDedupeWindow = TimeSpan.FromSeconds(2);
        private readonly TimeSpan modifiedEventDedupeWindow = TimeSpan.FromMilliseconds(100);

        private readonly Dictionary<string, FileSystemWatcher> watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> createdEvents =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> lastModifiedEvents =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private volatile bool enabled;
        private volatile bool disposed;

        public StrmFileWatcher(
            ILibraryMonitor libraryMonitor,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            LibraryService libraryService,
            ILogger logger)
        {
            this.libraryMonitor = libraryMonitor;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.libraryService = libraryService;
            this.logger = logger;
        }

        /// <summary>
        /// 配置监听开关。
        /// </summary>
        public void Configure(bool isEnabled, int delaySeconds)
        {
            if (this.disposed)
            {
                return;
            }

            this.enabled = isEnabled;
            RebuildWatchers(isEnabled);
        }

        /// <summary>
        /// 根据当前配置重建文件监听器。
        /// </summary>
        private void RebuildWatchers(bool isEnabled)
        {
            lock (this.syncRoot)
            {
                foreach (var existing in this.watchers.Values)
                {
                    try
                    {
                        existing.EnableRaisingEvents = false;
                        existing.Dispose();
                    }
                    catch
                    {
                    }
                }

                this.watchers.Clear();
                this.createdEvents.Clear();
                this.lastModifiedEvents.Clear();

                if (!isEnabled)
                {
                    this.logger?.Info("StrmFileWatcher 已禁用");
                    return;
                }

                var roots = (this.libraryService?.GetAllLibraryPaths() ?? new List<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var root in roots)
                {
                    try
                    {
                        var watcher = new FileSystemWatcher(root, "*")
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                            InternalBufferSize = 64 * 1024,
                            EnableRaisingEvents = true
                        };

                        watcher.Created += (sender, args) => OnCreated(args?.FullPath);
                        watcher.Changed += (sender, args) => OnModified(args?.FullPath);
                        this.watchers[root] = watcher;
                    }
                    catch (Exception ex)
                    {
                        this.logger?.Warn($"StrmFileWatcher 监听路径失败: {root}");
                        this.logger?.Warn(ex.Message);
                    }
                }

                this.logger?.Debug(
                    $"StrmFileWatcher 已启动，监听路径: {string.Join(", ", this.watchers.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))}");
            }
        }

        /// <summary>
        /// 记录新增文件事件。
        /// </summary>
        private void OnCreated(string path)
        {
            if (!IsWatchedMediaFile(path))
            {
                return;
            }

            var directoryPath = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return;
            }

            var shouldReportDirectory = RecordCreatedEvent(directoryPath, path);
            this.logger?.Info($"新增媒体文件，{Path.GetFileName(path) ?? path}");
            if (!shouldReportDirectory)
            {
                return;
            }

            try
            {
                Task.Run(async () => await TriggerPreciseFolderRefresh(directoryPath).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                this.logger?.Error($"StrmFileWatcher 通知 Emby 入库扫描失败: {directoryPath}");
                this.logger?.Error(ex.Message);
            }
        }

        /// <summary>
        /// 记录文件内容修改事件。
        /// </summary>
        private void OnModified(string path)
        {
            if (!IsWatchedShortcut(path))
            {
                return;
            }

            if (ShouldSkipModifiedLog(path))
            {
                return;
            }

            this.logger?.Info($"{Path.GetFileName(path) ?? path} 内容修改");
        }

        private bool IsWatchedShortcut(string path)
        {
            return this.enabled &&
                   !this.disposed &&
                   !string.IsNullOrWhiteSpace(path) &&
                   LibraryService.IsFileShortcut(path);
        }

        private bool IsWatchedMediaFile(string path)
        {
            return this.enabled &&
                   !this.disposed &&
                   !string.IsNullOrWhiteSpace(path) &&
                   (this.libraryManager.IsVideoFile(path.AsSpan()) ||
                    this.libraryManager.IsAudioFile(path.AsSpan()));
        }

        private bool RecordCreatedEvent(string directoryPath, string path)
        {
            var now = DateTime.UtcNow;

            lock (this.syncRoot)
            {
                var shouldReportDirectory = !this.createdEvents.TryGetValue(directoryPath, out var createdAt) ||
                                            now - createdAt >= this.directoryReportDedupeWindow;
                this.createdEvents[directoryPath] = now;
                this.createdEvents[path] = now;
                this.lastModifiedEvents[path] = now;
                PruneEventCache(this.createdEvents, now);
                PruneEventCache(this.lastModifiedEvents, now);
                return shouldReportDirectory;
            }
        }

        private bool ShouldSkipModifiedLog(string path)
        {
            var now = DateTime.UtcNow;

            lock (this.syncRoot)
            {
                if (this.createdEvents.TryGetValue(path, out var createdAt) &&
                    now - createdAt < this.modifiedEventDedupeWindow)
                {
                    this.lastModifiedEvents[path] = now;
                    PruneEventCache(this.createdEvents, now);
                    PruneEventCache(this.lastModifiedEvents, now);
                    return true;
                }

                if (this.lastModifiedEvents.TryGetValue(path, out var lastSeen) &&
                    now - lastSeen < this.modifiedEventDedupeWindow)
                {
                    return true;
                }

                this.lastModifiedEvents[path] = now;
                PruneEventCache(this.createdEvents, now);
                PruneEventCache(this.lastModifiedEvents, now);

                return false;
            }
        }

        private void PruneEventCache(Dictionary<string, DateTime> events, DateTime now)
        {
            var staleBefore = now - this.modifiedEventDedupeWindow;
            var stalePaths = events
                .Where(pair => pair.Value < staleBefore)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var stalePath in stalePaths)
            {
                events.Remove(stalePath);
            }
        }

        private async Task TriggerPreciseFolderRefresh(string originalPath)
        {
            var currentPath = originalPath;
            for (int i = 0; i < 4; i++)
            {
                if (string.IsNullOrWhiteSpace(currentPath))
                    break;

                try
                {
                    var query = new MediaBrowser.Controller.Entities.InternalItemsQuery
                    {
                        PathStartsWithAny = new[] { currentPath },
                        IsFolder = true,
                        Limit = 1,
                        Recursive = false
                    };
                    var folders = this.libraryManager.GetItemList(query);
                    var folder = folders?.FirstOrDefault();

                    if (folder != null)
                    {
                        if (folder is CollectionFolder)
                        {
                            this.logger?.Info($"StrmFileWatcher 新目录无法精准扫描，回退全库扫描: {originalPath}");
                            this.libraryMonitor?.ReportFileSystemChanged(originalPath);
                            return;
                        }

                        var refreshOptions = new MetadataRefreshOptions(new DirectoryService(this.fileSystem))
                        {
                            EnableRemoteContentProbe = false,
                            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                            ReplaceAllImages = false,
                            ReplaceAllMetadata = false,
                            EnableThumbnailImageExtraction = false,
                            EnableSubtitleDownloading = false,
                            IsAutomated = true
                        };
                        Traverse.Create(refreshOptions).Property("Recursive").SetValue(true);

                        this.logger?.Info($"StrmFileWatcher 精准扫描目录: {currentPath}");
                        await this.providerManager.RefreshFullItem(folder, refreshOptions, CancellationToken.None)
                            .ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    this.logger?.Warn($"StrmFileWatcher 尝试精准扫描失败 ({currentPath}): {ex.Message}");
                }

                var parent = Path.GetDirectoryName(currentPath);
                if (string.IsNullOrEmpty(parent) || parent == currentPath)
                    break;
                currentPath = parent;
            }

            this.logger?.Info($"StrmFileWatcher 未找到匹配目录，回退全库扫描: {originalPath}");
            this.libraryMonitor?.ReportFileSystemChanged(originalPath);
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.enabled = false;

            lock (this.syncRoot)
            {
                foreach (var watcher in this.watchers.Values)
                {
                    try
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                    }
                    catch
                    {
                    }
                }

                this.watchers.Clear();
                this.createdEvents.Clear();
                this.lastModifiedEvents.Clear();
            }
        }
    }
}
