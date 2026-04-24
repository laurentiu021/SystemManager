// SysManager — Windows system monitoring toolkit
// Author: laurentiu021 · https://github.com/laurentiu021/SysManager
// License: MIT
// Author : laurentiu021
// Source : https://github.com/laurentiu021/SysManager
// License: MIT

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;

// Attributes the SDK doesn't generate from the csproj metadata.
// Title / Description / Product / Company / Copyright come from csproj.
[assembly: AssemblyTrademark("SysManager by laurentiu021")]
[assembly: InternalsVisibleTo("SysManager.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
