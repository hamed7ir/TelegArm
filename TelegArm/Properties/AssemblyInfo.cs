using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("TelegArm")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("TelegArm")]
[assembly: AssemblyCopyright("Copyright ©  2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("0be14ff9-f66b-4b3d-b34b-47b8de8f836b")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
// THE source of truth for the version: Program.Version reads Major.Minor.Build from AssemblyVersion,
// TelegramService reports that to Telegram as app_version, and installer/anycpu/build.ps1 names the packages
// (and stamps Setup.exe) from the built exe — there is no second copy to keep in sync any more.
// 2.0.0 = the redesign line (MaterialSkin removed, per-monitor DPI). Device TEST builds keep the numeric
// version at 2.0.0 (app_version must stay numeric) and say which test build they are in the informational
// version, "2.0.0-alpha.N", which the package names and Copy diagnostics show. Drop the suffix to release.
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
[assembly: AssemblyInformationalVersion("2.0.0-alpha.1")]
