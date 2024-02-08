using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.IO;
using Kingmaker.Modding;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Injector {
    // This class patches the original game assembly to test under somewhat realistic conditions
    class Program {
        static void Main() {
            string gamePath = FindInstallLocation();
            if (gamePath == "") {
                Console.WriteLine("Could not find Steam Game File location (by scanning Player.Log). Patching failed.");
                return;
            }
            string assembliesPath = Path.Combine(gamePath, "WH40KRT_Data", "Managed");
            var origCodePath = Path.Combine(assembliesPath, "RogueTrader.ModInitializer.dll");
            var originalCodeAssembly = new FileInfo(origCodePath);
            var f = new FileInfo(origCodePath + ".orig");
            if (f.Exists) f.Delete();
            originalCodeAssembly.MoveTo(origCodePath + ".orig");
            try {
                var resolver = new CustomAssemblyResolver(assembliesPath);
                var parameters = new ReaderParameters { AssemblyResolver = resolver };
                // Add SteamWorkshopIntegration.Instance.Start at the beginning of ModInitializer.InitializeMods
                var assemblyDefinition = AssemblyDefinition.ReadAssembly(origCodePath + ".orig", parameters);
                var gameStarterType = assemblyDefinition.MainModule.Types.FirstOrDefault(t => t.FullName == "Code.GameCore.Modding.ModInitializer");
                var initProcessMethod = gameStarterType?.Methods.FirstOrDefault(m => m.Name == "InitializeMods");
                var ilProcessor = initProcessMethod.Body.GetILProcessor();

                var curAssembly = AssemblyDefinition.ReadAssembly(Assembly.GetExecutingAssembly().Location);
                var myCustomClassType = assemblyDefinition.MainModule.ImportReference(curAssembly.MainModule.Types.First(t => t.Name == nameof(SteamWorkshopIntegration)));
                var initializeMethod = new MethodReference(nameof(SteamWorkshopIntegration.Start), assemblyDefinition.MainModule.TypeSystem.Void, myCustomClassType) { HasThis = true };
                var instanceGetterMethod = new MethodReference("get_Instance", myCustomClassType, myCustomClassType) { HasThis = false };
                var getterInst = ilProcessor.Create(OpCodes.Call, instanceGetterMethod);
                var callInst = ilProcessor.Create(OpCodes.Callvirt, initializeMethod);
                if (InststructionEquals(getterInst, initProcessMethod.Body.Instructions.ElementAt(0)) && InststructionEquals(callInst, initProcessMethod.Body.Instructions.ElementAt(1))) {
                    Console.WriteLine("Game Files already patched.");
                } else {
                    var targetInstruction = initProcessMethod.Body.Instructions.First();
                    ilProcessor.InsertBefore(targetInstruction, getterInst);
                    ilProcessor.InsertBefore(targetInstruction, callInst);
                    Console.WriteLine("Succeeded in Patching.");
                }
                assemblyDefinition.Write(origCodePath);
                assemblyDefinition.Dispose();
                File.Delete(origCodePath + ".orig");
                var targetPath = Path.Combine(assembliesPath, Assembly.GetExecutingAssembly().GetName().Name + ".dll");
                if (!File.Exists(targetPath)) File.Copy(Assembly.GetExecutingAssembly().Location, targetPath);
            } catch (Exception ex) {
                Console.WriteLine(ex.ToString());
                File.Move(origCodePath + ".orig", origCodePath);
            }
        }
        private static bool InststructionEquals(Instruction i1, Instruction i2) {
            return (i1.OpCode == i2.OpCode) && (i1.Operand.ToString() == i2.Operand.ToString());
        }
        private static string FindInstallLocation() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                var rogueTraderDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low", "Owlcat Games", "Warhammer 40000 Rogue Trader", "Player.log");
                string lineToFind = "Mono path[0]";
                string line = null;
                foreach (var lineIter in File.ReadLines(rogueTraderDataPath)) {
                    if (lineIter.Contains(lineToFind)) {
                        line = lineIter;
                        break;
                    }
                }
                string monoPathRegex = @"^Mono path\[0\] = '(.*?)/WH40KRT_Data/Managed'$";
                Match match = Regex.Match(line, monoPathRegex);
                if (match.Success) {
                    return match.Groups[1].Value;
                }
            } else {
            }
            return "";
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
