using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyXInterop;

namespace Harmony
{
	internal class PatchHandler
	{
		private MethodBase mb;
		private PatchInfoWrapper previousState = new PatchInfoWrapper
		{
			prefixes = new PatchMethod[0],
			postfixes = new PatchMethod[0],
			transpilers = new PatchMethod[0],
			finalizers = new PatchMethod[0]
		};

		public DynamicMethod Apply()
		{
			PatchMethod[] ToPatchMethod(Patch[] patches)
			{
				return patches.Select(p => new PatchMethod
				{
					after = p.after,
					before = p.before,
					method = p.GetMethod(mb),
					priority = p.priority,
					owner = p.owner,
				}).ToArray();
			}

			var info = HarmonySharedState.GetPatchInfo(mb);
			var state = new PatchInfoWrapper
			{
				prefixes = ToPatchMethod(info.prefixes),
				postfixes = ToPatchMethod(info.postfixes),
				transpilers = ToPatchMethod(info.transpilers),
				finalizers = new PatchMethod[0]
			};

			var add = new PatchInfoWrapper { finalizers = new PatchMethod[0] };
			var remove = new PatchInfoWrapper { finalizers = new PatchMethod[0] };

			Diff(previousState.prefixes, state.prefixes, out add.prefixes, out remove.prefixes);
			Diff(previousState.postfixes, state.postfixes, out add.postfixes, out remove.postfixes);
			Diff(previousState.transpilers, state.transpilers, out add.transpilers, out remove.transpilers);

			previousState = state;

			var replacement = HarmonyInterop.ApplyPatchWithResult(mb, add, remove);
			return ToReturnDynamicMethod(replacement);
		}

		// Wrap non-DynamicMethod backend replacements so Harmony 1.2 callers receive a DynamicMethod handle.
		internal static DynamicMethod ToReturnDynamicMethod(MethodInfo replacement)
		{
			if (replacement == null)
				return null;

			if (replacement is DynamicMethod dynamicMethod)
				return dynamicMethod;

			if (!replacement.IsStatic || replacement.ContainsGenericParameters || (replacement.CallingConvention & CallingConventions.VarArgs) != 0)
				throw new NotSupportedException("The backend replacement must be a closed static method with a fixed signature.");

			var parameters = replacement.GetParameters();
			var types = new Type[parameters.Length];
			for (var index = 0; index < parameters.Length; index++)
				types[index] = parameters[index].ParameterType;

			var snapshot = new DynamicMethod(replacement.Name, replacement.ReturnType, types, typeof(PatchHandler).Module, true);
			var il = snapshot.GetILGenerator();
			for (var index = 0; index < parameters.Length; index++)
				il.Emit(OpCodes.Ldarg, (short)index);
			il.Emit(OpCodes.Call, replacement);
			il.Emit(OpCodes.Ret);
			return snapshot;
		}

		static void Diff(PatchMethod[] last, PatchMethod[] curr, out PatchMethod[] add, out PatchMethod[] remove)
		{
			add = curr.Except(last, PatchMethodComparer.Instance).ToArray();
			remove = last.Except(curr, PatchMethodComparer.Instance).ToArray();
		}

		static readonly Dictionary<MethodBase, PatchHandler> patchHandlers = new Dictionary<MethodBase, PatchHandler>();

		internal static PatchHandler Get(MethodBase method)
		{
			lock (patchHandlers)
			{
				if (!patchHandlers.TryGetValue(method, out var handler))
					patchHandlers[method] = handler = new PatchHandler { mb = method };
				return handler;
			}
		}
	}

	public static class HarmonySharedState
	{
		static readonly Dictionary<MethodBase, PatchInfo> patchInfos = new Dictionary<MethodBase, PatchInfo>();

		internal static PatchInfo GetPatchInfo(MethodBase method)
		{
			lock (patchInfos)
			{
				if (!patchInfos.TryGetValue(method, out var info))
					patchInfos[method] = info = new PatchInfo();
				return info;
			}
		}

		internal static IEnumerable<MethodBase> GetPatchedMethods()
		{
			lock (patchInfos)
			{
				return patchInfos.Keys.ToList().AsEnumerable();
			}
		}
	}
}
