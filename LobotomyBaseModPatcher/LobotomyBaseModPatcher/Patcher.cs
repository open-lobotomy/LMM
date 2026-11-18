using System;
using System.IO;
using Patchwork;

namespace LobotomyPatcher
{
    public class Patcher
    {
        public Patcher()
        {
            Debug.Log("스크립트 파일 패치 중...");
            string managedTarget = Path.Combine(PatchProgram.GameManagedFolderPath, "Assembly-CSharp.dll");
            string lobotomyPatch = Copper.ResolveRequiredSourceFile(PatchProgram.PatchFileFolderPath, "Lobotomypatch.dll");
            AssemblyPatcher patcher = new AssemblyPatcher(managedTarget, null);
            patcher.PatchAssembly(lobotomyPatch, null, true);
            patcher.WriteTo(managedTarget);

            Net40ReferenceAudit.Result audit = Net40ReferenceAudit.Normalize(managedTarget);
            Debug.Log(
                "net40+ reference audit: removed " + audit.Removed + " dead reference(s), "
                + audit.Rejected + " reference(s) with usages rejected");
            if (audit.Rejected > 0)
            {
                throw new InvalidOperationException(
                    "net40+ reference audit rejected references with usages:"
                    + Environment.NewLine + string.Join(Environment.NewLine, audit.RejectedDetails));
            }
            Debug.Log("패치 성공!");
        }
    }
}
