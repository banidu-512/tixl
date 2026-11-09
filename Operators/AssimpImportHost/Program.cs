using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

// Arguments: <Lib.dll> <dependency folder>... -- <arguments for the import>
var separatorIndex = Array.IndexOf(args, "--");
if (args.Length < 2 || separatorIndex < 1)
{
    Console.Error.Write("Usage: AssimpImportHost <operator package dll> <dependency folders...> -- <import arguments>");
    return 64;
}

var packageAssemblyPath = args[0];
var dependencyFolders = args[1..separatorIndex];
var importArguments = args[(separatorIndex + 1)..];

AssemblyLoadContext.Default.Resolving += (context, name) =>
                                         {
                                             foreach (var folder in dependencyFolders)
                                             {
                                                 var candidate = Path.Combine(folder, name.Name + ".dll");
                                                 if (File.Exists(candidate))
                                                     return context.LoadFromAssemblyPath(candidate);
                                             }

                                             return null;
                                         };

var packageAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(packageAssemblyPath));
var entryType = packageAssembly.GetType("Lib.Utils.AssimpIo.AssimpIsolatedImport", throwOnError: true)!;
var entryMethod = entryType.GetMethod("RunChild", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                  ?? throw new MissingMethodException(entryType.FullName, "RunChild");

return (int)entryMethod.Invoke(null, [importArguments])!;
