using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace LobotomyBaseModLib
{
    /// <summary>
    /// Two-pass, in-memory mod assembly catalog and loader for LMM.
    /// Adapts legacy Harmony 1 references in memory without altering files on disk,
    /// deduplicates shared libraries across mod directories by highest version, and
    /// serves dependencies via AppDomain.AssemblyResolve before mod types are inspected.
    /// </summary>
    public static class LmmModLoader
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> ByFilePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Candidate> CanonicalBySimpleName = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> DiscoveryOrder = new List<string>();
        private static readonly HashSet<string> LoadingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ClaimedEntryPoints = new HashSet<string>(StringComparer.Ordinal);
        private static bool resolverRegistered;
        private static Func<byte[], string, Assembly> activeAdaptedLoader = MonoImageLoader.LoadOrReuseBySimpleName;
        private static Func<string, Assembly> activeDirectLoader = Assembly.LoadFile;
        private static string managedDirectory = DefaultManagedDirectory();

        internal sealed class Candidate
        {
            internal string FilePath;
            internal string SimpleName;
            internal string FullName;
            internal Version Version;
            internal byte[] RewrittenBytes;
            internal bool Changed;
            internal Assembly LoadedAssembly;
        }

        internal static void ResetForTests()
        {
            lock (Sync)
            {
                ByFilePath.Clear();
                CanonicalBySimpleName.Clear();
                DiscoveryOrder.Clear();
                LoadingNames.Clear();
                ClaimedEntryPoints.Clear();
                activeAdaptedLoader = MonoImageLoader.LoadOrReuseBySimpleName;
                activeDirectLoader = Assembly.LoadFile;
                managedDirectory = DefaultManagedDirectory();
            }
        }

        /// <summary>
        /// Scan all mod directories, adapt legacy Harmony references in memory, register
        /// dependency resolution, and load canonical assemblies before any type inspection.
        /// </summary>
        public static void PrepareModDirectories(IList<DirectoryInfo> modDirectories, Action<string> logError)
        {
            PrepareModDirectories(
                modDirectories,
                logError,
                MonoImageLoader.LoadOrReuseBySimpleName,
                Assembly.LoadFile,
                DefaultManagedDirectory());
        }

        internal static void PrepareModDirectories(
            IList<DirectoryInfo> modDirectories,
            Action<string> logError,
            Func<byte[], string, Assembly> adaptedLoader,
            Func<string, Assembly> directLoader,
            string runtimeManagedDirectory)
        {
            if (modDirectories == null) throw new ArgumentNullException("modDirectories");
            if (adaptedLoader == null) throw new ArgumentNullException("adaptedLoader");
            if (directLoader == null) throw new ArgumentNullException("directLoader");

            lock (Sync)
            {
                activeAdaptedLoader = adaptedLoader;
                activeDirectLoader = directLoader;
                managedDirectory = string.IsNullOrEmpty(runtimeManagedDirectory) ? null : Path.GetFullPath(runtimeManagedDirectory);
                EnsureResolverRegistered();
                PrepareCatalogLocked(modDirectories, logError);

                for (int i = 0; i < DiscoveryOrder.Count; i++)
                {
                    Candidate candidate;
                    if (CanonicalBySimpleName.TryGetValue(DiscoveryOrder[i], out candidate))
                    {
                        try
                        {
                            EnsureCanonicalLoadedLocked(candidate);
                        }
                        catch (Exception ex)
                        {
                            if (logError != null)
                            {
                                logError("Mod load error - " + candidate.FilePath + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
                            }
                        }
                    }
                }
            }
        }

        internal static void PrepareCatalogOnly(
            IList<DirectoryInfo> modDirectories,
            Action<string> logError,
            string runtimeManagedDirectory)
        {
            if (modDirectories == null) throw new ArgumentNullException("modDirectories");
            lock (Sync)
            {
                managedDirectory = string.IsNullOrEmpty(runtimeManagedDirectory) ? null : Path.GetFullPath(runtimeManagedDirectory);
                PrepareCatalogLocked(modDirectories, logError);
            }
        }

        /// <summary>Return the loaded canonical assembly for a discovered mod DLL path, or null for unmanaged or failed files.</summary>
        public static Assembly GetLoadedAssemblyForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string normalized = Path.GetFullPath(filePath);
            lock (Sync)
            {
                string simpleName;
                if (!ByFilePath.TryGetValue(normalized, out simpleName) || simpleName == null)
                {
                    return null;
                }
                Candidate candidate;
                if (!CanonicalBySimpleName.TryGetValue(simpleName, out candidate))
                {
                    return null;
                }
                if (candidate.LoadedAssembly == null)
                {
                    return EnsureCanonicalLoadedLocked(candidate);
                }
                return candidate.LoadedAssembly;
            }
        }

        internal static Candidate GetCanonicalCandidate(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;
            lock (Sync)
            {
                Candidate candidate;
                return CanonicalBySimpleName.TryGetValue(simpleName, out candidate) ? candidate : null;
            }
        }

        internal static string GetMappedSimpleNameForFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string normalized = Path.GetFullPath(filePath);
            lock (Sync)
            {
                string simpleName;
                return ByFilePath.TryGetValue(normalized, out simpleName) ? simpleName : null;
            }
        }

        /// <summary>Ensure each mod entry point type and kind pair is initialized at most once across duplicate shared libraries.</summary>
        public static bool TryClaimEntryPoint(Assembly assembly, string typeFullName, string entryKind)
        {
            if (assembly == null || string.IsNullOrEmpty(typeFullName) || string.IsNullOrEmpty(entryKind))
            {
                return false;
            }
            string key = assembly.FullName + "|" + typeFullName + "|" + entryKind;
            lock (Sync)
            {
                return ClaimedEntryPoints.Add(key);
            }
        }

        internal static Assembly ResolveAssemblyByRequestName(string requestedAssemblyName)
        {
            return ResolveAssembly(null, new ResolveEventArgs(requestedAssemblyName));
        }

        private static void PrepareCatalogLocked(IList<DirectoryInfo> modDirectories, Action<string> logError)
        {
            ByFilePath.Clear();
            CanonicalBySimpleName.Clear();
            DiscoveryOrder.Clear();
            ClaimedEntryPoints.Clear();

            for (int d = 0; d < modDirectories.Count; d++)
            {
                DirectoryInfo dir = modDirectories[d];
                if (dir == null || !dir.Exists) continue;

                FileInfo[] files = dir.GetFiles();
                Array.Sort(files, delegate(FileInfo left, FileInfo right)
                {
                    return StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                });

                for (int f = 0; f < files.Length; f++)
                {
                    FileInfo file = files[f];
                    if (!string.Equals(file.Extension, ".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(file.FullName);
                    try
                    {
                        byte[] rawBytes = File.ReadAllBytes(fullPath);
                        string simpleName;
                        string fullName;
                        Version version;
                        if (!TryReadAssemblyMetadata(rawBytes, out simpleName, out fullName, out version))
                        {
                            ByFilePath[fullPath] = null;
                            continue;
                        }

                        byte[] rewrittenBytes = rawBytes;
                        bool changed = false;
                        using (var resolver = CreateResolver(dir.FullName))
                        {
                            string[] observations;
                            rewrittenBytes = HarmonyReferenceAdapter.RewriteImage(rawBytes, resolver, out observations);
                            changed = !ReferenceEquals(rewrittenBytes, rawBytes);
                            if (observations != null && observations.Length != 0 && logError != null)
                            {
                                for (int o = 0; o < observations.Length; o++)
                                {
                                    logError("Harmony adapter note for " + fullPath + ": " + observations[o]);
                                }
                            }
                        }

                        ByFilePath[fullPath] = simpleName;
                        Candidate existing;
                        if (!CanonicalBySimpleName.TryGetValue(simpleName, out existing))
                        {
                            DiscoveryOrder.Add(simpleName);
                            CanonicalBySimpleName[simpleName] = new Candidate
                            {
                                FilePath = fullPath,
                                SimpleName = simpleName,
                                FullName = fullName,
                                Version = version,
                                RewrittenBytes = changed ? rewrittenBytes : null,
                                Changed = changed,
                                LoadedAssembly = null
                            };
                        }
                        else if (version > existing.Version)
                        {
                            CanonicalBySimpleName[simpleName] = new Candidate
                            {
                                FilePath = fullPath,
                                SimpleName = simpleName,
                                FullName = fullName,
                                Version = version,
                                RewrittenBytes = changed ? rewrittenBytes : null,
                                Changed = changed,
                                LoadedAssembly = existing.LoadedAssembly
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        ByFilePath[fullPath] = null;
                        if (logError != null)
                        {
                            logError("Mod scan error - " + fullPath + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
                        }
                    }
                }
            }
        }

        private static bool TryReadAssemblyMetadata(byte[] rawBytes, out string simpleName, out string fullName, out Version version)
        {
            try
            {
                using (var stream = new MemoryStream(rawBytes, false))
                using (var assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { ReadingMode = ReadingMode.Deferred }))
                {
                    simpleName = assembly.Name.Name;
                    fullName = assembly.Name.FullName;
                    version = assembly.Name.Version ?? new Version(0, 0, 0, 0);
                    return true;
                }
            }
            catch (BadImageFormatException)
            {
                simpleName = null;
                fullName = null;
                version = null;
                return false;
            }
        }

        private static DefaultAssemblyResolver CreateResolver(string modDirectory)
        {
            var resolver = new DefaultAssemblyResolver();
            if (!string.IsNullOrEmpty(modDirectory) && Directory.Exists(modDirectory))
            {
                resolver.AddSearchDirectory(modDirectory);
            }
            if (!string.IsNullOrEmpty(managedDirectory) && Directory.Exists(managedDirectory))
            {
                resolver.AddSearchDirectory(managedDirectory);
            }
            return resolver;
        }

        private static Assembly EnsureCanonicalLoadedLocked(Candidate candidate)
        {
            if (candidate.LoadedAssembly != null)
            {
                return candidate.LoadedAssembly;
            }

            foreach (Assembly existing in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(existing.GetName().Name, candidate.SimpleName, StringComparison.OrdinalIgnoreCase))
                {
                    candidate.LoadedAssembly = existing;
                    return existing;
                }
            }

            if (LoadingNames.Contains(candidate.SimpleName))
            {
                return null;
            }

            LoadingNames.Add(candidate.SimpleName);
            try
            {
                Assembly loaded = candidate.Changed
                    ? activeAdaptedLoader(candidate.RewrittenBytes, candidate.FilePath)
                    : activeDirectLoader(candidate.FilePath);
                candidate.LoadedAssembly = loaded;
                return loaded;
            }
            finally
            {
                LoadingNames.Remove(candidate.SimpleName);
            }
        }

        private static void EnsureResolverRegistered()
        {
            if (resolverRegistered) return;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
            resolverRegistered = true;
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            if (args == null || string.IsNullOrEmpty(args.Name)) return null;
            string requestedSimpleName;
            try
            {
                requestedSimpleName = new AssemblyName(args.Name).Name;
            }
            catch (Exception)
            {
                return null;
            }
            if (string.IsNullOrEmpty(requestedSimpleName)) return null;

            lock (Sync)
            {
                foreach (Assembly existing in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(existing.GetName().Name, requestedSimpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return existing;
                    }
                }

                Candidate candidate;
                if (CanonicalBySimpleName.TryGetValue(requestedSimpleName, out candidate))
                {
                    return EnsureCanonicalLoadedLocked(candidate);
                }

                if (!string.IsNullOrEmpty(managedDirectory))
                {
                    string managedCandidate = Path.Combine(managedDirectory, requestedSimpleName + ".dll");
                    if (File.Exists(managedCandidate))
                    {
                        return activeDirectLoader(managedCandidate);
                    }
                }
            }
            return null;
        }

        private static string DefaultManagedDirectory()
        {
            try
            {
                string location = typeof(LmmModLoader).Assembly.Location;
                return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
