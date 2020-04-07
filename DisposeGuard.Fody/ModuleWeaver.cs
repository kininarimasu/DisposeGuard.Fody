using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace DisposeGuard.Fody
{
    public class ModuleWeaver : BaseModuleWeaver
    {
        readonly string suffix = Guid.NewGuid().ToString();
        // MethodReference ObjectDisposedExceptionRef;
        // MethodReference ExceptionRef;

        // enum OnFinalize
        // {
        //     Nothing,
        //     Dispose,
        //     Custom,
        // }

        public override void Execute()
        {
            var onFinalizeAttr = Config.Attributes().FirstOrDefault(x => x.Name == "OnFinalize").Value.ToString();
            // OnFinalize onFinalize = OnFinalize.Nothing;
            // MethodReference customMethod = null;
            MethodReference finalizer = null;

            if (!string.IsNullOrWhiteSpace(onFinalizeAttr))// && onFinalizeAttr.Contains('.'))
            {
                try
                {
                    finalizer = ModuleDefinition
                        .GetTypes()
                        .First(x =>
                            x.IsClass &&
                            x.FullName == Path.GetFileNameWithoutExtension(onFinalizeAttr)
                        )
                        .Methods
                        .First(x =>
                            x.IsStatic &&
                            x.IsMatch(
                                Path.GetExtension(onFinalizeAttr).TrimStart('.'),
                                "IDisposable",
                                ModuleDefinition.TypeSystem.Boolean.Name
                            )
                        );
                }
                catch {}

                if (finalizer == null)
                {
                    LogError($"Could not find static method '{onFinalizeAttr}(IDisposable, bool)'");
                    return;
                }
                else
                {
                    LogInfo($"Finalizer for disposable objects registered '{finalizer.FullName}'");
                }
            }

            /*switch (onFinalizeAttr?.Value.ToString())
            {
                // case "Nothing":
                //     onFinalize = OnFinalize.Nothing;
                //     break;
                case "Dispose":
                    onFinalize = OnFinalize.Dispose;
                    break;
                case "Custom":
                    onFinalize = OnFinalize.Custom;
                    customMethod = ModuleDefinition
                        .GetTypes()
                        .Where(x =>
                            x.IsClass &&
                            !x.IsGeneratedCode() &&
                            x.Methods.Any(x =>
                                x.IsStatic &&
                                x.CustomAttributes.ContainsOnFinalize() &&
                                x.IsMatch(ModuleDefinition.TypeSystem.Object.Name, ModuleDefinition.TypeSystem.Boolean.Name)
                            )
                        );
                    if (customMethod == null)
                    {
                        LogError("Could not find static method with attribute 'DisposeGuard.Fody.OnFinalize'");
                        return;
                    }
                    break;
                    // default:
                    //     onFinalize = OnFinalize.Nothing;
                    //     break;
                    // LogWarning("You must specify 'OnFinalize' parameter.");
                    // return;
            }*/

            var objectDisposedExceptionCtor = ModuleDefinition.ImportReference(
                FindType("System.ObjectDisposedException").Find(".ctor", "String")
            );
            var exceptionCtor = ModuleDefinition.ImportReference(
                FindType("System.Exception").Find(".ctor", "String")
            );

            foreach (var type in ModuleDefinition
                .GetTypes()
                .Where(x =>
                    x.IsClass &&
                    // !x.IsAbstract &&
                    !x.IsGeneratedCode() //&&
                                         // !x.CustomAttributes.ContainsDoNotTrack()
                )
            )
            {
                // if (!type.Interfaces.Any(x => x.InterfaceType.FullName == "System.IDisposable"))
                // {
                //     continue;
                // }

                var disposeMethod = type.Methods
                    .FirstOrDefault(x =>
                        !x.IsStatic &&
                        !x.HasParameters &&
                        (x.Name == "Dispose" || x.Name == "System.IDisposable.Dispose")
                    );

                if (disposeMethod == null)
                {
                    // LogInfo($"Cannot find dispose method in class {type.FullName}");
                    continue;
                }

                if (!isIDisposable(type))
                {
                    LogWarning($"Class '{type.FullName}' contains 'Dispose' method but not implements 'IDisposable' interface");
                    continue;
                }

                LogInfo($"Patching class '{type.FullName}'");

                var disposedField = createDisposedField();
                type.Fields.Add(disposedField);



                ////// MODIFY DISPOSE begin //////
                {
                    // var validSequencePoint = disposeMethod.DebugInformation.SequencePoints.FirstOrDefault();
                    disposeMethod.Body.SimplifyMacros();
                    disposeMethod.Body.Instructions.InsertAtStart(
                        Instruction.Create(OpCodes.Ldarg_0),
                        Instruction.Create(OpCodes.Ldc_I4_1),
                        Instruction.Create(OpCodes.Stfld, disposedField)
                    );
                    // if (validSequencePoint != null)
                    //     CecilExtensions.HideLineFromDebugger(validSequencePoint);
                    disposeMethod.Body.OptimizeMacros();
                }
                ////// MODIFY DISPOSE end //////



                var throwIfDisposedMethod = createThrowIfDisposedMethod(disposedField, type.FullName);
                type.Methods.Add(throwIfDisposedMethod);



                ////// ADD GUARDS begin //////
                foreach (var method in type.Methods)
                {
                    if (method.Name == ".ctor")
                        continue;
                    if (method.IsMatch("Finalize"))
                        continue;
                    if (method.IsStatic)
                        continue;
                    if (method.Name == "Dispose")
                        continue;
                    if (method.Name == "IsDisposed")
                        continue;
                    if (method.Name == throwIfDisposedMethod.Name)
                        continue;
                    // if (!method.HasBody)
                    //     continue;
                    if (method.IsPrivate)
                        continue;

                    var validSequencePoint = method.DebugInformation.SequencePoints.FirstOrDefault();
                    method.Body.SimplifyMacros();
                    method.Body.Instructions.InsertAtStart(
                        Instruction.Create(OpCodes.Ldarg_0),
                        Instruction.Create(OpCodes.Call, throwIfDisposedMethod)
                    );
                    if (validSequencePoint != null)
                        CecilExtensions.HideLineFromDebugger(validSequencePoint);
                    method.Body.OptimizeMacros();
                }
                ////// ADD GUARDS end //////



                ////// MODIFY FINALIZER begin //////
                MethodDefinition _finalizeMethod()
                {
                    var finalizeMethod = type.Methods.FirstOrDefault(x => !x.IsStatic && x.IsMatch("Finalize"));
                    if (finalizeMethod == null)
                    {
                        finalizeMethod = new MethodDefinition(
                            "Finalize",
                            MethodAttributes.HideBySig | MethodAttributes.Family | MethodAttributes.Virtual,
                            ModuleDefinition.TypeSystem.Void
                        );
                        finalizeMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        type.Methods.Add(finalizeMethod);
                    }
                    return finalizeMethod;
                }

                if (finalizer != null)
                {
                    var method = _finalizeMethod();
                    method.Body.SimplifyMacros();
                    method.Body.Instructions.InsertAtStart(
                        Instruction.Create(OpCodes.Ldarg_0),
                        Instruction.Create(OpCodes.Ldarg_0),
                        Instruction.Create(OpCodes.Ldfld, disposedField),
                        Instruction.Create(OpCodes.Call, finalizer)
                    );
                    method.Body.OptimizeMacros();
                }

                /*switch (onFinalize)
                {
                    case OnFinalize.Dispose:
                        {
                            var method = _finalizeMethod();
                            method.Body.SimplifyMacros();
                            method.Body.Instructions.InsertAtStart(
                                Instruction.Create(OpCodes.Ldarg_0),
                                Instruction.Create(OpCodes.Call, disposeMethod)
                            );
                            method.Body.OptimizeMacros();
                        }
                        break;
                    case OnFinalize.Custom:
                        {
                            var method = _finalizeMethod();
                            method.Body.SimplifyMacros();
                            method.Body.Instructions.InsertAtStart(
                                Instruction.Create(OpCodes.Ldarg_0),
                                Instruction.Create(OpCodes.Ldarg_0),
                                Instruction.Create(OpCodes.Ldfld, disposedField),
                                Instruction.Create(OpCodes.Call, customMethod)
                            );
                            method.Body.OptimizeMacros();
                        }
                        break;
                }*/
                ////// MODIFY FINALIZER end //////

                // var disposeMethod = disposeMethods.FirstOrDefault(x => !x.HasParameters);
                // if (disposeMethod == null)
                // {
                // 	// If the base type is not in the same assembly as the type we're processing
                // 	// then we want to patch the Dispose method. If it is in the same
                // 	// assembly then the patch code gets added to the Dispose method of the
                // 	// base class, so we skip this type.
                // 	if (type.BaseType.Scope == type.Scope)
                // 		continue;

                // 	disposeMethod = disposeMethods[0];
                // }

                // ProcessDisposeMethod(disposeMethod);

                // var constructors = type.Methods.Where(x => !x.IsStatic && x.IsConstructor).ToList();
                // if (constructors.Count != 0)
                // {
                // 	foreach (var ctor in constructors)
                // 	{
                // 		ProcessConstructor(ctor);
                // 	}
                // }
            }

            // CleanReferences();

            FieldDefinition createDisposedField()
            {
                return new FieldDefinition(
                    "__disposed__" + suffix,
                    FieldAttributes.Private | FieldAttributes.HasFieldRVA,
                    ModuleDefinition.TypeSystem.Boolean
                );
            }

            MethodDefinition createThrowIfDisposedMethod(FieldDefinition disposedField, string className)
            {
                var method = new MethodDefinition(
                    "__ThrowIfDisposed__" + suffix,
                    MethodAttributes.Private | MethodAttributes.HideBySig,
                    ModuleDefinition.TypeSystem.Void
                );

                var returnInstruction = Instruction.Create(OpCodes.Ret);
                method.Body.Instructions.InsertAtStart(
                    Instruction.Create(OpCodes.Ldarg_0),
                    Instruction.Create(OpCodes.Ldfld, disposedField),
                    Instruction.Create(OpCodes.Brfalse_S, returnInstruction),
                    Instruction.Create(OpCodes.Ldstr, className),
                    Instruction.Create(OpCodes.Newobj, objectDisposedExceptionCtor),
                    Instruction.Create(OpCodes.Throw),
                    returnInstruction
                );

                return method;
            }

            bool isIDisposable(TypeDefinition type)
            {
                if (type.Interfaces.Any(i => i.InterfaceType.FullName.Equals("System.IDisposable")))
                {
                    return true;
                }
                if (type.FullName.Equals("System.IDisposable"))
                {
                    return true;
                }
                return type.BaseType != null && isIDisposable(type.BaseType.Resolve());
            }
        }

        public override IEnumerable<string> GetAssembliesForScanning()
        {
            yield return "netstandard";
            yield return "mscorlib";
        }

        public override bool ShouldCleanReference => true;
    }
}
