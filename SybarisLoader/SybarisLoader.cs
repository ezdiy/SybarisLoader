using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Configuration;
using Mono.Cecil;
using System.Diagnostics;

namespace SybarisLoader
{
    public static class Loader
    {
        private static Dictionary<string, List<MethodInfo>> patchersDictionary;

        /// <summary>
        ///     Load sybaris compatible patchers from supplied folder
        /// </summary>
        /// <remarks>
        ///     At the moment this loader requires to System.dll (and others) being loaded into memroy to work,
        ///     which is why it cannot be patched with this method.
        /// </remarks>
        public static void LoadPatchers(string dir)
        {
            patchersDictionary = new Dictionary<string, List<MethodInfo>>();

            Trace.TraceInformation("Loading patchers");
            
            foreach (string dll in Directory.GetFiles(dir,"*.Patcher.dll"))
            {
                Assembly assembly;

                try
                {
                    assembly = Assembly.LoadFile(dll);
                }
                catch (Exception e)
                {
                    Trace.TraceError($"Failed to load {dll}: {e.Message}");
                    if (e.InnerException != null)
                        Trace.TraceError($"Inner: {e.InnerException}");
                    continue;
                }

                foreach (Type type in assembly.GetTypes())
                {
                    if (type.IsInterface)
                        continue;

                    FieldInfo targetAssemblyNamesField = type.GetField("TargetAssemblyNames", BindingFlags.Static | BindingFlags.Public);

                    if (targetAssemblyNamesField == null || targetAssemblyNamesField.FieldType != typeof(string[]))
                        continue;

                    MethodInfo patchMethod = type.GetMethod("Patch", BindingFlags.Static | BindingFlags.Public);

                    if (patchMethod == null || patchMethod.ReturnType != typeof(void))
                        continue;

                    ParameterInfo[] parameters = patchMethod.GetParameters();

                    if (parameters.Length != 1 || parameters[0].ParameterType != typeof(AssemblyDefinition))
                        continue;

                    string[] requestedAssemblies = targetAssemblyNamesField.GetValue(null) as string[];

                    if (requestedAssemblies == null || requestedAssemblies.Length == 0)
                        continue;

                    Trace.TraceInformation($"Adding {type.FullName}");

                    foreach (string requestedAssembly in requestedAssemblies)
                    {
                        if (!patchersDictionary.TryGetValue(requestedAssembly, out List<MethodInfo> list))
                        {
                            list = new List<MethodInfo>();
                            patchersDictionary.Add(requestedAssembly, list);
                        }

                        list.Add(patchMethod);
                    }
                }
            }
        }

        /// <summary>
        ///     Carry out patching on the asemblies.
        /// </summary>
        /// <remarks>
        ///     The returned list of assemblies is to be from memory by the caller
        ///     (possibly after further transforms).
        ///     Since .NET loads all assemblies only once,
        ///     any further attempts by Unity to load the patched assemblies
        ///     will do nothing. Thus we achieve the same "dynamic patching" effect as with Sybaris.
        /// </remarks>

        public static List<AssemblyDefinition> Patch(string dir)
        {
            var output = new List<AssemblyDefinition>();
            Trace.TraceInformation("Patching assemblies:");

            foreach (KeyValuePair<string, List<MethodInfo>> patchJob in patchersDictionary)
            {
                string assemblyName = patchJob.Key;
                List<MethodInfo> patchers = patchJob.Value;

                string assemblyPath = Path.Combine(dir, assemblyName);

                if (!File.Exists(assemblyPath))
                {
                    Trace.TraceWarning($"{assemblyPath} does not exist. Skipping...");
                    continue;
                }

                AssemblyDefinition assemblyDefinition;

                try
                {
                    assemblyDefinition = AssemblyDefinition.ReadAssembly(assemblyPath);
                }
                catch (Exception e)
                {
                    Trace.TraceError($"Failed to open {assemblyPath}: {e.Message}");
                    continue;
                }

                foreach (MethodInfo patcher in patchers)
                {
                    Trace.TraceInformation($"Running {patcher.DeclaringType.FullName}");
                    try
                    {
                        patcher.Invoke(null, new object[] {assemblyDefinition});
                    }
                    catch (TargetInvocationException te)
                    {
                        Exception inner = te.InnerException;
                        if (inner != null)
                        {
                            Trace.TraceError($"Error inside the patcher: {inner.Message}");
                            Trace.TraceError($"Stack trace:\n{inner.StackTrace}");
                        }
                    }
                    catch (Exception e)
                    {
                        Trace.TraceError($"By the patcher loader: {e.Message}");
                        Trace.TraceError($"Stack trace:\n{e.StackTrace}");
                    }
                }

                // Save the patched assembly to a file for debugging purposes
                if (Properties.Settings.Default.debug)
                    SavePatchedAssembly(dir, assemblyDefinition, Path.GetFileNameWithoutExtension(assemblyName));

                output.Add(assemblyDefinition);
            }
            return output;
        }

        public static void SavePatchedAssembly(string dir, AssemblyDefinition ass, string name)
        {
            MemoryStream ms = new MemoryStream();
            ass.Write(ms);
            byte[] assemblyBytes = ms.ToArray();

            string path = Path.Combine(dir, $"{name}_patched.dll");
            File.WriteAllBytes(path, assemblyBytes);
            Debug.WriteLine($"Saved patched {name} to {path}");
        }
    }
}