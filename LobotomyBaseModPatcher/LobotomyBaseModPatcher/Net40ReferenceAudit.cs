using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LobotomyPatcher
{
    // Post-patch reference audit: Patchwork.Engine imports references from its own net472
    // runtime corlib, which can leave a dead mscorlib 4.0.0.0 reference in the written assembly.
    // Unused .NET 4+ framework references are stripped and the remaining assembly must hold
    // strictly one mscorlib reference at version 2.0.0.0 (fail loudly otherwise).
    public static class Net40ReferenceAudit
    {
        private static readonly HashSet<string> FrameworkAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mscorlib",
            "System",
            "WindowsBase",
            "PresentationCore",
            "PresentationFramework",
            "netstandard",
        };

        private static readonly HashSet<string> FrameworkTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "b77a5c561934e089",
            "b03f5f7f11d50a3a",
            "31bf3856ad364e35",
            "cc7b13ffcd2ddd51",
        };

        public sealed class Result
        {
            public int Removed;
            public int Rejected;
            public int RemainingMscorlibCount;
            public readonly List<string> RemainingMscorlibVersions = new List<string>();
            public readonly List<string> RejectedDetails = new List<string>();
        }

        public static Result Normalize(string path)
        {
            Result result = new Result();
            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            string targetDir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(targetDir))
            {
                resolver.AddSearchDirectory(targetDir);
            }
            AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
                path, new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
            ModuleDefinition module = assembly.MainModule;
            List<AssemblyNameReference> net40References = new List<AssemblyNameReference>();
            foreach (AssemblyNameReference reference in module.AssemblyReferences)
            {
                if (IsNet40FrameworkReference(reference))
                {
                    net40References.Add(reference);
                }
            }

            Dictionary<AssemblyNameReference, List<string>> usages =
                new Dictionary<AssemblyNameReference, List<string>>();
            foreach (AssemblyNameReference reference in net40References)
            {
                usages[reference] = new List<string>();
            }

            if (net40References.Count > 0)
            {
                CollectAllUsages(assembly, module, net40References, usages);
            }

            List<AssemblyNameReference> dead = new List<AssemblyNameReference>();
            foreach (AssemblyNameReference reference in net40References)
            {
                if (usages[reference].Count == 0)
                {
                    dead.Add(reference);
                    continue;
                }
                result.Rejected++;
                result.RejectedDetails.Add(
                    reference.FullName + ":" + Environment.NewLine + "  "
                    + string.Join(Environment.NewLine + "  ", usages[reference]));
            }

            if (result.Rejected == 0)
            {
                foreach (AssemblyNameReference reference in dead)
                {
                    module.AssemblyReferences.Remove(reference);
                    result.Removed++;
                }
            }

            ValidateSingleMscorlib20(module, result);

            if (result.Rejected == 0 && result.Removed > 0)
            {
                Rewrite(path, assembly);
            }

            return result;
        }

        private static bool IsNet40FrameworkReference(AssemblyNameReference reference)
        {
            if (reference == null || reference.Version == null || reference.Version.Major < 4)
            {
                return false;
            }
            if (FrameworkAssemblyNames.Contains(reference.Name)
                || reference.Name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || reference.Name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (reference.PublicKeyToken != null && reference.PublicKeyToken.Length == 8)
            {
                string token = BitConverter.ToString(reference.PublicKeyToken).Replace("-", "");
                return FrameworkTokens.Contains(token);
            }
            return false;
        }

        private static void ValidateSingleMscorlib20(ModuleDefinition module, Result result)
        {
            List<AssemblyNameReference> mscorlibRefs = new List<AssemblyNameReference>();
            foreach (AssemblyNameReference reference in module.AssemblyReferences)
            {
                if (string.Equals(reference.Name, "mscorlib", StringComparison.OrdinalIgnoreCase))
                {
                    mscorlibRefs.Add(reference);
                    result.RemainingMscorlibVersions.Add(reference.Version != null ? reference.Version.ToString() : "null");
                }
            }

            result.RemainingMscorlibCount = mscorlibRefs.Count;
            if (mscorlibRefs.Count == 0)
            {
                result.Rejected++;
                result.RejectedDetails.Add("Missing mscorlib reference; expected single mscorlib 2.0.0.0.");
                return;
            }
            if (mscorlibRefs.Count > 1)
            {
                result.Rejected++;
                result.RejectedDetails.Add("Multiple mscorlib references remain: " + string.Join(", ", result.RemainingMscorlibVersions));
                return;
            }
            if (mscorlibRefs[0].Version == null || mscorlibRefs[0].Version.Major != 2 || mscorlibRefs[0].Version.Minor != 0)
            {
                result.Rejected++;
                result.RejectedDetails.Add("Unexpected mscorlib version " + mscorlibRefs[0].Version + "; expected 2.0.0.0.");
            }
        }

        private static void CollectAllUsages(
            AssemblyDefinition assembly,
            ModuleDefinition module,
            List<AssemblyNameReference> targets,
            Dictionary<AssemblyNameReference, List<string>> usages)
        {
            foreach (TypeReference type in module.GetTypeReferences())
            {
                RecordTypeUsage(type, "type " + type.FullName, targets, usages);
            }

            foreach (MemberReference member in module.GetMemberReferences())
            {
                if (member.DeclaringType != null)
                {
                    RecordTypeUsage(member.DeclaringType, "member " + member.DeclaringType.FullName + "::" + member.Name, targets, usages);
                }
                if (member is FieldReference fieldRef)
                {
                    RecordTypeUsage(fieldRef.FieldType, "fieldRef " + member.FullName, targets, usages);
                }
                else if (member is MethodReference methodRef)
                {
                    RecordMethodRefUsage(methodRef, "methodRef " + member.FullName, targets, usages);
                }
            }

            foreach (ExportedType exported in module.ExportedTypes)
            {
                AssemblyNameReference owner = FindReference(targets, exported.Scope);
                if (owner != null)
                {
                    usages[owner].Add("exported type " + exported.FullName);
                }
            }

            RecordCustomAttributes(assembly.CustomAttributes, "assembly attribute", targets, usages);
            RecordCustomAttributes(module.CustomAttributes, "module attribute", targets, usages);

            foreach (TypeDefinition typeDef in module.Types)
            {
                CollectTypeDefUsages(typeDef, targets, usages);
            }
        }

        private static void CollectTypeDefUsages(
            TypeDefinition typeDef,
            List<AssemblyNameReference> targets,
            Dictionary<AssemblyNameReference, List<string>> usages)
        {
            RecordTypeUsage(typeDef.BaseType, "base " + typeDef.FullName, targets, usages);
            RecordCustomAttributes(typeDef.CustomAttributes, "type attribute on " + typeDef.FullName, targets, usages);
            foreach (TypeReference iface in typeDef.Interfaces)
            {
                RecordTypeUsage(iface, "interface on " + typeDef.FullName, targets, usages);
            }
            foreach (GenericParameter gp in typeDef.GenericParameters)
            {
                foreach (TypeReference constraint in gp.Constraints)
                    RecordTypeUsage(constraint, "constraint on " + typeDef.FullName, targets, usages);
            }
            foreach (FieldDefinition field in typeDef.Fields)
            {
                RecordTypeUsage(field.FieldType, "field " + field.FullName, targets, usages);
                RecordCustomAttributes(field.CustomAttributes, "field attribute on " + field.FullName, targets, usages);
            }
            foreach (PropertyDefinition prop in typeDef.Properties)
            {
                RecordTypeUsage(prop.PropertyType, "property " + prop.FullName, targets, usages);
                RecordCustomAttributes(prop.CustomAttributes, "property attribute on " + prop.FullName, targets, usages);
            }
            foreach (EventDefinition evt in typeDef.Events)
            {
                RecordTypeUsage(evt.EventType, "event " + evt.FullName, targets, usages);
                RecordCustomAttributes(evt.CustomAttributes, "event attribute on " + evt.FullName, targets, usages);
            }
            foreach (MethodDefinition method in typeDef.Methods)
            {
                string ctx = "method " + method.FullName;
                RecordTypeUsage(method.ReturnType, ctx + " return", targets, usages);
                RecordCustomAttributes(method.CustomAttributes, ctx + " attribute", targets, usages);
                RecordCustomAttributes(method.MethodReturnType.CustomAttributes, ctx + " return attribute", targets, usages);
                foreach (ParameterDefinition param in method.Parameters)
                {
                    RecordTypeUsage(param.ParameterType, ctx + " param " + param.Name, targets, usages);
                    RecordCustomAttributes(param.CustomAttributes, ctx + " param attribute", targets, usages);
                }
                foreach (GenericParameter gp in method.GenericParameters)
                {
                    foreach (TypeReference constraint in gp.Constraints)
                        RecordTypeUsage(constraint, ctx + " constraint", targets, usages);
                }
                foreach (MethodReference over in method.Overrides)
                {
                    RecordMethodRefUsage(over, ctx + " override", targets, usages);
                }
                if (method.HasBody)
                {
                    foreach (VariableDefinition local in method.Body.Variables)
                    {
                        RecordTypeUsage(local.VariableType, ctx + " local", targets, usages);
                    }
                    foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
                    {
                        RecordTypeUsage(handler.CatchType, ctx + " catch", targets, usages);
                    }
                    foreach (Instruction instr in method.Body.Instructions)
                    {
                        if (instr.Operand is TypeReference typeOp)
                            RecordTypeUsage(typeOp, ctx + " il type", targets, usages);
                        else if (instr.Operand is FieldReference fieldOp)
                        {
                            RecordTypeUsage(fieldOp.DeclaringType, ctx + " il field owner", targets, usages);
                            RecordTypeUsage(fieldOp.FieldType, ctx + " il field type", targets, usages);
                        }
                        else if (instr.Operand is MethodReference methodOp)
                            RecordMethodRefUsage(methodOp, ctx + " il call", targets, usages);
                        else if (instr.Operand is CallSite callSite)
                        {
                            RecordTypeUsage(callSite.ReturnType, ctx + " callsite return", targets, usages);
                            foreach (ParameterDefinition p in callSite.Parameters)
                                RecordTypeUsage(p.ParameterType, ctx + " callsite param", targets, usages);
                        }
                    }
                }
            }
            foreach (TypeDefinition nested in typeDef.NestedTypes)
            {
                CollectTypeDefUsages(nested, targets, usages);
            }
        }

        private static void RecordCustomAttributes(
            IEnumerable<CustomAttribute> attributes,
            string context,
            List<AssemblyNameReference> targets,
            Dictionary<AssemblyNameReference, List<string>> usages)
        {
            foreach (CustomAttribute attr in attributes)
            {
                RecordTypeUsage(attr.AttributeType, context + " " + attr.AttributeType.FullName, targets, usages);
                if (attr.Constructor != null)
                {
                    RecordMethodRefUsage(attr.Constructor, context + " ctor", targets, usages);
                }
                foreach (CustomAttributeArgument arg in attr.ConstructorArguments)
                {
                    RecordAttributeArg(arg, context, targets, usages);
                }
                foreach (CustomAttributeNamedArgument arg in attr.Fields)
                {
                    RecordAttributeArg(arg.Argument, context, targets, usages);
                }
                foreach (CustomAttributeNamedArgument arg in attr.Properties)
                {
                    RecordAttributeArg(arg.Argument, context, targets, usages);
                }
            }
        }

        private static void RecordAttributeArg(
            CustomAttributeArgument arg,
            string context,
            List<AssemblyNameReference> targets,
            Dictionary<AssemblyNameReference, List<string>> usages)
        {
            RecordTypeUsage(arg.Type, context + " arg type", targets, usages);
            if (arg.Value is TypeReference typeVal)
            {
                RecordTypeUsage(typeVal, context + " typeof arg", targets, usages);
            }
            else if (arg.Value is CustomAttributeArgument[] arrayVals)
            {
                foreach (CustomAttributeArgument elem in arrayVals)
                {
                    RecordAttributeArg(elem, context, targets, usages);
                }
            }
        }

        private static void RecordMethodRefUsage(
            MethodReference methodRef,
            string context,
            List<AssemblyNameReference> targets,
            Dictionary<AssemblyNameReference, List<string>> usages)
        {
            if (methodRef == null) return;
            RecordTypeUsage(methodRef.DeclaringType, context, targets, usages);
            RecordTypeUsage(methodRef.ReturnType, context, targets, usages);
            foreach (ParameterDefinition p in methodRef.Parameters)
            {
                RecordTypeUsage(p.ParameterType, context, targets, usages);
            }
            if (methodRef is GenericInstanceMethod genericMethod)
            {
                foreach (TypeReference arg in genericMethod.GenericArguments)
                {
                    RecordTypeUsage(arg, context + " generic arg", targets, usages);
                }
            }
        }

        private static void RecordTypeUsage(
            TypeReference type,
            string context,
            List<AssemblyNameReference> targets,
            Dictionary<AssemblyNameReference, List<string>> usages)
        {
            if (type == null) return;
            AssemblyNameReference owner = FindReference(targets, RootScope(type));
            if (owner != null)
            {
                usages[owner].Add(context);
            }
            if (type is GenericInstanceType generic)
            {
                foreach (TypeReference arg in generic.GenericArguments)
                {
                    RecordTypeUsage(arg, context + " generic arg", targets, usages);
                }
            }
        }

        private static void Rewrite(string path, AssemblyDefinition assembly)
        {
            string tempPath = path + ".normalized.tmp";
            assembly.Write(tempPath);
            ZeroPeTimestamp(tempPath);
            File.Copy(tempPath, path, true);
            File.Delete(tempPath);
        }

        // Pin PE header timestamp to zero so normalized output is deterministic across runs.
        private static void ZeroPeTimestamp(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                byte[] dosHeader = new byte[64];
                if (stream.Read(dosHeader, 0, dosHeader.Length) != dosHeader.Length
                    || dosHeader[0] != (byte)'M'
                    || dosHeader[1] != (byte)'Z')
                {
                    return;
                }
                int peOffset = BitConverter.ToInt32(dosHeader, 0x3C);
                if (peOffset <= 0 || peOffset + 12 > stream.Length)
                {
                    return;
                }
                stream.Position = peOffset + 8;
                stream.Write(new byte[4], 0, 4);
            }
        }

        private static AssemblyNameReference FindReference(
            List<AssemblyNameReference> references, IMetadataScope scope)
        {
            AssemblyNameReference scopeReference = scope as AssemblyNameReference;
            if (scopeReference == null)
            {
                return null;
            }
            foreach (AssemblyNameReference reference in references)
            {
                if (ReferenceEquals(reference, scopeReference)
                    || string.Equals(reference.FullName, scopeReference.FullName, StringComparison.Ordinal))
                {
                    return reference;
                }
            }
            return null;
        }

        private static AssemblyNameReference RootScope(TypeReference type)
        {
            TypeReference unwrapped = UnwrapElementType(type);
            IMetadataScope scope = unwrapped.Scope;
            int guard = 0;
            while (scope is TypeReference parent && guard++ < 64)
            {
                scope = parent.Scope;
            }
            return scope as AssemblyNameReference;
        }

        private static TypeReference UnwrapElementType(TypeReference type)
        {
            while (true)
            {
                if (type is TypeSpecification spec)
                {
                    type = spec.ElementType;
                    continue;
                }
                return type;
            }
        }
    }
}
