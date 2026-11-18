using System;
using System.IO;
using System.Linq;
using LobotomyPatcher;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LobotomyBaseModPatcher.Tests
{
    public sealed class PatcherAndAuditTests
    {
        [Fact]
        public void Net40AuditRemovesDeadMscorlib40AndAcceptsCalVerMonoMod()
        {
            string tempDir = CreateTempDir();
            try
            {
                string dllPath = Path.Combine(tempDir, "Target.dll");
                var asm = CreateBasicV2Assembly("Target", out _);
                asm.MainModule.AssemblyReferences.Add(new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0)));
                asm.MainModule.AssemblyReferences.Add(new AssemblyNameReference("MonoMod.Utils", new Version(22, 7, 31, 1)));
                asm.MainModule.AssemblyReferences.Add(new AssemblyNameReference("MonoMod.RuntimeDetour", new Version(22, 7, 31, 1)));
                asm.Write(dllPath);

                Net40ReferenceAudit.Result audit = Net40ReferenceAudit.Normalize(dllPath);
                Assert.Equal(1, audit.Removed);
                Assert.Equal(0, audit.Rejected);
                Assert.Equal(1, audit.RemainingMscorlibCount);
                Assert.Equal(new[] { "2.0.0.0" }, audit.RemainingMscorlibVersions);

                var normalized = AssemblyDefinition.ReadAssembly(dllPath);
                Assert.Single(normalized.MainModule.AssemblyReferences, r => r.Name == "mscorlib" && r.Version == new Version(2, 0, 0, 0));
                Assert.Contains(normalized.MainModule.AssemblyReferences, r => r.Name == "MonoMod.Utils");
                Assert.Contains(normalized.MainModule.AssemblyReferences, r => r.Name == "MonoMod.RuntimeDetour");
            }
            finally { DeleteDir(tempDir); }
        }

        [Fact]
        public void Net40AuditInspectsRichMetadataGraphsAndRejectsLiveOrBrokenMscorlib()
        {
            string tempDir = CreateTempDir();
            try
            {
                string livePath = Path.Combine(tempDir, "LiveRichNet40.dll");
                var liveAsm = CreateBasicV2Assembly("LiveRichNet40", out var mscorlib20);
                var mscorlib40 = new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0));
                var bclTokenRef = new AssemblyNameReference("CustomBclAsm", new Version(4, 0, 0, 0))
                {
                    PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 }
                };
                liveAsm.MainModule.AssemblyReferences.Add(mscorlib40);
                liveAsm.MainModule.AssemblyReferences.Add(bclTokenRef);

                var voidType = new TypeReference("System", "Void", liveAsm.MainModule, mscorlib20);
                var objType = new TypeReference("System", "Object", liveAsm.MainModule, mscorlib20);
                var typeType = new TypeReference("System", "Type", liveAsm.MainModule, mscorlib20);
                var net4TupleDef = new TypeReference("System", "Tuple`1", liveAsm.MainModule, mscorlib40);
                var genTuple = new GenericInstanceType(net4TupleDef);
                genTuple.GenericArguments.Add(objType);

                var net4AttrType = new TypeReference("System", "CustomNet4Attr", liveAsm.MainModule, mscorlib40);
                var attrCtor = new MethodReference(".ctor", voidType, net4AttrType);
                attrCtor.Parameters.Add(new ParameterDefinition(typeType));
                var customAttr = new CustomAttribute(attrCtor);
                customAttr.ConstructorArguments.Add(new CustomAttributeArgument(typeType, genTuple));

                var holder = new TypeDefinition("Test", "Holder", TypeAttributes.Public | TypeAttributes.Class, objType);
                holder.CustomAttributes.Add(customAttr);
                holder.Interfaces.Add(new TypeReference("Test", "INet4Iface", liveAsm.MainModule, bclTokenRef));
                holder.Fields.Add(new FieldDefinition("ArrField", FieldAttributes.Public, new ArrayType(genTuple)));
                holder.Properties.Add(new PropertyDefinition("PtrProp", PropertyAttributes.None, new PointerType(objType)));
                holder.Events.Add(new EventDefinition("Evt", EventAttributes.None, new PinnedType(objType)));

                var gp = new GenericParameter("T", holder);
                gp.Constraints.Add(genTuple);
                holder.GenericParameters.Add(gp);

                var method = new MethodDefinition("Run", MethodAttributes.Public | MethodAttributes.Static, voidType);
                method.Parameters.Add(new ParameterDefinition("p", ParameterAttributes.None, new ByReferenceType(genTuple)));
                method.Overrides.Add(new MethodReference("BaseMethod", voidType, objType));
                method.Body.Variables.Add(new VariableDefinition(genTuple));
                var il = method.Body.GetILProcessor();
                var targetInstr = il.Create(OpCodes.Ret);
                var genericCall = new GenericInstanceMethod(new MethodReference("GenericHelper", voidType, net4TupleDef));
                genericCall.GenericArguments.Add(genTuple);
                il.Append(il.Create(OpCodes.Ldtoken, net4TupleDef));
                il.Append(il.Create(OpCodes.Pop));
                il.Append(il.Create(OpCodes.Call, genericCall));
                il.Append(targetInstr);
                method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    CatchType = net4AttrType,
                    TryStart = method.Body.Instructions[0],
                    TryEnd = targetInstr,
                    HandlerStart = targetInstr,
                    HandlerEnd = targetInstr
                });
                holder.Methods.Add(method);
                holder.NestedTypes.Add(new TypeDefinition("Test", "Nested", TypeAttributes.NestedPublic | TypeAttributes.Class, objType));
                liveAsm.MainModule.Types.Add(holder);
                liveAsm.MainModule.ExportedTypes.Add(new ExportedType("Test", "Exported", liveAsm.MainModule, mscorlib40));
                liveAsm.Write(livePath);

                byte[] beforeBytes = File.ReadAllBytes(livePath);
                var liveResult = Net40ReferenceAudit.Normalize(livePath);
                Assert.True(liveResult.Rejected >= 2);
                Assert.Equal(beforeBytes, File.ReadAllBytes(livePath));

                // Duplicate mscorlib 2.0 + 3.5 -> rejected loudly
                string dupPath = Path.Combine(tempDir, "DupMscorlib.dll");
                var dupAsm = CreateBasicV2Assembly("DupMscorlib", out _);
                dupAsm.MainModule.AssemblyReferences.Add(new AssemblyNameReference("mscorlib", new Version(3, 5, 0, 0)));
                dupAsm.Write(dupPath);
                var dupResult = Net40ReferenceAudit.Normalize(dupPath);
                Assert.Equal(1, dupResult.Rejected);
                Assert.Equal(2, dupResult.RemainingMscorlibCount);

                // Wrong mscorlib 3.5 -> rejected loudly
                string wrongVerPath = Path.Combine(tempDir, "WrongVerMscorlib.dll");
                var wrongAsm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("WrongVer", new Version(1, 0, 0, 0)), "WrongVer", ModuleKind.Dll);
                wrongAsm.MainModule.AssemblyReferences.Clear();
                wrongAsm.MainModule.AssemblyReferences.Add(new AssemblyNameReference("mscorlib", new Version(3, 5, 0, 0)));
                wrongAsm.Write(wrongVerPath);
                var wrongResult = Net40ReferenceAudit.Normalize(wrongVerPath);
                Assert.Equal(1, wrongResult.Rejected);

                // Missing mscorlib -> rejected loudly
                string noMscorlibPath = Path.Combine(tempDir, "NoMscorlib.dll");
                var noMscorlibAsm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("NoMscorlib", new Version(1, 0, 0, 0)), "NoMscorlib", ModuleKind.Dll);
                noMscorlibAsm.MainModule.AssemblyReferences.Clear();
                noMscorlibAsm.Write(noMscorlibPath);
                var noResult = Net40ReferenceAudit.Normalize(noMscorlibPath);
                Assert.Equal(1, noResult.Rejected);
                Assert.Equal(0, noResult.RemainingMscorlibCount);
            }
            finally { DeleteDir(tempDir); }
        }

        [Fact]
        public void CopperCopiesRequiredOptionalAndShimFilesWithExtensionlessResolution()
        {
            string tempDir = CreateTempDir();
            try
            {
                string managed = Path.Combine(tempDir, "Managed");
                string patchFiles = Path.Combine(tempDir, "PatchFiles");
                string logDir = Path.Combine(tempDir, "Logs");
                Directory.CreateDirectory(managed);
                Directory.CreateDirectory(patchFiles);
                Directory.CreateDirectory(logDir);
                Directory.CreateDirectory(Path.Combine(patchFiles, "BaseMod", "Nested"));

                string[] requiredNoExt = { "Assembly-CSharp", "0Harmony", "LobotomyBaseModLib", "NAudio", "Facepunch.Steamworks.Win64", "steam_api64" };
                foreach (var name in requiredNoExt)
                    File.WriteAllText(Path.Combine(patchFiles, name), "payload-" + name);
                File.WriteAllText(Path.Combine(patchFiles, "Newtonsoft.Json.dll"), "optional-json");
                string[] shimsNoExt = { "0Harmony109", "0Harmony12", "HarmonyXInterop", "Mono.Cecil", "MonoMod.Utils", "MonoMod.RuntimeDetour" };
                foreach (var name in shimsNoExt)
                    File.WriteAllText(Path.Combine(patchFiles, name), "shim-" + name);
                File.WriteAllText(Path.Combine(patchFiles, "BaseMod", "Nested", "info.txt"), "ok");
                File.WriteAllText(Path.Combine(managed, "12Harmony.dll"), "legacy");

                LobotomyPatcher.Debug.logpath = logDir;
                LobotomyPatcher.Debug.Logstr = string.Empty;
                PatchProgram.GameManagedFolderPath = managed;
                PatchProgram.PatchFileFolderPath = patchFiles;

                _ = new Copper(StartOption.Paste);

                Assert.True(File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")));
                Assert.True(File.Exists(Path.Combine(managed, "Newtonsoft.Json.dll")));
                Assert.True(File.Exists(Path.Combine(managed, "0Harmony109.dll")));
                Assert.True(File.Exists(Path.Combine(managed, "0Harmony12.dll")));
                Assert.True(File.Exists(Path.Combine(managed, "HarmonyXInterop.dll")));
                Assert.True(File.Exists(Path.Combine(managed, "BaseMod", "Nested", "info.txt")));
                Assert.False(File.Exists(Path.Combine(managed, "12Harmony.dll")));
                Assert.Contains("패치 방식 Paste 확인", File.ReadAllText(Path.Combine(logDir, "Log.txt")));

                Assert.Throws<FileNotFoundException>(() => Copper.ResolveRequiredSourceFile(patchFiles, "Missing.dll"));
            }
            finally
            {
                LobotomyPatcher.Debug.logpath = null;
                LobotomyPatcher.Debug.Logstr = string.Empty;
                DeleteDir(tempDir);
            }
        }

        [Fact]
        public void ProgramAndPatchProgramHandleEndToEndPatchingAndErrorCases()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string assetsPatcher = Path.Combine(repoRoot, "LMM", "Assets", "Patcher");

            string tempDir = CreateTempDir();
            string prevCwd = Environment.CurrentDirectory;
            try
            {
                Assert.Equal(1, LobotomyPatcher.Program.Run(Array.Empty<string>()));
                Assert.Equal(1, LobotomyPatcher.Program.Run(new[] { tempDir, tempDir, tempDir, "InvalidOption" }));
                Assert.False(PatchProgram.Patch(tempDir, Path.Combine(tempDir, "MissingPatchFiles"), "Patch"));

                // Hermetic end-to-end Patcher + PatchProgram + Copper + Net40ReferenceAudit test via Patchwork.Engine
                string managed = Path.Combine(tempDir, "Managed");
                string patchFiles = Path.Combine(tempDir, "PatchFiles");
                string logDir = Path.Combine(tempDir, "Logs");
                Directory.CreateDirectory(managed);
                Directory.CreateDirectory(patchFiles);
                Directory.CreateDirectory(logDir);
                Directory.CreateDirectory(Path.Combine(patchFiles, "BaseMod"));

                // Stage synthetic Assembly-CSharp (with extra dead mscorlib 4.0.0.0 to exercise Net40ReferenceAudit.Normalize in Patcher)
                var gameAsm = CreateBasicV2Assembly("Assembly-CSharp", out var mscorlib20);
                var objType = new TypeReference("System", "Object", gameAsm.MainModule, mscorlib20);
                var sampleType = new TypeDefinition("Game", "Sample", TypeAttributes.Public | TypeAttributes.Class, objType);
                gameAsm.MainModule.Types.Add(sampleType);
                gameAsm.Write(Path.Combine(patchFiles, "Assembly-CSharp"));

                // Stage minimal synthetic Lobotomypatch with PatchAssembly attribute (plus dead mscorlib 4.0 to exercise Net40ReferenceAudit.Normalize)
                var patchAsm = CreateBasicV2Assembly("Lobotomypatch", out _);
                var patchAttrCtor = patchAsm.MainModule.Import(typeof(Patchwork.Attributes.PatchAssemblyAttribute).GetConstructor(Type.EmptyTypes));
                patchAsm.CustomAttributes.Add(new CustomAttribute(patchAttrCtor));
                patchAsm.MainModule.AssemblyReferences.Add(new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0)));
                patchAsm.Write(Path.Combine(patchFiles, "Lobotomypatch"));

                string[] otherRequired = { "0Harmony", "LobotomyBaseModLib", "NAudio", "Facepunch.Steamworks.Win64", "steam_api64" };
                foreach (var name in otherRequired)
                    File.WriteAllText(Path.Combine(patchFiles, name), "stub-" + name);

                // Provide mscorlib 2.0 in managed & PatcherDependencies for Mono.Cecil 0.9 resolution on .NET 10
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string net35Corlib = Path.Combine(userProfile, ".nuget", "packages", "microsoft.netframework.referenceassemblies.net35", "1.0.3", "build", ".NETFramework", "v3.5", "mscorlib.dll");
                if (File.Exists(net35Corlib))
                    File.Copy(net35Corlib, Path.Combine(managed, "mscorlib.dll"), true);
                File.Copy(Path.Combine(assetsPatcher, "Patchwork.Attributes.dll"), Path.Combine(managed, "Patchwork.Attributes.dll"), true);

                Environment.CurrentDirectory = assetsPatcher;
                int exitCode = LobotomyPatcher.Program.Run(new[] { managed, patchFiles, logDir, "Patch" });
                string logText = File.Exists(Path.Combine(logDir, "Log.txt")) ? File.ReadAllText(Path.Combine(logDir, "Log.txt")) : "<no Log.txt>";
                Assert.True(exitCode == 0, "Expected exitCode 0, got " + exitCode + Environment.NewLine + logText);
                Assert.Contains("패치 성공!", logText);

                var patchedAsm = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "Assembly-CSharp.dll"));
                var mscorlibRefs = patchedAsm.MainModule.AssemblyReferences.Where(r => r.Name == "mscorlib").ToList();
                var mscorlibRef = Assert.Single(mscorlibRefs);
                Assert.Equal(new Version(2, 0, 0, 0), mscorlibRef.Version);
            }
            finally
            {
                Environment.CurrentDirectory = prevCwd;
                LobotomyPatcher.Debug.logpath = null;
                LobotomyPatcher.Debug.Logstr = string.Empty;
                DeleteDir(tempDir);
            }
        }

        private static AssemblyDefinition CreateBasicV2Assembly(string name, out AssemblyNameReference mscorlib20)
        {
            var asm = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
            asm.MainModule.AssemblyReferences.Clear();
            mscorlib20 = new AssemblyNameReference("mscorlib", new Version(2, 0, 0, 0))
            {
                PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 }
            };
            asm.MainModule.AssemblyReferences.Add(mscorlib20);
            return asm;
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "lmm-patcher-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteDir(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
