extern alias H109;
extern alias H12;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using HarmonyXInterop;
using Xunit;
using EmitOpCodes = System.Reflection.Emit.OpCodes;

namespace LobotomyBaseMod.Tests
{
    public sealed class HarmonyInteropTests
    {
        public static int TargetMultiply(int x) => x * 2;
        public static int TargetAdd109(int x) => x + 1;
        public static int TargetAdd12(int x) => x + 10;
        public static int PropertyBacking => 7;

        private static void MultiplyPostfix12(ref int __result) => __result += 100;
        private static void AddPostfix109(ref int __result) => __result += 1000;
        private static bool PrefixSkip(ref int __result)
        {
            __result = 42;
            return false;
        }
        private static IEnumerable<H12::Harmony.CodeInstruction> SampleTranspiler(IEnumerable<H12::Harmony.CodeInstruction> instructions) => instructions;
        private static IEnumerable<CodeInstruction> ModernTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase method, ILGenerator il) => instructions;

        [Fact]
        public void Harmony12PatchReturnsNonNullDynamicMethodAndSyncsPatchState()
        {
            var original = typeof(HarmonyInteropTests).GetMethod(nameof(TargetMultiply));
            var postfix = typeof(HarmonyInteropTests).GetMethod(nameof(MultiplyPostfix12), BindingFlags.NonPublic | BindingFlags.Static);
            PatchInfo capturedPatchInfo = null;

            HarmonyInterop.UpdateWrapperOverride = (target, patchInfo) =>
            {
                capturedPatchInfo = patchInfo;
                return target as MethodInfo;
            };
            var harmony = H12::Harmony.HarmonyInstance.Create("tests.harmony12.basic");
            try
            {
                DynamicMethod dm = harmony.Patch(original, null, new H12::Harmony.HarmonyMethod(postfix));
                Assert.NotNull(dm);
                Assert.NotNull(capturedPatchInfo);
                Assert.Single(capturedPatchInfo.postfixes);
                Assert.Equal(42, (int)dm.Invoke(null, new object[] { 21 }));

                var patches = harmony.GetPatchInfo(original);
                Assert.NotNull(patches);
                Assert.Single(patches.Postfixes);
                Assert.True(harmony.HasAnyPatches("tests.harmony12.basic"));
                Assert.Contains(original, harmony.GetPatchedMethods());

                harmony.VersionInfo(out var currentVersion);
                Assert.Equal(new Version(1, 2, 0, 0), currentVersion);
            }
            finally
            {
                harmony.Unpatch(original, H12::Harmony.HarmonyPatchType.All, "tests.harmony12.basic");
                HarmonyInterop.UpdateWrapperOverride = null;
            }

            Assert.Empty(capturedPatchInfo.postfixes);
            Assert.False(harmony.HasAnyPatches("tests.harmony12.basic"));
        }

        [Fact]
        public void Harmony12AttributeAndManualProcessorsUnpatchByMethodAndPatchFunctionsWork()
        {
            PatchInfo capturedPatchInfo = null;
            HarmonyInterop.UpdateWrapperOverride = (target, patchInfo) =>
            {
                capturedPatchInfo = patchInfo;
                return target as MethodInfo;
            };
            var harmony = H12::Harmony.HarmonyInstance.Create("tests.harmony12.processor");
            try
            {
                var attrInfo = H12::Harmony.HarmonyMethod.Merge(
                    H12::Harmony.HarmonyMethodExtensions.GetHarmonyMethods(typeof(SampleAttrPatchClass)));
                var processor = new H12::Harmony.PatchProcessor(harmony, typeof(SampleAttrPatchClass), attrInfo);
                var dms = processor.Patch();
                Assert.Single(dms);
                Assert.Contains(typeof(HarmonyInteropTests).GetMethod(nameof(TargetMultiply)), H12::Harmony.PatchProcessor.AllPatchedMethods());

                var transpilerMethod = typeof(HarmonyInteropTests).GetMethod(nameof(SampleTranspiler), BindingFlags.NonPublic | BindingFlags.Static);
                var patchInfo = new H12::Harmony.PatchInfo();
                H12::Harmony.PatchFunctions.AddTranspiler(patchInfo, "id1", new H12::Harmony.HarmonyMethod(transpilerMethod));
                Assert.Single(patchInfo.transpilers);
                H12::Harmony.PatchFunctions.RemoveTranspiler(patchInfo, "id1");
                Assert.Empty(patchInfo.transpilers);

                var sorted = H12::Harmony.PatchFunctions.GetSortedPatchMethods(
                    typeof(HarmonyInteropTests).GetMethod(nameof(TargetMultiply)),
                    new[] { new H12::Harmony.Patch(transpilerMethod, 0, "id1", 400, null, null) });
                Assert.Single(sorted);

                processor.Unpatch(typeof(SampleAttrPatchClass).GetMethod(nameof(SampleAttrPatchClass.Postfix)));
                Assert.Empty(capturedPatchInfo.postfixes);
            }
            finally
            {
                HarmonyInterop.UpdateWrapperOverride = null;
            }
        }

