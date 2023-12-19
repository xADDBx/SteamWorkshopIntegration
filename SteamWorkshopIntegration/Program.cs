using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Configuration.Assemblies;
using System.IO;
using Kingmaker.Modding;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Injector {
    // This class patches the original game assembly to test under somewhat realistic conditions
    class Program {
        const string GamePath = "D:/Games/Steam/steamapps/common/Warhammer 40,000 Rogue Trader";
        public static readonly string assembliesPath = Path.Combine(GamePath, "WH40KRT_Data", "Managed");
        static void Main() {
            var resolver = new CustomAssemblyResolver(assembliesPath);
            var parameters = new ReaderParameters { AssemblyResolver = resolver };
            var origCodePath = Path.Combine(assembliesPath, "Code.dll");
            var originalCodeAssembly = new FileInfo(origCodePath);
            try {
                originalCodeAssembly.MoveTo(origCodePath + ".orig");
            } catch (Exception e) {
                originalCodeAssembly.Delete();
            }
            // Add SteamWorkshopIntegration.Instance.Start to GameStarter.InitProcess just before PFLog.Mods.Log("Starting OwlcatUnityModManager");
            var assemblyDefinition = AssemblyDefinition.ReadAssembly(origCodePath + ".orig", parameters);
            var gameStarterType = assemblyDefinition.MainModule.Types.FirstOrDefault(t => t.FullName == "Kingmaker.GameStarter");
            var initProcessMethod = gameStarterType?.Methods.FirstOrDefault(m => m.Name == "InitProcess");
            var ilProcessor = initProcessMethod.Body.GetILProcessor();
            var curAssembly = AssemblyDefinition.ReadAssembly(Assembly.GetExecutingAssembly().Location);
            var myCustomClassType = assemblyDefinition.MainModule.ImportReference(curAssembly.MainModule.Types.First(t => t.Name == nameof(SteamWorkshopIntegration)));
            var initializeMethod = new MethodReference(nameof(SteamWorkshopIntegration.Start), assemblyDefinition.MainModule.TypeSystem.Void, myCustomClassType) { HasThis = true };
            var afterTargetInstruction = initProcessMethod.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Ldstr && i.Operand is string arg && arg == "Starting OwlcatUnityModManager");
            var targetInstruction = initProcessMethod.Body.Instructions.ElementAt(initProcessMethod.Body.Instructions.IndexOf(afterTargetInstruction) - 1);
            var instanceGetterMethod = new MethodReference("get_Instance", myCustomClassType, myCustomClassType) { HasThis = false };
            ilProcessor.InsertBefore(targetInstruction, ilProcessor.Create(OpCodes.Call, instanceGetterMethod));
            ilProcessor.InsertBefore(targetInstruction, ilProcessor.Create(OpCodes.Callvirt, initializeMethod));
            assemblyDefinition.Write(origCodePath);

        }
    }

    class CustomAssemblyResolver : BaseAssemblyResolver {
        private readonly string[] _directories;

        public CustomAssemblyResolver(params string[] directories) {
            _directories = directories;
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) {
            // Attempt to resolve using default logic
            try {
                return base.Resolve(name, parameters);
            } catch (AssemblyResolutionException) {
                // If resolution fails, try locating assembly in provided directories
                foreach (var directory in _directories) {
                    string assemblyPath = Path.Combine(directory, name.Name + ".dll");
                    if (File.Exists(assemblyPath)) {
                        return AssemblyDefinition.ReadAssembly(assemblyPath, parameters);
                    }
                }
                throw;
            }
        }
    }
}
