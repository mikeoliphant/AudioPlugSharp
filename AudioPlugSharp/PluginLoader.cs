using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace AudioPlugSharp
{
    public static class PluginLoader
    {
        private static Dictionary<string, AssemblyLoadContext> Contexts = new Dictionary<string, AssemblyLoadContext>();

        public static IAudioPlugin LoadPluginFromAssembly(string assemblyName)
        {
            string assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!Contexts.TryGetValue(assemblyPath, out var loadContext))
            {
                loadContext = new PluginLoadContext(Path.Combine(assemblyPath, assemblyName) + ".dll");
            }

            Assembly pluginAssembly = LoadAssembly(assemblyName, loadContext);

            IAudioPlugin plugin = GetObjectByInterface(pluginAssembly, typeof(IAudioPlugin)) as IAudioPlugin;

            if ((plugin != null) && plugin.CacheLoadContext)
            {
                Contexts[assemblyPath] = loadContext;
            }

            return plugin;
        }

        static Assembly LoadAssembly(String assemblyName, AssemblyLoadContext loadContext)
        {
            try
            {
                Logger.Log("Load assembly: " + assemblyName);

                String executingAseemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                //Assembly^ assembly = AssemblyLoadContext::GetLoadContext(Reflection::Assembly::GetExecutingAssembly())->LoadFromAssemblyPath(
                //	Path::Combine(executingAseemblyPath, (gcnew Reflection::AssemblyName(assemblyName))->Name + ".dll"));

                AssemblyName actualAssemblyName = new AssemblyName(assemblyName);

                Logger.Log("Actual assembly name is " + actualAssemblyName);

                Assembly assembly = loadContext.LoadFromAssemblyName(actualAssemblyName);

                //Assembly^ assembly = loadContext->LoadFromAssemblyPath(
                //	Path::Combine(executingAseemblyPath, (gcnew Reflection::AssemblyName(assemblyName))->Name + ".dll"));

                return assembly;
            }
            catch (System.Exception ex)
            {
                Logger.Log("Unable to load assembly: " + assemblyName + " with: " + ex.ToString());
            }

            return null;
        }

        static Object GetObjectByInterface(Assembly assembly, Type interfaceType)
        {
            Logger.Log("Looking for type: " + interfaceType.AssemblyQualifiedName + " in assembly " + assembly.FullName);

            Logger.Log("FullyQualifiedName: " + typeof(IAudioPlugin).Module.FullyQualifiedName);


            Type matchedType = null;

            try
            {
                Logger.Log("Assembly types: " + assembly.GetTypes().Length);


                foreach (Type type in assembly.GetTypes())
                {
                    if (type.IsPublic)
                    {
                        Logger.Log("Checking type: " + type.Name + " -- " + type.Module.FullyQualifiedName);

                        if (interfaceType.IsAssignableFrom(type))
                        {
                            Logger.Log("Matched");

                            matchedType = type;

                            break;
                        }

                        //for each (System::Type^ iType in type->GetInterfaces())
                        //{
                        //	Logger::Log("Checking interface type: " + iType->Name + " -- " + iType->Module->FullyQualifiedName);

                        //	if (iType == interfaceType)
                        //	{
                        //		Logger::Log("Matched");

                        //		matchedType = type;

                        //		break;
                        //	}
                        //}
                    }

                    if (matchedType != null)
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("Type scanning failed with: " + ex.ToString());
            }

            if (matchedType == null)
            {
                Logger.Log("Unable to find type");

                return null;
            }

            Object obj = null;

            try
            {
                obj = System.Activator.CreateInstance(matchedType);
            }
            catch (Exception ex)
            {
                Logger.Log("Failed to create object: " + ex.ToString());
            }

            return obj;
        }
    }


	public class PluginLoadContext : AssemblyLoadContext
    {
        AssemblyDependencyResolver resolver;

        public PluginLoadContext(String pluginPath)
        {
            Logger.Log("Load context from: " + pluginPath);

            resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == "AudioPlugSharp")
            {
                Logger.Log("Skipping AudioPlugSharp assembly load");

                return AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly()).LoadFromAssemblyName(assemblyName);
            }

            String assemblyPath = resolver.ResolveAssemblyToPath(assemblyName);

            Logger.Log("PluginLoadContext load [" + assemblyName + "] from: " + assemblyPath);

            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            return null;
        }      

        protected override IntPtr LoadUnmanagedDll(String unmanagedDllName)
        {
            String libraryPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }
}