        [H12::Harmony.HarmonyPatch(typeof(HarmonyInteropTests), nameof(TargetMultiply), new[] { typeof(int) })]
        private static class SampleAttrPatchClass
        {
            public static void Postfix(ref int __result) => __result += 1;
        }

        [Fact]
        public void TranspilerInteropWrapperConvertsInstructionsAndInvokesTranspiler()
        {
            var transpilerMethod = typeof(HarmonyInteropTests).GetMethod(nameof(ModernTranspiler), BindingFlags.NonPublic | BindingFlags.Static);
            MethodInfo wrapped = TranspilerInterop.WrapInterop(transpilerMethod);
            Assert.NotNull(wrapped);
            Assert.Same(wrapped, TranspilerInterop.WrapInterop(transpilerMethod));

            var dmd = new MonoMod.Utils.DynamicMethodDefinition("DummyGen", typeof(void), Type.EmptyTypes);
            var il = dmd.GetILGenerator();
            var sampleInstructions = new[] { new CodeInstruction(EmitOpCodes.Nop), new CodeInstruction(EmitOpCodes.Br_S, il.DefineLabel()) };
            var result = wrapped.Invoke(null, new object[]
            {
                sampleInstructions,
                il,
                typeof(HarmonyInteropTests).GetMethod(nameof(TargetMultiply))
            }) as IEnumerable<CodeInstruction>;
            Assert.NotNull(result);
            Assert.Equal(2, new List<CodeInstruction>(result).Count);
        }

        [Fact]
        public void Harmony12ToReturnDynamicMethodHandlesMethodInfoAndValidation()
        {
            var existing = new DynamicMethod("dm_passthrough", typeof(int), Type.EmptyTypes);
            var il = existing.GetILGenerator();
            il.Emit(EmitOpCodes.Ldc_I4_1);
            il.Emit(EmitOpCodes.Ret);

            Assert.Same(existing, H12::Harmony.PatchHandler.ToReturnDynamicMethod(existing));
            Assert.Null(H12::Harmony.PatchHandler.ToReturnDynamicMethod(null));

            MethodInfo closedStatic = typeof(HarmonyInteropTests).GetMethod(nameof(TargetMultiply));
            DynamicMethod wrapped = H12::Harmony.PatchHandler.ToReturnDynamicMethod(closedStatic);
            Assert.NotNull(wrapped);
            Assert.Equal(14, (int)wrapped.Invoke(null, new object[] { 7 }));

            Assert.Throws<NotSupportedException>(() =>
                H12::Harmony.PatchHandler.ToReturnDynamicMethod(typeof(object).GetMethod(nameof(ToString))));
        }

