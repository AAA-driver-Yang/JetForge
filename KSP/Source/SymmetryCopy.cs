using System.Collections.Generic;
using JetForge.Core;
using UnityEngine;

namespace JetForge.KSP
{
/// <summary>
/// 把"刚改完的这一件"复制到它的对称件上。
///
/// 为什么要专门做这件事：KSP 的对偶放置是**真的两件零件**，各自有自己的模块字段。
/// 我们的编辑器只认窗口打开时抓到的那一件，于是对称的一对里改一台、另一台不动。
/// 这一对看起来就像"改了一个，另一个被重置了"——其实另一台还停在它自己的旧值上，
/// 而机库里两件并排摆着，看着就像被改回去。
///
/// 只在"应用"这种明确动作里调，别挂在每帧拖动上：对称件的网格重建一次几十毫秒，
/// 拖动时跟着重建会把手感拖没（分层刷新在别处管这件事，见 Core/Arch/RefreshPlan.cs）。
/// </summary>
public static class SymmetryCopy
{
    /// <summary>同船 + 对称对偶 + 同一款零件，才算"该跟着变"的那一件。</summary>
    private static List<Part> Twins(Part source)
    {
        List<Part> list = new List<Part>();
        if (source == null) return list;
        if (source.symmetryCounterparts != null)
        {
            for (int i = 0; i < source.symmetryCounterparts.Count; i++)
            {
                Part p = source.symmetryCounterparts[i];
                if (p == null || p == source) continue;
                if (p.partInfo == null || source.partInfo == null) continue;
                if (p.partInfo.name != source.partInfo.name) continue;   // 不同款别硬套
                list.Add(p);
            }
        }
        return list;
    }

    /// <summary>进气道：把型线、长度、出口口径、技术代一起抄过去。返回抄了几件。</summary>
    public static int Intake(ModuleJetForgeIntake src)
    {
        if (src == null || src.part == null) return 0;
        List<Part> twins = Twins(src.part);
        int n = 0;
        for (int i = 0; i < twins.Count; i++)
        {
            ModuleJetForgeIntake m = twins[i].FindModuleImplementing<ModuleJetForgeIntake>();
            if (m == null || m == src) continue;
            m.Spec = src.Spec != null ? src.Spec.Clone() : new IntakeSpec();
            m.exitDiameter = src.exitDiameter;
            m.techName = src.techName;
            m.length = src.length;
            m.RefreshMesh();
            n++;
        }
        return n;
    }

    // 发动机的"对称件跟着改"已经并进 ModuleJetForgeEngine.ApplyDesign →
    // PropagateDesign 那条路（按"改动前的设计指纹"找人，比只看 symmetryCounterparts 更宽：
    // 手动摆的第二台也能收到）。两份机制各管一半的话，迟早出现"改了一个地方、另一个地方没跟上"，
    // 所以这里只留进气道这一份。
}
}
