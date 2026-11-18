using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;

namespace LobotomyBaseModLib
{
    public static class HarmonyReferenceAdapter
    {
        public static byte[] RewriteImage(byte[] image, out string[] observations)
        {
            return RewriteImage(image, null, out observations);
        }

        // Reading from a stream detaches the module from its directory, so callers pass an
        // explicit resolver rooted at the mod directory when sibling references are needed.
        public static byte[] RewriteImage(byte[] image, IAssemblyResolver resolver, out string[] observations)
        {
            if (image == null) throw new ArgumentNullException("image");
            var notes = new List<string>();
            using (var input = new MemoryStream(image, false))
            using (var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { AssemblyResolver = resolver }))
            {
                bool changed = Rewrite(assembly, notes);
                observations = notes.ToArray();
                if (!changed) return image;
                using (var output = new MemoryStream())
                {
                    assembly.Write(output, new WriterParameters { Timestamp = 0 });
                    return output.ToArray();
                }
            }
        }

        public static bool Rewrite(AssemblyDefinition assembly)
        {
            var observations = new List<string>();
            bool changed = Rewrite(assembly, observations);
            if (observations.Count != 0) throw new NotSupportedException(string.Join("; ", observations.ToArray()));
            return changed;
        }

        public static bool Rewrite(AssemblyDefinition assembly, ICollection<string> observations)
        {
            if (assembly == null) throw new ArgumentNullException("assembly");
            if (observations == null) throw new ArgumentNullException("observations");
            var changes = new List<KeyValuePair<AssemblyNameReference, string>>();
            bool unsupported = false;
            foreach (var reference in assembly.MainModule.AssemblyReferences)
            {
                bool ordinary = string.Equals(reference.Name, "0Harmony", StringComparison.OrdinalIgnoreCase);
                bool legacy12 = string.Equals(reference.Name, "12Harmony", StringComparison.OrdinalIgnoreCase);
                if ((!ordinary && !legacy12) || (ordinary && reference.Version.Major >= 2)) continue;
                string target = null;
                if (ordinary && reference.Version.Major == 1 && reference.Version.Minor < 2)
                    target = "0Harmony109";
                else if (reference.Version.Major == 1 && reference.Version.Minor == 2)
                    target = "0Harmony12";

                if (target == null || (reference.PublicKeyToken != null && reference.PublicKeyToken.Length != 0))
                {
                    unsupported = true;
                    observations.Add("Legacy Harmony reference requires a separate compatibility decision: " + reference.FullName);
                }
                else
                {
                    changes.Add(new KeyValuePair<AssemblyNameReference, string>(reference, target));
                }
            }

            if (changes.Count != 0 && (assembly.Modules.Count != 1 || assembly.Name.HasPublicKey))
            {
                unsupported = true;
                observations.Add("Signed or multiple-module consumer remains unchanged: " + assembly.Name.FullName);
            }

            if (unsupported) return false;
            foreach (var change in changes)
            {
                change.Key.Name = change.Value;
                change.Key.Version = change.Value == "0Harmony109" ? new Version(1, 0, 9, 0) : new Version(1, 2, 0, 0);
            }

            var survivors = new Dictionary<string, AssemblyNameReference>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<AssemblyNameReference>();
            foreach (var reference in assembly.MainModule.AssemblyReferences)
            {
                if (reference.Name != "0Harmony109" && reference.Name != "0Harmony12") continue;
                AssemblyNameReference survivor;
                if (survivors.TryGetValue(reference.FullName, out survivor))
                {
                    foreach (var type in assembly.MainModule.GetTypeReferences())
                        if (ReferenceEquals(type.Scope, reference)) type.Scope = survivor;
                    foreach (var type in assembly.MainModule.ExportedTypes)
                        if (ReferenceEquals(type.Scope, reference)) type.Scope = survivor;
                    duplicates.Add(reference);
                }
                else
                {
                    survivors.Add(reference.FullName, reference);
                }
            }

            foreach (var duplicate in duplicates) assembly.MainModule.AssemblyReferences.Remove(duplicate);
            return changes.Count != 0 || duplicates.Count != 0;
        }
    }
}
