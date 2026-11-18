using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace LobotomyBaseModLib
{
    /// <summary>Load controlled rewritten images through the existing Windows Mono runtime.</summary>
    public static class MonoImageLoader
    {
        internal interface INativeMonoRuntime
        {
            bool IsSupportedPlatform();
            IntPtr OpenImage(IntPtr data, uint length, int copy, out int status, int reflectionOnly, IntPtr name);
            IntPtr LoadAssembly(IntPtr image, IntPtr filename, out int status, int reflectionOnly);
            void CloseImage(IntPtr image);
            IntPtr ImageGuid(IntPtr image);
            IntPtr AssemblyImage(IntPtr assembly);
            IntPtr Domain();
            IntPtr AssemblyObject(IntPtr domain, IntPtr assembly);
            uint CreateHandle(IntPtr value, int pinned);
            void FreeHandle(uint handle);
            Assembly ResolveRootedAssembly(IntPtr nativeAssembly, uint nativeHandle);
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Entry> ByPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Entry> ByName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> LoadingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static INativeMonoRuntime activeRuntime;
        private static Func<string, Assembly, bool> nameInUseCheck = DefaultIsNameInUseElsewhere;
        private static bool requiresRestart;

        internal static void ResetForTests(INativeMonoRuntime testRuntime = null, Func<string, Assembly, bool> testNameCheck = null)
        {
            lock (Sync)
            {
                ByPath.Clear();
                ByName.Clear();
                LoadingPaths.Clear();
                LoadingNames.Clear();
                activeRuntime = testRuntime;
                nameInUseCheck = testNameCheck ?? DefaultIsNameInUseElsewhere;
                requiresRestart = false;
            }
        }

        /// <summary>Load an image or reuse an already-loaded assembly sharing the same simple name.</summary>
        public static Assembly LoadOrReuseBySimpleName(byte[] image, string originalPath)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (image.Length == 0) throw new ArgumentException("The rewritten image is empty.", "image");
            if (string.IsNullOrEmpty(originalPath)) throw new ArgumentException("An original file path is required.", "originalPath");

            Metadata metadata = Inspect(image);
            string path = Path.GetFullPath(originalPath);
            lock (Sync)
            {
                Entry known;
                if (ByPath.TryGetValue(path, out known))
                {
                    return known.Assembly;
                }
                if (ByName.TryGetValue(metadata.SimpleName, out known))
                {
                    return known.Assembly;
                }
                foreach (Assembly existing in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(existing.GetName().Name, metadata.SimpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return existing;
                    }
                }
            }
            return Load(image, originalPath);
        }

        /// <summary>Load an image while retaining its original file location and source hash.</summary>
        public static Assembly Load(byte[] image, string originalPath)
        {
            if (image == null) throw new ArgumentNullException("image");
            if (image.Length == 0) throw new ArgumentException("The rewritten image is empty.", "image");
            if (string.IsNullOrEmpty(originalPath)) throw new ArgumentException("An original file path is required.", "originalPath");

            INativeMonoRuntime runtime = GetOrCreateRuntime();
            if (!runtime.IsSupportedPlatform())
                throw new PlatformNotSupportedException("Controlled image loading requires the game's existing Windows CLR 2 Mono runtime.");

            string path = Path.GetFullPath(originalPath);
            byte[] captured = (byte[])image.Clone();
            string imageHash = Hash(captured);
            Metadata metadata = Inspect(captured);
            lock (Sync)
            {
                if (requiresRestart)
                    throw new InvalidOperationException("A prior native load failed after materialization began. Restart the process before loading another image.");
                string sourceHash = HashFile(path);
                Entry known;
                if (ByPath.TryGetValue(path, out known))
                {
                    if (known.SourceHash != sourceHash || known.ImageHash != imageHash || known.Identity != metadata.Identity || known.Mvid != metadata.Mvid)
                        throw new InvalidOperationException("The source or rewritten image changed after this path was loaded. A fresh process is required: " + path);
                    VerifyAssembly(known.Assembly, metadata, path);
                    VerifyNoOtherAssembly(metadata.SimpleName, known.Assembly);
                    return known.Assembly;
                }
                if (ByName.TryGetValue(metadata.SimpleName, out known))
                    throw new InvalidOperationException("An assembly with this name already belongs to another original path; its location cannot represent both files: " + metadata.SimpleName);
                if (LoadingPaths.Contains(path) || LoadingNames.Contains(metadata.SimpleName))
                    throw new InvalidOperationException("A recursive request reached an image whose native load has not completed: " + metadata.SimpleName);
                VerifyNoOtherAssembly(metadata.SimpleName, null);
                LoadingPaths.Add(path);
                LoadingNames.Add(metadata.SimpleName);
                try
                {
                    Assembly loaded = Materialize(runtime, captured, metadata, path, sourceHash);
                    var entry = new Entry { Assembly = loaded, Identity = metadata.Identity, Mvid = metadata.Mvid, SourceHash = sourceHash, ImageHash = imageHash };
                    ByPath.Add(path, entry);
                    ByName.Add(metadata.SimpleName, entry);
                    return loaded;
                }
                finally
                {
                    LoadingPaths.Remove(path);
                    LoadingNames.Remove(metadata.SimpleName);
                }
            }
        }

        private static INativeMonoRuntime GetOrCreateRuntime()
        {
            lock (Sync)
            {
                if (activeRuntime == null)
                    activeRuntime = new PInvokeMonoRuntime();
                return activeRuntime;
            }
        }

        private static Assembly Materialize(INativeMonoRuntime runtime, byte[] image, Metadata metadata, string path, string sourceHash)
        {
            IntPtr openedImage = IntPtr.Zero;
            GCHandle buffer = default(GCHandle);
            uint nativeHandle = 0;
            bool assemblyLoadStarted = false;
            using (var filename = new Utf8(path))
            {
                try
                {
                    buffer = GCHandle.Alloc(image, GCHandleType.Pinned);
                    int status = 0;
                    openedImage = runtime.OpenImage(buffer.AddrOfPinnedObject(), checked((uint)image.Length), 1, out status, 0, filename.Pointer);
                    if (openedImage == IntPtr.Zero || status != 0)
                        throw new BadImageFormatException("Mono rejected the rewritten image; image status=" + status + ".");
                    IntPtr guid = runtime.ImageGuid(openedImage);
                    if (guid == IntPtr.Zero || new Guid(Marshal.PtrToStringAnsi(guid)) != metadata.Mvid)
                        throw new InvalidOperationException("Mono returned image metadata that differs from the captured rewritten input.");

                    assemblyLoadStarted = true;
                    IntPtr assembly = runtime.LoadAssembly(openedImage, filename.Pointer, out status, 0);
                    if (assembly == IntPtr.Zero || status != 0)
                        throw new BadImageFormatException("Mono rejected the rewritten assembly; assembly status=" + status + ".");
                    if (runtime.AssemblyImage(assembly) != openedImage)
                        throw new InvalidOperationException("Mono substituted another native image while loading the rewritten assembly.");

                    Assembly result = runtime.ResolveRootedAssembly(assembly, 0);
                    if (result == null)
                    {
                        IntPtr domain = runtime.Domain();
                        if (domain == IntPtr.Zero) throw new InvalidOperationException("Mono did not expose the current managed domain.");
                        IntPtr reflectionObject = runtime.AssemblyObject(domain, assembly);
                        if (reflectionObject == IntPtr.Zero) throw new InvalidOperationException("Mono did not expose the loaded reflection assembly.");
                        nativeHandle = runtime.CreateHandle(reflectionObject, 0);
                        if (nativeHandle == 0) throw new InvalidOperationException("Mono could not root the loaded reflection assembly.");
                        result = runtime.ResolveRootedAssembly(assembly, nativeHandle);
                    }
                    if (result == null) throw new InvalidOperationException("The rooted Mono object is not a managed Assembly.");
                    VerifyAssembly(result, metadata, path);
                    VerifyNoOtherAssembly(metadata.SimpleName, result);
                    if (HashFile(path) != sourceHash)
                        throw new InvalidOperationException("The original file changed while its rewritten image was loading: " + path);
                    return result;
                }
                catch
                {
                    if (assemblyLoadStarted) requiresRestart = true;
                    throw;
                }
                finally
                {
                    if (nativeHandle != 0) runtime.FreeHandle(nativeHandle);
                    if (openedImage != IntPtr.Zero) runtime.CloseImage(openedImage);
                    if (buffer.IsAllocated) buffer.Free();
                }
            }
        }

        private static readonly FieldInfo NativeAssemblyField =
            typeof(Assembly).GetField("_mono_assembly", BindingFlags.NonPublic | BindingFlags.Instance);

        private static Assembly FindManagedAssemblyByNativePointer(IntPtr nativeAssembly)
        {
            if (NativeAssemblyField == null || NativeAssemblyField.FieldType != typeof(IntPtr))
                return null;
            foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (candidate != null && candidate.GetType().Assembly == typeof(Assembly).Assembly
                    && (IntPtr)NativeAssemblyField.GetValue(candidate) == nativeAssembly)
                {
                    return candidate;
                }
            }
            return null;
        }

        private static Metadata Inspect(byte[] image)
        {
            using (var stream = new MemoryStream(image, false))
            using (var assembly = AssemblyDefinition.ReadAssembly(stream))
            {
                if (assembly.Modules.Count != 1) throw new NotSupportedException("Controlled loading currently supports one-module assemblies only.");
                return new Metadata { Identity = assembly.Name.FullName, SimpleName = assembly.Name.Name, Mvid = assembly.MainModule.Mvid };
            }
        }

        private static void VerifyAssembly(Assembly assembly, Metadata expected, string path)
        {
            if (assembly.ReflectionOnly || assembly.FullName != expected.Identity || assembly.ManifestModule.ModuleVersionId != expected.Mvid)
                throw new InvalidOperationException("The loaded assembly does not match the captured rewritten identity and module metadata.");
            if (string.IsNullOrEmpty(assembly.Location) || !SamePath(assembly.Location, path))
                throw new InvalidOperationException("Mono did not preserve the original Assembly.Location.");
            Uri codeBase;
            if (!Uri.TryCreate(assembly.CodeBase, UriKind.Absolute, out codeBase) || !codeBase.IsFile || !SamePath(codeBase.LocalPath, path))
                throw new InvalidOperationException("Mono did not preserve the original Assembly.CodeBase.");
        }

        private static void VerifyNoOtherAssembly(string name, Assembly allowed)
        {
            if (nameInUseCheck(name, allowed))
                throw new InvalidOperationException("An untracked or duplicate managed assembly already owns this name: " + name);
        }

        private static bool DefaultIsNameInUseElsewhere(string name, Assembly allowed)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ReferenceEquals(assembly, allowed))
                    return true;
            }
            return false;
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string Hash(byte[] image)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(image)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class Metadata
        {
            internal string Identity;
            internal string SimpleName;
            internal Guid Mvid;
        }

        private sealed class Entry
        {
            internal Assembly Assembly;
            internal string Identity;
            internal Guid Mvid;
            internal string SourceHash;
            internal string ImageHash;
        }

        private sealed class Utf8 : IDisposable
        {
            internal readonly IntPtr Pointer;
            internal Utf8(string value)
            {
                byte[] data = Encoding.UTF8.GetBytes(value + "\0");
                Pointer = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, Pointer, data.Length);
            }
            public void Dispose() { Marshal.FreeHGlobal(Pointer); }
        }

        private sealed class PInvokeMonoRuntime : INativeMonoRuntime
        {
            private NativeApi api;

            public bool IsSupportedPlatform()
            {
                return Environment.OSVersion.Platform == PlatformID.Win32NT
                    && Type.GetType("Mono.Runtime") != null
                    && Environment.Version.Major == 2;
            }

            private NativeApi EnsureApi()
            {
                if (api == null)
                    api = new NativeApi();
                return api;
            }

            public IntPtr OpenImage(IntPtr data, uint length, int copy, out int status, int reflectionOnly, IntPtr name)
                => EnsureApi().OpenImage(data, length, copy, out status, reflectionOnly, name);

            public IntPtr LoadAssembly(IntPtr image, IntPtr filename, out int status, int reflectionOnly)
                => EnsureApi().LoadAssembly(image, filename, out status, reflectionOnly);

            public void CloseImage(IntPtr image) => EnsureApi().CloseImage(image);
            public IntPtr ImageGuid(IntPtr image) => EnsureApi().ImageGuid(image);
            public IntPtr AssemblyImage(IntPtr assembly) => EnsureApi().AssemblyImage(assembly);
            public IntPtr Domain() => EnsureApi().Domain();
            public IntPtr AssemblyObject(IntPtr domain, IntPtr assembly) => EnsureApi().AssemblyObject(domain, assembly);
            public uint CreateHandle(IntPtr value, int pinned) => EnsureApi().CreateHandle(value, pinned);
            public void FreeHandle(uint handle) => EnsureApi().FreeHandle(handle);

            public Assembly ResolveRootedAssembly(IntPtr nativeAssembly, uint nativeHandle)
            {
                if (nativeHandle == 0)
                    return FindManagedAssemblyByNativePointer(nativeAssembly);
                // CLR 2 Mono's GCHandle stores the same 32-bit handle used by its embedding API.
                return GCHandle.FromIntPtr(new IntPtr(unchecked((int)nativeHandle))).Target as Assembly;
            }
        }

        private sealed class NativeApi
        {
            internal readonly ImageOpen OpenImage;
            internal readonly AssemblyLoad LoadAssembly;
            internal readonly PointerAction CloseImage;
            internal readonly PointerFunction ImageGuid;
            internal readonly PointerFunction AssemblyImage;
            internal readonly DomainGet Domain;
            internal readonly AssemblyGetObject AssemblyObject;
            internal readonly HandleCreate CreateHandle;
            internal readonly HandleFree FreeHandle;

            internal NativeApi()
            {
                IntPtr module = GetModuleHandle("mono.dll");
                if (module == IntPtr.Zero) throw new PlatformNotSupportedException("The existing mono.dll module is unavailable; the loader will not load another runtime.");
                OpenImage = (ImageOpen)Bind(module, "mono_image_open_from_data_with_name", typeof(ImageOpen));
                LoadAssembly = (AssemblyLoad)Bind(module, "mono_assembly_load_from_full", typeof(AssemblyLoad));
                CloseImage = (PointerAction)Bind(module, "mono_image_close", typeof(PointerAction));
                ImageGuid = (PointerFunction)Bind(module, "mono_image_get_guid", typeof(PointerFunction));
                AssemblyImage = (PointerFunction)Bind(module, "mono_assembly_get_image", typeof(PointerFunction));
                Domain = (DomainGet)Bind(module, "mono_domain_get", typeof(DomainGet));
                AssemblyObject = (AssemblyGetObject)Bind(module, "mono_assembly_get_object", typeof(AssemblyGetObject));
                CreateHandle = (HandleCreate)Bind(module, "mono_gchandle_new", typeof(HandleCreate));
                FreeHandle = (HandleFree)Bind(module, "mono_gchandle_free", typeof(HandleFree));
            }

            private static Delegate Bind(IntPtr module, string name, Type type)
            {
                IntPtr address = GetProcAddress(module, name);
                if (address == IntPtr.Zero) throw new PlatformNotSupportedException("The current Mono runtime lacks required export " + name + ".");
                return Marshal.GetDelegateForFunctionPointer(address, type);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ImageOpen(IntPtr data, uint length, int copy, out int status, int reflectionOnly, IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AssemblyLoad(IntPtr image, IntPtr filename, out int status, int reflectionOnly);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PointerAction(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PointerFunction(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr DomainGet();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AssemblyGetObject(IntPtr domain, IntPtr assembly);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint HandleCreate(IntPtr value, int pinned);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void HandleFree(uint handle);
    }
}
