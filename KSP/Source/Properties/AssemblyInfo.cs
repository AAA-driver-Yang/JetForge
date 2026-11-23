using System.Reflection;
using System.Runtime.InteropServices;

// 版本号的唯一来源是 Core/Version.cs 里的 JetForgeVersion.Number，
// 这里必须与之保持一致（tools/make_release.sh 会检查，不一致就报错退出）。
[assembly: AssemblyTitle("JetForge")]
[assembly: AssemblyDescription("程序化喷气发动机与进气道设计器（Kerbal Space Program）")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyCompany("JetForge")]
[assembly: AssemblyProduct("JetForge")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]