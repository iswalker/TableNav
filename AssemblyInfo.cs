using System.Reflection;
using System.Runtime.InteropServices;

// These attributes are compiled by csc.exe into the Win32 VERSIONINFO resource
// embedded in the PE header. Task Manager reads FileDescription (AssemblyTitle)
// for the process display name.

[assembly: AssemblyTitle("TableNav")]
[assembly: AssemblyDescription("Fast, focused CSV and XLSX viewer and editor")]
[assembly: AssemblyProduct("TableNav")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]
