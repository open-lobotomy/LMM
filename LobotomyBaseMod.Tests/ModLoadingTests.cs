using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LobotomyBaseModLib;
using Mono.Cecil;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LobotomyBaseMod.Tests
{
    public sealed class ModLoadingTests
    {
        [Theory]
        [InlineData("0Harmony", "1.0.0.0", "0Harmony109", "1.0.9.0")]
        [InlineData("0Harmony", "1.0.9.1", "0Harmony109", "1.0.9.0")]
        [InlineData("0Harmony", "1.1.0.0", "0Harmony109", "1.0.9.0")]
        [InlineData("0Harmony", "1.2.0.1", "0Harmony12", "1.2.0.0")]
        [InlineData("12Harmony", "1.2.0.1", "0Harmony12", "1.2.0.0")]
        public void KnownLegacyReferencesRetainFamily(string name, string version, string expected, string expectedVersion)
        {
            using var assembly = SyntheticAssembly();
            var reference = new AssemblyNameReference(name, Version.Parse(version));
            assembly.MainModule.AssemblyReferences.Add(reference);

            Assert.True(HarmonyReferenceAdapter.Rewrite(assembly));
            Assert.Equal(expected, reference.Name);
            Assert.Equal(Version.Parse(expectedVersion), reference.Version);
        }

        [Fact]
        public void DuplicateConvertedReferencesCollapseAndReparentScopes()
        {
            using var raw = SyntheticAssembly();
            var refA = new AssemblyNameReference("0Harmony", new Version(1, 2, 0, 0));
            var refB = new AssemblyNameReference("12Harmony", new Version(1, 2, 0, 1));
            raw.MainModule.AssemblyReferences.Add(refA);
            raw.MainModule.AssemblyReferences.Add(refB);
            var importedMarker = new TypeReference("Harmony", "MarkerType", raw.MainModule, refB);
            raw.MainModule.Types[0].BaseType = importedMarker;
            raw.MainModule.ExportedTypes.Add(new ExportedType("Harmony", "ExportedMarker", raw.MainModule, refB));

            using var buffer = new MemoryStream();
            raw.Write(buffer);
            buffer.Position = 0;
            using var assembly = AssemblyDefinition.ReadAssembly(buffer);

            var observations = new List<string>();
            Assert.True(HarmonyReferenceAdapter.Rewrite(assembly, observations));
            Assert.Empty(observations);
            var survivor = Assert.Single(assembly.MainModule.AssemblyReferences, r => r.Name == "0Harmony12");
            Assert.Same(survivor, assembly.MainModule.Types[0].BaseType.Scope);
            Assert.Same(survivor, assembly.MainModule.ExportedTypes[0].Scope);
        }

        [Fact]
        public void UnsupportedLegacyInputDoesNotPartiallyRewriteKnownReferences()
        {
            using var assembly = SyntheticAssembly();
            var known = new AssemblyNameReference("0Harmony", new Version(1, 0, 9, 1));
            assembly.MainModule.AssemblyReferences.Add(known);
            assembly.MainModule.AssemblyReferences.Add(new AssemblyNameReference("12Harmony", new Version(1, 1, 0, 0)));
            var notes = new List<string>();

            Assert.False(HarmonyReferenceAdapter.Rewrite(assembly, notes));
            Assert.Single(notes);
            Assert.Equal("0Harmony", known.Name);
            Assert.Throws<NotSupportedException>(() => HarmonyReferenceAdapter.Rewrite(assembly));
        }

        [Fact]
        public void SignedReferenceAndSignedConsumerStayUnchanged()
        {
            using var assembly = SyntheticAssembly();
            var reference = new AssemblyNameReference("0Harmony", new Version(1, 0, 9, 1))
            {
                PublicKeyToken = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }
            };
            assembly.MainModule.AssemblyReferences.Add(reference);
            var notes = new List<string>();
            Assert.False(HarmonyReferenceAdapter.Rewrite(assembly, notes));
            Assert.Single(notes);

            reference.PublicKeyToken = Array.Empty<byte>();
            assembly.Name.PublicKey = new byte[] { 1, 2, 3 };
            notes.Clear();
            Assert.False(HarmonyReferenceAdapter.Rewrite(assembly, notes));
            Assert.Single(notes);
            Assert.Equal("0Harmony", reference.Name);
        }

        [Fact]
        public void RewriteAndRewriteImageValidateArgumentsAndModernPassthrough()
        {
            Assert.Throws<ArgumentNullException>(() => HarmonyReferenceAdapter.RewriteImage(null, out _));
            Assert.Throws<ArgumentNullException>(() => HarmonyReferenceAdapter.Rewrite(null));
            Assert.Throws<ArgumentNullException>(() => HarmonyReferenceAdapter.Rewrite(null, new List<string>()));
            using var nullObsAsm = SyntheticAssembly();
            Assert.Throws<ArgumentNullException>(() => HarmonyReferenceAdapter.Rewrite(nullObsAsm, null));

            using var assembly = SyntheticAssembly();
            assembly.MainModule.AssemblyReferences.Add(new AssemblyNameReference("0Harmony", new Version(2, 4, 3, 0)));
            using var stream = new MemoryStream();
            assembly.Write(stream);
            byte[] image = stream.ToArray();

            Assert.Same(image, HarmonyReferenceAdapter.RewriteImage(image, out var notes));
            Assert.Empty(notes);
        }

        [Fact]
        public void MonoImageLoaderValidatesArgumentsPlatformAndNativeLoadStates()
        {
            Assert.Throws<ArgumentNullException>(() => MonoImageLoader.Load(null, "unused"));
            Assert.Throws<ArgumentException>(() => MonoImageLoader.Load(Array.Empty<byte>(), "unused"));
            Assert.Throws<ArgumentException>(() => MonoImageLoader.Load(new byte[] { 1 }, null));
            Assert.Throws<ArgumentNullException>(() => MonoImageLoader.LoadOrReuseBySimpleName(null, "unused"));
            Assert.Throws<ArgumentException>(() => MonoImageLoader.LoadOrReuseBySimpleName(Array.Empty<byte>(), "unused"));
            Assert.Throws<ArgumentException>(() => MonoImageLoader.LoadOrReuseBySimpleName(new byte[] { 1 }, null));

            MonoImageLoader.ResetForTests();
            try
            {
                Assert.Throws<PlatformNotSupportedException>(() => MonoImageLoader.Load(new byte[] { 1 }, "nonexistent-path"));
            }
            finally
            {
                MonoImageLoader.ResetForTests();
            }
        }

        [Fact]
        public void MonoImageLoaderExercisesMaterializeReuseConflictAndFailureLatch()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "lmm-imgloader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                Assembly sampleAssembly = typeof(ModLoadingTests).Assembly;
                byte[] sampleBytes = File.ReadAllBytes(sampleAssembly.Location);
                Guid sampleMvid = sampleAssembly.ManifestModule.ModuleVersionId;

                string pathA = Path.Combine(tempDir, "CopyA.dll");
                string pathB = Path.Combine(tempDir, "CopyB.dll");
                File.WriteAllBytes(pathA, sampleBytes);
                File.WriteAllBytes(pathB, sampleBytes);

                var fake = new FakeNativeMonoRuntime(sampleMvid, sampleAssembly, Path.GetFullPath(pathA));
                MonoImageLoader.ResetForTests(fake, (name, allowed) => false);
                try
                {
                    // Fake runtime expects sampleAssembly.Location == sampleAssembly.Location; write a copy at a path matching fake test check.
                    string actualLocationPath = sampleAssembly.Location;
                    byte[] loadedCopy = File.ReadAllBytes(actualLocationPath);

                    // 1) Mismatching MVID from Mono throws InvalidOperationException cleanly before assemblyLoadStarted.
                    fake.ReportedMvid = Guid.NewGuid();
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(loadedCopy, actualLocationPath));
                    fake.ReportedMvid = sampleMvid;

                    // 2) Bad open image status throws BadImageFormatException without latching restart.
                    fake.OpenStatus = 1;
                    Assert.Throws<BadImageFormatException>(() => MonoImageLoader.Load(loadedCopy, actualLocationPath));
                    fake.OpenStatus = 0;

                    // 3) Happy path loads via handle fallback, caches by path & simple name.
                    fake.UseHandleFallback = true;
                    Assembly first = MonoImageLoader.Load(loadedCopy, actualLocationPath);
                    Assert.Same(sampleAssembly, first);
                    Assert.True(fake.FreedHandles > 0);
                    Assert.True(fake.ClosedImages > 0);

                    // Cache hit by path
                    Assert.Same(sampleAssembly, MonoImageLoader.Load(loadedCopy, actualLocationPath));
                    Assert.Same(sampleAssembly, MonoImageLoader.LoadOrReuseBySimpleName(loadedCopy, actualLocationPath));

                    // Cache hit by simple name from different path via LoadOrReuseBySimpleName
                    Assert.Same(sampleAssembly, MonoImageLoader.LoadOrReuseBySimpleName(loadedCopy, pathB));

                    // Conflict when calling Load (strict) for same simple name at another path
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(loadedCopy, pathB));

                    // Conflict when rewritten image bytes change for an already-loaded path
                    byte[] modified = (byte[])loadedCopy.Clone();
                    modified[modified.Length - 1] ^= 1;
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(modified, actualLocationPath));
                }
                finally
                {
                    MonoImageLoader.ResetForTests();
                }

                // Exercise requiresRestart latch when assembly load fails after materialization starts
                MonoImageLoader.ResetForTests(fake, (name, allowed) => false);
                try
                {
                    fake.UseHandleFallback = false;
                    fake.AssemblyStatus = 2;
                    Assert.Throws<BadImageFormatException>(() => MonoImageLoader.Load(sampleBytes, sampleAssembly.Location));
                    fake.AssemblyStatus = 0;
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(sampleBytes, sampleAssembly.Location));
                }
                finally
                {
                    MonoImageLoader.ResetForTests();
                }

                // Exercise substituted image, null domain, null reflection object, zero handle, duplicate name check
                MonoImageLoader.ResetForTests(fake, (name, allowed) => false);
                try
                {
                    fake.SubstituteAssemblyImage = true;
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(sampleBytes, sampleAssembly.Location));
                }
                finally { MonoImageLoader.ResetForTests(); }

                MonoImageLoader.ResetForTests(fake, (name, allowed) => false);
                try
                {
                    fake.SubstituteAssemblyImage = false;
                    fake.UseHandleFallback = true;
                    fake.NullDomain = true;
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(sampleBytes, sampleAssembly.Location));
                }
                finally { MonoImageLoader.ResetForTests(); }

                MonoImageLoader.ResetForTests(fake, (name, allowed) => false);
                try
                {
                    fake.NullDomain = false;
                    fake.NullReflectionObject = true;
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(sampleBytes, sampleAssembly.Location));
                }
                finally { MonoImageLoader.ResetForTests(); }

                MonoImageLoader.ResetForTests(fake, (name, allowed) => false);
                try
                {
                    fake.NullReflectionObject = false;
                    fake.ZeroHandle = true;
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(sampleBytes, sampleAssembly.Location));
                }
                finally { MonoImageLoader.ResetForTests(); }

                MonoImageLoader.ResetForTests(fake, (name, allowed) => true);
                try
                {
                    Assert.Throws<InvalidOperationException>(() => MonoImageLoader.Load(sampleBytes, sampleAssembly.Location));
                }
                finally { MonoImageLoader.ResetForTests(); }
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void LmmModLoaderHandlesCatalogLoadingResolutionAndEntryPointClaims()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "lmm-modloader-test-" + Guid.NewGuid().ToString("N"));
            string modA = Path.Combine(tempRoot, "ModA");
            string modB = Path.Combine(tempRoot, "ModB");
            Directory.CreateDirectory(modA);
            Directory.CreateDirectory(modB);
            LmmModLoader.ResetForTests();
            try
            {
                string nativePath = Path.Combine(modA, "NativeHelper.dll");
                File.WriteAllBytes(nativePath, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02 });

                string commonA = Path.Combine(modA, "SharedCommon.dll");
                string commonB = Path.Combine(modB, "SharedCommon.dll");
                WriteSyntheticAssembly(commonA, "SharedCommon", new Version(6, 0, 4, 0), new AssemblyNameReference("0Harmony", new Version(1, 0, 9, 1)));
                WriteSyntheticAssembly(commonB, "SharedCommon", new Version(6, 0, 5, 0), new AssemblyNameReference("0Harmony", new Version(1, 0, 9, 1)));

                string tieA = Path.Combine(modA, "TieLib.dll");
                string tieB = Path.Combine(modB, "TieLib.dll");
                WriteSyntheticAssembly(tieA, "TieLib", new Version(1, 0, 0, 0), new AssemblyNameReference("12Harmony", new Version(1, 2, 0, 1)));
                WriteSyntheticAssembly(tieB, "TieLib", new Version(1, 0, 0, 0), new AssemblyNameReference("12Harmony", new Version(1, 2, 0, 1)));

                string modernPath = Path.Combine(modB, "ModernMod.dll");
                WriteSyntheticAssembly(modernPath, "ModernMod", new Version(1, 0, 0, 0), new AssemblyNameReference("0Harmony", new Version(2, 4, 3, 0)));

                string managedDummy = Path.Combine(tempRoot, "ManagedDep.dll");
                WriteSyntheticAssembly(managedDummy, "ManagedDep", new Version(1, 0, 0, 0), null);

                byte[] commonBBefore = File.ReadAllBytes(commonB);
                byte[] tieABefore = File.ReadAllBytes(tieA);
                var logs = new List<string>();

                LmmModLoader.PrepareCatalogOnly(
                    new[] { null, new DirectoryInfo(Path.Combine(tempRoot, "Missing")), new DirectoryInfo(modA), new DirectoryInfo(modB) },
                    logs.Add,
                    tempRoot);

                Assert.Empty(logs);
                Assert.Null(LmmModLoader.GetMappedSimpleNameForFile(nativePath));
                Assert.Null(LmmModLoader.GetLoadedAssemblyForFile(nativePath));
                Assert.Null(LmmModLoader.GetLoadedAssemblyForFile(null));
                Assert.Null(LmmModLoader.GetCanonicalCandidate(null));
                Assert.Null(LmmModLoader.GetMappedSimpleNameForFile(null));

                var commonCandidate = LmmModLoader.GetCanonicalCandidate("SharedCommon");
                Assert.NotNull(commonCandidate);
                Assert.Equal(Path.GetFullPath(commonB), commonCandidate.FilePath);
                Assert.Equal(new Version(6, 0, 5, 0), commonCandidate.Version);
                Assert.True(commonCandidate.Changed);
                using (var rewrittenCommon = AssemblyDefinition.ReadAssembly(new MemoryStream(commonCandidate.RewrittenBytes, false)))
                {
                    Assert.Contains(rewrittenCommon.MainModule.AssemblyReferences, r => r.Name == "0Harmony109" && r.Version == new Version(1, 0, 9, 0));
                    Assert.DoesNotContain(rewrittenCommon.MainModule.AssemblyReferences, r => r.Name == "0Harmony");
                }
                Assert.Equal(commonBBefore, File.ReadAllBytes(commonB));

                var tieCandidate = LmmModLoader.GetCanonicalCandidate("TieLib");
                Assert.NotNull(tieCandidate);
                Assert.Equal(Path.GetFullPath(tieA), tieCandidate.FilePath);
                Assert.Equal(tieABefore, File.ReadAllBytes(tieA));

                var modernCandidate = LmmModLoader.GetCanonicalCandidate("ModernMod");
                Assert.NotNull(modernCandidate);
                Assert.False(modernCandidate.Changed);

                Assert.False(LmmModLoader.TryClaimEntryPoint(null, "SomeType", "Harmony_Patch"));
                Assert.True(LmmModLoader.TryClaimEntryPoint(typeof(ModLoadingTests).Assembly, "SomeType", "Harmony_Patch"));
                Assert.False(LmmModLoader.TryClaimEntryPoint(typeof(ModLoadingTests).Assembly, "SomeType", "Harmony_Patch"));
                Assert.True(LmmModLoader.TryClaimEntryPoint(typeof(ModLoadingTests).Assembly, "SomeType", "ModInitializer"));

                // ExtenionUtil_Lib reflection helpers (non-Harmony fallback in Class1.cs)
                var box = new SampleReflectTarget();
                Assert.Equal(42, box.Invoke("Compute", new object[] { 20, 22 }));
                Assert.Equal(7, box.GetFieldValue<int>("secret"));
                box.SetFieldValue("secret", 99);
                Assert.Equal(99, box.GetFieldValueGetter<SampleReflectTarget, int>("secret")(box));
                box.SetFieldValueSetter<SampleReflectTarget, int>("secret")(box, 123);
                Assert.Equal(123, box.GetFieldValue<int>("secret"));

                var adaptedPaths = new List<string>();
                var directPaths = new List<string>();
                LmmModLoader.PrepareModDirectories(
                    new[] { new DirectoryInfo(modA), new DirectoryInfo(modB) },
                    logs.Add,
                    (bytes, path) =>
                    {
                        adaptedPaths.Add(path);
                        return typeof(HarmonyReferenceAdapter).Assembly;
                    },
                    path =>
                    {
                        directPaths.Add(path);
                        return typeof(ModLoadingTests).Assembly;
                    },
                    tempRoot);

                Assert.Empty(logs);
                Assert.Equal(2, adaptedPaths.Count);
                Assert.Single(directPaths);
                Assert.Same(typeof(HarmonyReferenceAdapter).Assembly, LmmModLoader.GetLoadedAssemblyForFile(commonA));
                Assert.Same(typeof(ModLoadingTests).Assembly, LmmModLoader.GetLoadedAssemblyForFile(modernPath));

                // Exercise AssemblyResolve for managedDirectory hit, unknown hit, and invalid request name
                Assert.Same(typeof(ModLoadingTests).Assembly, LmmModLoader.ResolveAssemblyByRequestName("ManagedDep, Version=1.0.0.0"));
                Assert.Contains(Path.GetFullPath(managedDummy), directPaths);
                Assert.Null(LmmModLoader.ResolveAssemblyByRequestName(""));
                Assert.Null(LmmModLoader.ResolveAssemblyByRequestName("NonExistentDep"));

                // Exercise error capture when loader throws
                var errorLogs = new List<string>();
                LmmModLoader.PrepareModDirectories(
                    new[] { new DirectoryInfo(modB) },
                    errorLogs.Add,
                    (b, p) => throw new InvalidOperationException("boom"),
                    p => throw new InvalidOperationException("boom"),
                    tempRoot);
                Assert.NotEmpty(errorLogs);
            }
            finally
            {
                LmmModLoader.ResetForTests();
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
            }
        }

        private static void WriteSyntheticAssembly(string path, string name, Version version, AssemblyNameReference reference)
        {
            using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, version), name, ModuleKind.Dll);
            if (reference != null) assembly.MainModule.AssemblyReferences.Add(reference);
            assembly.Write(path);
        }

        private sealed class SampleReflectTarget
        {
            private int secret;
            internal SampleReflectTarget() { secret = 7; }
            private int Compute(int a, int b) => a + b;
        }

        private static AssemblyDefinition SyntheticAssembly() =>
            AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Synthetic", new Version(1, 0)), "Synthetic", ModuleKind.Dll);

        private sealed class FakeNativeMonoRuntime : MonoImageLoader.INativeMonoRuntime
        {
            private readonly Assembly targetAssembly;
            private IntPtr allocatedGuidPtr;

            internal Guid ReportedMvid;
            internal int OpenStatus;
            internal int AssemblyStatus;
            internal bool SubstituteAssemblyImage;
            internal bool UseHandleFallback;
            internal bool NullDomain;
            internal bool NullReflectionObject;
            internal bool ZeroHandle;
            internal int ClosedImages;
            internal int FreedHandles;

            internal FakeNativeMonoRuntime(Guid mvid, Assembly assembly, string expectedPath)
            {
                ReportedMvid = mvid;
                targetAssembly = assembly;
            }

            public bool IsSupportedPlatform() => true;

            public IntPtr OpenImage(IntPtr data, uint length, int copy, out int status, int reflectionOnly, IntPtr name)
            {
                status = OpenStatus;
                return status == 0 ? new IntPtr(101) : IntPtr.Zero;
            }

            public IntPtr LoadAssembly(IntPtr image, IntPtr filename, out int status, int reflectionOnly)
            {
                status = AssemblyStatus;
                return status == 0 ? new IntPtr(202) : IntPtr.Zero;
            }

            public void CloseImage(IntPtr image)
            {
                ClosedImages++;
                if (allocatedGuidPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(allocatedGuidPtr);
                    allocatedGuidPtr = IntPtr.Zero;
                }
            }

            public IntPtr ImageGuid(IntPtr image)
            {
                if (allocatedGuidPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(allocatedGuidPtr);
                }
                allocatedGuidPtr = Marshal.StringToHGlobalAnsi(ReportedMvid.ToString("D").ToUpperInvariant());
                return allocatedGuidPtr;
            }

            public IntPtr AssemblyImage(IntPtr assembly) => SubstituteAssemblyImage ? new IntPtr(999) : new IntPtr(101);
            public IntPtr Domain() => NullDomain ? IntPtr.Zero : new IntPtr(303);
            public IntPtr AssemblyObject(IntPtr domain, IntPtr assembly) => NullReflectionObject ? IntPtr.Zero : new IntPtr(404);
            public uint CreateHandle(IntPtr value, int pinned) => ZeroHandle ? 0u : 505u;
            public void FreeHandle(uint handle) => FreedHandles++;

            public Assembly ResolveRootedAssembly(IntPtr nativeAssembly, uint nativeHandle)
            {
                if (UseHandleFallback && nativeHandle == 0)
                    return null;
                return targetAssembly;
            }
        }
    }
}