        [Fact]
        public void Harmony109PatchAndAttributeHelpersForwardIntoHarmonyX()
        {
            var original = typeof(HarmonyInteropTests).GetMethod(nameof(TargetAdd109));
            var postfix = typeof(HarmonyInteropTests).GetMethod(nameof(AddPostfix109), BindingFlags.NonPublic | BindingFlags.Static);
            PatchInfo capturedPatchInfo = null;

            HarmonyInterop.UpdateWrapperOverride = (target, patchInfo) =>
            {
                capturedPatchInfo = patchInfo;
                return target as MethodInfo;
            };
            try
            {
                var harmony = H109::Harmony.HarmonyInstance.Create("tests.harmony109.basic");
                harmony.Patch(original, null, new H109::Harmony.HarmonyMethod(postfix));

                Assert.NotNull(capturedPatchInfo);
                Assert.Single(capturedPatchInfo.postfixes);

                var info = harmony.IsPatched(original);
                Assert.NotNull(info);
                Assert.Single(info.Postfixes);
                Assert.Contains("tests.harmony109.basic", info.Owners);
                Assert.Contains(original, harmony.GetPatchedMethods());

                harmony.VersionInfo(out var version109);
                Assert.Equal(new Version(1, 0, 9, 0), version109);

                var propAttr = new H109::Harmony.HarmonyPatch(nameof(PropertyBacking), H109::Harmony.PropertyMethod.Getter);
                Assert.Equal("get_" + nameof(PropertyBacking), propAttr.info.methodName);

                var cache = new H109::Harmony.AccessCache();
                var m1 = cache.GetMethodInfo(typeof(HarmonyInteropTests), nameof(TargetAdd109), new[] { typeof(int) });
                var m2 = cache.GetMethodInfo(typeof(HarmonyInteropTests), nameof(TargetAdd109), new[] { typeof(int) });
                Assert.Same(m1, m2);

                var prefix109 = typeof(HarmonyInteropTests).GetMethod(nameof(PrefixSkip), BindingFlags.NonPublic | BindingFlags.Static);
                var pInfo109 = new H109::Harmony.PatchInfo();
                H109::Harmony.PatchFunctions.AddPrefix(pInfo109, "id109", new H109::Harmony.HarmonyMethod(prefix109));
                Assert.Single(pInfo109.prefixes);
                var sorted109 = H109::Harmony.PatchFunctions.GetSortedPatchMethods(original, pInfo109.prefixes);
                Assert.Single(sorted109);
            }
            finally
            {
                HarmonyInterop.UpdateWrapperOverride = null;
            }
        }

        [Fact]
        public void HarmonyXInteropApplyPatchWithResultReturnsWrapperAndSyncsTranspilers()
        {
            var original = typeof(HarmonyInteropTests).GetMethod(nameof(TargetAdd12));
            var prefixMethod = typeof(HarmonyInteropTests).GetMethod(nameof(PrefixSkip), BindingFlags.NonPublic | BindingFlags.Static);
            var transpilerMethod = typeof(HarmonyInteropTests).GetMethod(nameof(SampleTranspiler), BindingFlags.NonPublic | BindingFlags.Static);
            PatchInfo capturedPatchInfo = null;

            HarmonyInterop.UpdateWrapperOverride = (target, patchInfo) =>
            {
                capturedPatchInfo = patchInfo;
                return target as MethodInfo;
            };
            try
            {
                var add = new PatchInfoWrapper
                {
                    prefixes = new[] { new PatchMethod { method = prefixMethod, owner = "tests.interop.owner", priority = 400 } },
                    postfixes = Array.Empty<PatchMethod>(),
                    transpilers = new[] { new PatchMethod { method = transpilerMethod, owner = "tests.interop.owner", priority = 400 } },
                    finalizers = Array.Empty<PatchMethod>()
                };
                var empty = new PatchInfoWrapper
                {
                    prefixes = Array.Empty<PatchMethod>(),
                    postfixes = Array.Empty<PatchMethod>(),
                    transpilers = Array.Empty<PatchMethod>(),
                    finalizers = Array.Empty<PatchMethod>()
                };

                MethodInfo wrapper = HarmonyInterop.ApplyPatchWithResult(original, add, empty);
                Assert.Same(original, wrapper);
                Assert.Single(capturedPatchInfo.prefixes);
                Assert.Single(capturedPatchInfo.transpilers);

                HarmonyInterop.ApplyPatch(original, empty, add);
                Assert.Empty(capturedPatchInfo.prefixes);
                Assert.Empty(capturedPatchInfo.transpilers);
            }
            finally
            {
                HarmonyInterop.UpdateWrapperOverride = null;
            }
        }
    }
}
