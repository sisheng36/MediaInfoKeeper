using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public sealed class StrmFileWatcher : IDisposable
    {
        private readonly IHttpClient httpClient;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly LibraryService libraryService;
        private readonly ILogger logger;
        private readonly string baseUrl;
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
            IHttpClient httpClient,
            ILibraryMonitor libraryMonitor,
            IServerConfigurationManager serverConfig,
            LibraryService libraryService,
            ILogger logger)
        {
            this.httpClient = httpClient;
            this.libraryMonitor = libraryMonitor;
            this.libraryService = libraryService;
            this.logger = logger;

            var cfg = serverConfig.Configuration;
            var port = cfg.PublicPort > 0 ? cfg.PublicPort : cfg.HttpServerPort;
            this.baseUrl = $"http://localhost:{port}";
        }

        public void Configure(bool isEnabled, int delaySeconds)
        {
            if (this.disposed)
                return;

            this.enabled = isEnabled;
            RebuildWatchers(isEnabled);
        }

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
                    catch { }
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

        private void OnCreated(string path)
        {
            if (!IsWatchedMediaFile(path))
                return;

            var directoryPath = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directoryPath))
                return;

            var shouldReportDirectory = RecordCreatedEvent(directoryPath, path);
            this.logger?.Info($"新增媒体文件，{Path.GetFileName(path) ?? path}");
            if (!shouldReportDirectory)
                return;

            try
            {
                Task.Run(async () => await NotifyMediaUpdated(directoryPath).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                this.logger?.Error($"StrmFileWatcher 通知 Emby 入库扫描失败: {directoryPath}");
                this.logger?.Error(ex.Message);
            }
        }

        private void OnModified(string path)
        {
            if (!IsWatchedShortcut(path))
                return;

            if (ShouldSkipModifiedLog(path))
                return;

            this.logger?.Info($"{Path.GetFileName(path) ?? path} 内容修改");
        }

        private async Task NotifyMediaUpdated(string directoryPath)
        {
            try
            {
                var url = $"{this.baseUrl}/Library/Media/Updated?path={Uri.EscapeDataString(directoryPath)}";
                var options = new HttpRequestOptions
                {
                    Url = url,
                    LogRequest = false,
                    LogResponse = false,
                    TimeoutMs = 30000
                };
                using var response = await this.httpClient.Post(options).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent)
                {
                    this.logger?.Info($"StrmFileWatcher 精准扫描目录: {directoryPath}");
                }
                else
                {
                    this.logger?.Warn($"StrmFileWatcher /Library/Media/Updated 返回异常: {response.StatusCode}");
                    this.libraryMonitor?.ReportFileSystemChanged(directoryPath);
                }
            }
            catch (Exception ex)
            {
                this.logger?.Warn($"StrmFileWatcher /Library/Media/Updated 调用失败: {ex.Message}");
                this.libraryMonitor?.ReportFileSystemChanged(directoryPath);
            }
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
                   (Plugin.LibraryManager.IsVideoFile(path.AsSpan()) ||
                    Plugin.LibraryManager.IsAudioFile(path.AsSpan()));
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

        public void Dispose()
        {
            if (this.disposed)
                return;

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
                    catch { }
                }

                this.watchers.Clear();
                this.createdEvents.Clear();
                this.lastModifiedEvents.Clear();
            }
        }
    }
}
