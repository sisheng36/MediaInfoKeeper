using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaInfoKeeper.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    public static class TmdbPersonUpdate
    {
        private static readonly object InitLock = new object();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo updatePeopleMethod;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            lock (InitLock)
            {
                if (harmony != null)
                {
                    Configure(enable);
                    return;
                }

                var libraryManagerType = Plugin.LibraryManager?.GetType() ??
                                         Type.GetType("Emby.Server.Implementations.Library.LibraryManager, Emby.Server.Implementations");
                if (libraryManagerType == null)
                {
                    PatchLog.InitFailed(logger, nameof(TmdbPersonUpdate), "LibraryManager 类型缺失");
                    return;
                }

                var version = libraryManagerType.Assembly.GetName().Version;
                updatePeopleMethod = PatchMethodResolver.Resolve(
                    libraryManagerType,
                    version,
                    new MethodSignatureProfile
                    {
                        Name = "librarymanager-updatepeople-exact",
                        MethodName = "UpdatePeople",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        ParameterTypes = new[] { typeof(BaseItem), typeof(List<PersonInfo>), typeof(bool) },
                        ReturnType = typeof(void),
                        IsStatic = false
                    },
                    logger,
                    "TmdbPersonUpdate.LibraryManager.UpdatePeople");

                if (updatePeopleMethod == null)
                {
                    PatchLog.InitFailed(logger, nameof(TmdbPersonUpdate), "UpdatePeople 目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.tmdbpersonupdate");
                if (isEnabled)
                {
                    Patch();
                }
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;
            if (harmony == null)
            {
                return;
            }

            if (enable)
            {
                Patch();
            }
            else
            {
                Unpatch();
            }
        }

        private static void Patch()
        {
            if (isPatched || harmony == null || updatePeopleMethod == null)
            {
                return;
            }

            harmony.Patch(
                updatePeopleMethod,
                prefix: new HarmonyMethod(typeof(TmdbPersonUpdate), nameof(UpdatePeoplePrefix)));
            PatchLog.Patched(logger, nameof(TmdbPersonUpdate), updatePeopleMethod);
            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null || updatePeopleMethod == null)
            {
                return;
            }

            harmony.Unpatch(updatePeopleMethod, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static void UpdatePeoplePrefix(BaseItem item, List<PersonInfo> people)
        {
            if (!isEnabled || item == null || people == null || people.Count == 0)
            {
                return;
            }

            try
            {
                SyncTmdbNames(item, people);
            }
            catch (Exception ex)
            {
                logger?.Error("TmdbPersonUpdate prefix 异常: {0}", ex);
            }
        }

        private static void SyncTmdbNames(BaseItem item, List<PersonInfo> people)
        {
            var libraryManager = Plugin.LibraryManager;
            if (libraryManager == null)
            {
                return;
            }

            var itemLabel = FormatItemLabel(item);

            foreach (var person in people)
            {
                if (!ShouldUpdatePersonName(person, out var tmdbPersonId))
                {
                    continue;
                }

                if (!long.TryParse(tmdbPersonId, out _))
                {
                    continue;
                }

                var existingPeople = libraryManager.GetPeopleItems(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { typeof(Person).Name },
                    Recursive = true,
                    AnyProviderIdEquals = new ProviderIdDictionary
                    {
                        [MetadataProviders.Tmdb.ToString()] = tmdbPersonId
                    }
                });

                var existingPerson = existingPeople?.Items?.OfType<Person>().FirstOrDefault();
                if (existingPerson == null)
                {
                    continue;
                }

                var currentName = existingPerson.Name?.Trim();
                var newName = person.Name?.Trim();
                if (string.IsNullOrWhiteSpace(newName) ||
                    string.Equals(currentName, newName, StringComparison.Ordinal))
                {
                    continue;
                }

                existingPerson.Name = newName;
                existingPerson.UpdateToRepository(ItemUpdateType.MetadataImport);
                logger?.Info(
                    "TMDB演员名称已更新 {0}: {1} -> {2} tmdbPersonId={3}",
                    itemLabel,
                    string.IsNullOrWhiteSpace(currentName) ? "空" : currentName,
                    newName,
                    tmdbPersonId);
            }
        }

        private static string FormatItemLabel(BaseItem item)
        {
            if (item == null)
            {
                return "未知条目";
            }

            var name = string.IsNullOrWhiteSpace(item.FileNameWithoutExtension)
                ? (item.FileName ?? "未知条目")
                : item.Name;

            return name;
        }

        private static bool ShouldUpdatePersonName(PersonInfo person, out string tmdbPersonId)
        {
            tmdbPersonId = null;
            if (person == null)
            {
                return false;
            }

            var name = person.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            tmdbPersonId = person.GetProviderId(MetadataProviders.Tmdb);
            return !string.IsNullOrWhiteSpace(tmdbPersonId);
        }
    }
}
