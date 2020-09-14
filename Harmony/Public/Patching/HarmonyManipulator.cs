using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib.Internal.Patching;
using HarmonyLib.Internal.Util;
using HarmonyLib.Tools;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace HarmonyLib.Public.Patching
{
	/// <summary>
	///    IL manipulator to create Harmony-style patches
	/// </summary>
	///
	public static class HarmonyManipulator
	{
		private static readonly string INSTANCE_PARAM = "__instance";
		private static readonly string ORIGINAL_METHOD_PARAM = "__originalMethod";
		private static readonly string RUN_ORIGINAL_PARAM = "__runOriginal";
		private static readonly string RESULT_VAR = "__result";
		private static readonly string STATE_VAR = "__state";
		private static readonly string EXCEPTION_VAR = "__exception";
		private static readonly string PARAM_INDEX_PREFIX = "__";
		private static readonly string INSTANCE_FIELD_PREFIX = "___";

		private static readonly MethodInfo GetMethodFromHandle1 =
			typeof(MethodBase).GetMethod("GetMethodFromHandle", new[] {typeof(RuntimeMethodHandle)});

		private static readonly MethodInfo GetMethodFromHandle2 = typeof(MethodBase).GetMethod("GetMethodFromHandle",
			new[] {typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle)});

		private static void SortPatches(MethodBase original, PatchInfo patchInfo, out List<MethodInfo> prefixes,
			out List<MethodInfo> postfixes, out List<MethodInfo> transpilers,
			out List<MethodInfo> finalizers)
		{
			Patch[] prefixesArr, postfixesArr, transpilersArr, finalizersArr;

			// Lock to ensure no more patches are added while we're sorting
			lock (patchInfo)
			{
				prefixesArr = patchInfo.prefixes.ToArray();
				postfixesArr = patchInfo.postfixes.ToArray();
				transpilersArr = patchInfo.transpilers.ToArray();
				finalizersArr = patchInfo.finalizers.ToArray();
			}

			// debug is useless; debug logs passed on-demand
			prefixes = PatchFunctions.GetSortedPatchMethods(original, prefixesArr);
			postfixes = PatchFunctions.GetSortedPatchMethods(original, postfixesArr);
			transpilers = PatchFunctions.GetSortedPatchMethods(original, transpilersArr);
			finalizers = PatchFunctions.GetSortedPatchMethods(original, finalizersArr);
		}

		/// <summary>
		/// Manipulates a <see cref="Mono.Cecil.Cil.MethodBody"/> by applying Harmony patches to it.
		/// </summary>
		/// <param name="original">Reference to the method that should be considered as original. Used to reference parameter and return types.</param>
		/// <param name="patchInfo">Collection of Harmony patches to apply.</param>
		/// <param name="ctx">Method body to manipulate as <see cref="ILContext"/> instance. Should contain instructions to patch.</param>
		/// <remarks>
		/// In most cases you will want to use <see cref="PatchManager.ToPatchInfo"/> to create or obtain global
		/// patch info for the method that contains aggregated info of all Harmony instances.
		/// </remarks>
		///
		public static void Manipulate(MethodBase original, PatchInfo patchInfo, ILContext ctx)
		{
			SortPatches(original, patchInfo, out var sortedPrefixes, out var sortedPostfixes, out var sortedTranspilers,
				out var sortedFinalizers);

			Logger.Log(Logger.LogChannel.Info, () =>
			{
				var sb = new StringBuilder();

				sb.AppendLine(
					$"Patching {original.FullDescription()} with {sortedPrefixes.Count} prefixes, {sortedPostfixes.Count} postfixes, {sortedTranspilers.Count} transpilers, {sortedFinalizers.Count} finalizers");

				void Print(List<MethodInfo> list, string type)
				{
					if (list.Count == 0)
						return;
					sb.AppendLine($"{list.Count} {type}:");
					foreach (var fix in list)
						sb.AppendLine($"* {fix.FullDescription()}");
				}

				Print(sortedPrefixes, "prefixes");
				Print(sortedPostfixes, "postfixes");
				Print(sortedTranspilers, "transpilers");
				Print(sortedFinalizers, "finalizers");

				return sb.ToString();
			});

			MakePatched(original, ctx, sortedPrefixes, sortedPostfixes, sortedTranspilers, sortedFinalizers);
		}

		private static void WriteTranspiledMethod(ILContext ctx, MethodBase original, List<MethodInfo> transpilers)
		{
			if (transpilers.Count == 0)
				return;

			Logger.Log(Logger.LogChannel.Info, () => $"Transpiling {original.FullDescription()}");

			// Create a high-level manipulator for the method
			var manipulator = new ILManipulator(ctx.Body);

			// Add in all transpilers
			foreach (var transpilerMethod in transpilers)
				manipulator.AddTranspiler(transpilerMethod);

			// Write new manipulated code to our body
			manipulator.WriteTo(ctx.Body, original);
		}

		private static ILEmitter.Label MakeReturnLabel(ILEmitter il)
		{
			// We replace all `ret`s with a simple branch to force potential execution of post-original code

			// Create a helper label as well
			// We mark the label as not emitted so that potential postfix code can mark it
			var resultLabel = il.DeclareLabel();
			resultLabel.emitted = false;

			var hasRet = false;
			foreach (var ins in il.IL.Body.Instructions.Where(ins => ins.MatchRet()))
			{
				hasRet = true;
				ins.OpCode = OpCodes.Br;
				ins.Operand = resultLabel.instruction;
				resultLabel.targets.Add(ins);
			}

			// Pick `nop` if previously the method didn't have `ret` before, like in case of exception throwing
			resultLabel.instruction = Instruction.Create(hasRet ? OpCodes.Ret : OpCodes.Nop);

			// Already append ending label for other code to use as emitBefore point
			il.IL.Append(resultLabel.instruction);

			return resultLabel;
		}

		private static void WritePostfixes(ILEmitter il, MethodBase original, ILEmitter.Label returnLabel,
			Dictionary<string, VariableDefinition> variables, List<MethodInfo> postfixes)
		{
			// Postfix layout:
			// Make return value (if needed) into a variable
			// If method has return value, store the current stack value into it (since the value on the stack is the return value)
			// Call postfixes that modify return values by __return
			// Call postfixes that modify return values by chaining

			if (postfixes.Count == 0)
				return;

			Logger.Log(Logger.LogChannel.Info, () => "Writing postfixes");

			// Get the last instruction (expected to be `ret`)
			il.emitBefore = il.IL.Body.Instructions[il.IL.Body.Instructions.Count - 1];

			// Mark the original method return label here
			il.MarkLabel(returnLabel);

			if (!variables.TryGetValue(RESULT_VAR, out var returnValueVar))
			{
				var retVal = AccessTools.GetReturnedType(original);
				returnValueVar = variables[RESULT_VAR] = retVal == typeof(void) ? null : il.DeclareVariable(retVal);
			}

			if (returnValueVar != null)
				il.Emit(OpCodes.Stloc, returnValueVar);

			foreach (var postfix in postfixes.Where(p => p.ReturnType == typeof(void)))
			{
				EmitCallParameter(il, original, postfix, variables, true);
				il.Emit(OpCodes.Call, postfix);
			}

			// Load the result for the final time, the chained postfixes will handle the rest
			if (returnValueVar != null)
				il.Emit(OpCodes.Ldloc, returnValueVar);

			// If postfix returns a value, it must be chainable
			// The first param is always the return of the previous
			foreach (var postfix in postfixes.Where(p => p.ReturnType != typeof(void)))
			{
				EmitCallParameter(il, original, postfix, variables, true);
				il.Emit(OpCodes.Call, postfix);

				var firstParam = postfix.GetParameters().FirstOrDefault();

				if (firstParam == null || postfix.ReturnType != firstParam.ParameterType)
				{
					if (firstParam != null)
						throw new InvalidHarmonyPatchArgumentException(
							$"Return type of pass through postfix {postfix.FullDescription()} does not match type of its first parameter",
							original, postfix);
					throw new InvalidHarmonyPatchArgumentException(
						$"Postfix patch {postfix.FullDescription()} must have `void` as return type", original, postfix);
				}
			}
		}

		private static void WritePrefixes(ILEmitter il, MethodBase original, ILEmitter.Label returnLabel,
			Dictionary<string, VariableDefinition> variables, List<MethodInfo> prefixes)
		{
			// Prefix layout:
			// Make return value (if needed) into a variable
			// Call prefixes
			// If method returns a value, add additional logic to allow skipping original method

			if (prefixes.Count == 0)
				return;

			Logger.Log(Logger.LogChannel.Info, () => "Writing prefixes");

			// Start emitting at the start
			il.emitBefore = il.IL.Body.Instructions[0];

			if (!variables.TryGetValue(RESULT_VAR, out var returnValueVar))
			{
				var retVal = AccessTools.GetReturnedType(original);
				returnValueVar = variables[RESULT_VAR] = retVal == typeof(void) ? null : il.DeclareVariable(retVal);
			}

			// A prefix that can modify control flow has one of the following:
			// * It returns a boolean
			// * It declares bool __runOriginal
			var canModifyControlFlow = prefixes.Any(p => p.ReturnType == typeof(bool) ||
			                                             p.GetParameters()
				                                             .Any(pp => pp.Name == RUN_ORIGINAL_PARAM &&
				                                                        pp.ParameterType.OpenRefType() == typeof(bool)));

			// Flag to check if the orignal method should be run (or was run)
			// Always present so other patchers can access it
			var runOriginal = variables[RUN_ORIGINAL_PARAM] = il.DeclareVariable(typeof(bool));
			// Init runOriginal to true
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Stloc, runOriginal);

			// If runOriginal flag exists, we need to add more logic to the method end
			var postProcessTarget = returnValueVar != null ? il.DeclareLabel() : returnLabel;

			foreach (var prefix in prefixes)
			{
				EmitCallParameter(il, original, prefix, variables, false);
				il.Emit(OpCodes.Call, prefix);

				if (!AccessTools.IsVoid(prefix.ReturnType))
				{
					if (prefix.ReturnType != typeof(bool))
						throw new InvalidHarmonyPatchArgumentException(
							$"Prefix patch {prefix.FullDescription()} has return type {prefix.ReturnType}, but only `bool` or `void` are permitted",
							original, prefix);

					if (canModifyControlFlow)
					{
						// AND the current runOriginal to return value of the method (if any)
						il.Emit(OpCodes.Ldloc, runOriginal);
						il.Emit(OpCodes.And);
						il.Emit(OpCodes.Stloc, runOriginal);
					}
				}
			}

			if (!canModifyControlFlow)
				return;

			// If runOriginal is false, branch automatically to the end
			il.Emit(OpCodes.Ldloc, runOriginal);
			il.Emit(OpCodes.Brfalse, postProcessTarget);

			if (returnValueVar == null)
				return;

			// Finally, load return value onto stack at the end
			il.emitBefore = il.IL.Body.Instructions[il.IL.Body.Instructions.Count - 1];
			il.MarkLabel(postProcessTarget);
			il.Emit(OpCodes.Ldloc, returnValueVar);
		}

		private static void WriteFinalizers(ILEmitter il, MethodBase original, ILEmitter.Label returnLabel,
			Dictionary<string, VariableDefinition> variables,
			List<MethodInfo> finalizers)
		{
			// Finalizer layout:
			// Create __exception variable to store exception info and a skip flag
			// Wrap the whole method into a try/catch
			// Call finalizers at the end of method (simulate `finally`)
			// If __exception got set, throw it
			// Begin catch block
			// Store exception into __exception
			// If skip flag is set, skip finalizers
			// Call finalizers
			// If __exception is still set, rethrow (if new exception set, otherwise throw the new exception)
			// End catch block

			if (finalizers.Count == 0)
				return;

			Logger.Log(Logger.LogChannel.Info, () => "Writing finalizers");

			// Create variables to hold custom exception
			variables[EXCEPTION_VAR] = il.DeclareVariable(typeof(Exception));

			// Create a flag to signify that finalizers have been run
			// Cecil DMD fix: initialize it to false
			var skipFinalizersVar = il.DeclareVariable(typeof(bool));
			il.emitBefore = il.IL.Body.Instructions[0];
			il.Emit(OpCodes.Ldc_I4_0);
			il.Emit(OpCodes.Stloc, skipFinalizersVar);


			il.emitBefore = il.IL.Body.Instructions[il.IL.Body.Instructions.Count - 1];

			// Mark the original method return label here if it hasn't been yet
			il.MarkLabel(returnLabel);

			if (!variables.TryGetValue(RESULT_VAR, out var returnValueVar))
			{
				var retVal = AccessTools.GetReturnedType(original);
				returnValueVar = variables[RESULT_VAR] = retVal == typeof(void) ? null : il.DeclareVariable(retVal);
			}

			// Start main exception block
			var mainBlock = il.BeginExceptionBlock(il.DeclareLabelFor(il.IL.Body.Instructions[0]));

			bool WriteFinalizerCalls(bool suppressExceptions)
			{
				var canRethrow = true;

				foreach (var finalizer in finalizers)
				{
					var start = il.DeclareLabel();
					il.MarkLabel(start);

					EmitCallParameter(il, original, finalizer, variables, false);
					il.Emit(OpCodes.Call, finalizer);

					if (finalizer.ReturnType != typeof(void))
					{
						il.Emit(OpCodes.Stloc, variables[EXCEPTION_VAR]);
						canRethrow = false;
					}

					if (suppressExceptions)
					{
						var exBlock = il.BeginExceptionBlock(start);

						il.BeginHandler(exBlock, ExceptionHandlerType.Catch, typeof(object));
						il.Emit(OpCodes.Pop);
						il.EndExceptionBlock(exBlock);
					}
				}

				return canRethrow;
			}

			// First, store potential result into a variable and empty the stack
			if (returnValueVar != null)
				il.Emit(OpCodes.Stloc, returnValueVar);

			// Write finalizers inside the `try`
			WriteFinalizerCalls(false);

			// Mark finalizers as skipped so they won't rerun
			il.Emit(OpCodes.Ldc_I4_1);
			il.Emit(OpCodes.Stloc, skipFinalizersVar);

			// If __exception is not null, throw
			var skipLabel = il.DeclareLabel();
			il.Emit(OpCodes.Ldloc, variables[EXCEPTION_VAR]);
			il.Emit(OpCodes.Brfalse, skipLabel);
			il.Emit(OpCodes.Ldloc, variables[EXCEPTION_VAR]);
			il.Emit(OpCodes.Throw);
			il.MarkLabel(skipLabel);

			// Begin a generic `catch(Exception o)` here and capture exception into __exception
			il.BeginHandler(mainBlock, ExceptionHandlerType.Catch, typeof(Exception));
			il.Emit(OpCodes.Stloc, variables[EXCEPTION_VAR]);

			// Call finalizers or skip them if needed
			il.Emit(OpCodes.Ldloc, skipFinalizersVar);
			var postFinalizersLabel = il.DeclareLabel();
			il.Emit(OpCodes.Brtrue, postFinalizersLabel);

			var rethrowPossible = WriteFinalizerCalls(true);

			il.MarkLabel(postFinalizersLabel);

			// Possibly rethrow if __exception is still not null (i.e. suppressed)
			skipLabel = il.DeclareLabel();
			il.Emit(OpCodes.Ldloc, variables[EXCEPTION_VAR]);
			il.Emit(OpCodes.Brfalse, skipLabel);
			if (rethrowPossible)
				il.Emit(OpCodes.Rethrow);
			else
			{
				il.Emit(OpCodes.Ldloc, variables[EXCEPTION_VAR]);
				il.Emit(OpCodes.Throw);
			}

			il.MarkLabel(skipLabel);
			il.EndExceptionBlock(mainBlock);

			if (returnValueVar != null)
				il.Emit(OpCodes.Ldloc, returnValueVar);
		}

		private static void MakePatched(MethodBase original, ILContext ctx,
			List<MethodInfo> prefixes, List<MethodInfo> postfixes,
			List<MethodInfo> transpilers, List<MethodInfo> finalizers)
		{
			try
			{
				if (original == null)
					throw new ArgumentException(nameof(original));

				Logger.Log(Logger.LogChannel.Info, () => $"Running ILHook manipulator on {original.FullDescription()}");

				WriteTranspiledMethod(ctx, original, transpilers);

				// If no need to wrap anything, we're basically done!
				if (prefixes.Count + postfixes.Count + finalizers.Count == 0)
				{
					Logger.Log(Logger.LogChannel.IL,
						() => $"Generated patch ({ctx.Method.FullName}):\n{ctx.Body.ToILDasmString()}");
					return;
				}

				var il = new ILEmitter(ctx.IL);
				var returnLabel = MakeReturnLabel(il);
				var variables = new Dictionary<string, VariableDefinition>();

				// Collect state variables
				foreach (var nfix in prefixes.Union(postfixes).Union(finalizers))
					if (nfix.DeclaringType != null && variables.ContainsKey(nfix.DeclaringType.FullName) == false)
						foreach (var patchParam in nfix
							.GetParameters().Where(patchParam => patchParam.Name == STATE_VAR))
							variables[nfix.DeclaringType.FullName] =
								il.DeclareVariable(patchParam.ParameterType.OpenRefType()); // Fix possible reftype

				WritePrefixes(il, original, returnLabel, variables, prefixes);
				WritePostfixes(il, original, returnLabel, variables, postfixes);
				WriteFinalizers(il, original, returnLabel, variables, finalizers);

				// Mark return label in case it hasn't been marked yet and close open labels to return
				il.MarkLabel(returnLabel);
				var lastInstruction = il.SetOpenLabelsTo(ctx.Instrs[ctx.Instrs.Count - 1]);

				// If we have finalizers, ensure the return label is `ret` and not `nop`
				if (finalizers.Count > 0)
					lastInstruction.OpCode = OpCodes.Ret;

				Logger.Log(Logger.LogChannel.IL,
					() => $"Generated patch ({ctx.Method.FullName}):\n{ctx.Body.ToILDasmString()}");
			}
			catch (Exception e)
			{
				Logger.Log(Logger.LogChannel.Error, () => $"Failed to patch {original.FullDescription()}: {e}");
			}
		}

		private static OpCode GetIndOpcode(Type type)
		{
			if (type.IsEnum)
				return OpCodes.Ldind_I4;

			if (type == typeof(float)) return OpCodes.Ldind_R4;
			if (type == typeof(double)) return OpCodes.Ldind_R8;

			if (type == typeof(byte)) return OpCodes.Ldind_U1;
			if (type == typeof(ushort)) return OpCodes.Ldind_U2;
			if (type == typeof(uint)) return OpCodes.Ldind_U4;
			if (type == typeof(ulong)) return OpCodes.Ldind_I8;

			if (type == typeof(sbyte)) return OpCodes.Ldind_I1;
			if (type == typeof(short)) return OpCodes.Ldind_I2;
			if (type == typeof(int)) return OpCodes.Ldind_I4;
			if (type == typeof(long)) return OpCodes.Ldind_I8;

			return OpCodes.Ldind_Ref;
		}

		private static bool EmitOriginalBaseMethod(ILEmitter il, MethodBase original)
		{
			if (original is MethodInfo method)
				il.Emit(OpCodes.Ldtoken, method);
			else if (original is ConstructorInfo constructor)
				il.Emit(OpCodes.Ldtoken, constructor);
			else return false;

			var type = original.ReflectedType;
			if (type.IsGenericType) il.Emit(OpCodes.Ldtoken, type);
			il.Emit(OpCodes.Call, type.IsGenericType ? GetMethodFromHandle2 : GetMethodFromHandle1);
			return true;
		}

		private static void EmitCallParameter(ILEmitter il, MethodBase original, MethodInfo patch,
			Dictionary<string, VariableDefinition> variables, bool allowFirsParamPassthrough)
		{
			var isInstance = original.IsStatic is false;
			var originalParameters = original.GetParameters();
			var originalParameterNames = originalParameters.Select(p => p.Name).ToArray();

			// check for passthrough using first parameter (which must have same type as return type)
			var parameters = patch.GetParameters().ToList();
			if (allowFirsParamPassthrough && patch.ReturnType != typeof(void) && parameters.Count > 0 &&
			    parameters[0].ParameterType == patch.ReturnType)
				parameters.RemoveRange(0, 1);

			foreach (var patchParam in parameters)
			{
				if (patchParam.Name == ORIGINAL_METHOD_PARAM)
				{
					if (EmitOriginalBaseMethod(il, original))
						continue;

					il.Emit(OpCodes.Ldnull);
					continue;
				}

				if (patchParam.Name == INSTANCE_PARAM)
				{
					if (original.IsStatic)
						il.Emit(OpCodes.Ldnull);
					else
					{
						var instanceIsRef = original.DeclaringType is object && AccessTools.IsStruct(original.DeclaringType);
						var parameterIsRef = patchParam.ParameterType.IsByRef;
						if (instanceIsRef == parameterIsRef) il.Emit(OpCodes.Ldarg_0);
						if (instanceIsRef && parameterIsRef is false)
						{
							il.Emit(OpCodes.Ldarg_0);
							il.Emit(OpCodes.Ldobj, original.DeclaringType);
						}

						if (instanceIsRef is false && parameterIsRef) il.Emit(OpCodes.Ldarga, 0);
					}

					continue;
				}

				if (patchParam.Name.StartsWith(INSTANCE_FIELD_PREFIX, StringComparison.Ordinal))
				{
					var fieldName = patchParam.Name.Substring(INSTANCE_FIELD_PREFIX.Length);
					FieldInfo fieldInfo;
					if (fieldName.All(char.IsDigit))
					{
						// field access by index only works for declared fields
						fieldInfo = AccessTools.DeclaredField(original.DeclaringType, int.Parse(fieldName));
						if (fieldInfo is null)
							throw new ArgumentException(
								$"No field found at given index in class {original.DeclaringType.FullName}", fieldName);
					}
					else
					{
						fieldInfo = AccessTools.Field(original.DeclaringType, fieldName);
						if (fieldInfo is null)
							throw new ArgumentException($"No such field defined in class {original.DeclaringType.FullName}",
								fieldName);
					}

					if (fieldInfo.IsStatic)
						il.Emit(patchParam.ParameterType.IsByRef ? OpCodes.Ldsflda : OpCodes.Ldsfld, fieldInfo);
					else
					{
						il.Emit(OpCodes.Ldarg_0);
						il.Emit(patchParam.ParameterType.IsByRef ? OpCodes.Ldflda : OpCodes.Ldfld, fieldInfo);
					}

					continue;
				}

				// state is special too since each patch has its own local var
				if (patchParam.Name == STATE_VAR)
				{
					var ldlocCode = patchParam.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
					if (variables.TryGetValue(patch.DeclaringType.FullName, out var stateVar))
						il.Emit(ldlocCode, stateVar);
					else
						il.Emit(OpCodes.Ldnull);
					continue;
				}

				// treat __result var special
				if (patchParam.Name == RESULT_VAR)
				{
					var returnType = AccessTools.GetReturnedType(original);
					if (returnType == typeof(void))
						throw new Exception($"Cannot get result from void method {original.FullDescription()}");
					var resultType = patchParam.ParameterType;
					if (resultType.IsByRef)
						resultType = resultType.GetElementType();
					if (resultType.IsAssignableFrom(returnType) is false)
						throw new Exception(
							$"Cannot assign method return type {returnType.FullName} to {RESULT_VAR} type {resultType.FullName} for method {original.FullDescription()}");
					var ldlocCode = patchParam.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
					il.Emit(ldlocCode, variables[RESULT_VAR]);
					continue;
				}

				// any other declared variables
				if (variables.TryGetValue(patchParam.Name, out var localBuilder))
				{
					var ldlocCode = patchParam.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
					il.Emit(ldlocCode, localBuilder);
					continue;
				}

				int idx;
				if (patchParam.Name.StartsWith(PARAM_INDEX_PREFIX, StringComparison.Ordinal))
				{
					var val = patchParam.Name.Substring(PARAM_INDEX_PREFIX.Length);
					if (!int.TryParse(val, out idx))
						throw new Exception($"Parameter {patchParam.Name} does not contain a valid index");
					if (idx < 0 || idx >= originalParameters.Length)
						throw new Exception($"No parameter found at index {idx}");
				}
				else
				{
					idx = patch.GetArgumentIndex(originalParameterNames, patchParam);
					if (idx == -1)
					{
						var harmonyMethod = HarmonyMethodExtensions.GetMergedFromType(patchParam.ParameterType);
						if (harmonyMethod.methodType is null) // MethodType default is Normal
							harmonyMethod.methodType = MethodType.Normal;
						var delegateOriginal = harmonyMethod.GetOriginalMethod();
						if (delegateOriginal is MethodInfo methodInfo)
						{
							var delegateConstructor =
								patchParam.ParameterType.GetConstructor(new[] {typeof(object), typeof(IntPtr)});
							if (delegateConstructor is object)
							{
								var originalType = original.DeclaringType;
								if (methodInfo.IsStatic)
									il.Emit(OpCodes.Ldnull);
								else
								{
									il.Emit(OpCodes.Ldarg_0);
									if (originalType.IsValueType)
									{
										il.Emit(OpCodes.Ldobj, originalType);
										il.Emit(OpCodes.Box, originalType);
									}
								}

								if (methodInfo.IsStatic is false && harmonyMethod.nonVirtualDelegate is false)
								{
									il.Emit(OpCodes.Dup);
									il.Emit(OpCodes.Ldvirtftn, methodInfo);
								}
								else
									il.Emit(OpCodes.Ldftn, methodInfo);

								il.Emit(OpCodes.Newobj, delegateConstructor);
								continue;
							}
						}

						throw new Exception(
							$"Parameter \"{patchParam.Name}\" not found in method {original.FullDescription()}");
					}
				}

				//   original -> patch     opcode
				// --------------------------------------
				// 1 normal   -> normal  : LDARG
				// 2 normal   -> ref/out : LDARGA
				// 3 ref/out  -> normal  : LDARG, LDIND_x
				// 4 ref/out  -> ref/out : LDARG
				//
				var originalIsNormal = originalParameters[idx].IsOut is false &&
				                       originalParameters[idx].ParameterType.IsByRef is false;
				var patchIsNormal = patchParam.IsOut is false && patchParam.ParameterType.IsByRef is false;
				var patchArgIndex = idx + (isInstance ? 1 : 0);

				// Case 1 + 4
				if (originalIsNormal == patchIsNormal)
				{
					il.Emit(OpCodes.Ldarg, patchArgIndex);
					continue;
				}

				// Case 2
				if (originalIsNormal && patchIsNormal is false)
				{
					il.Emit(OpCodes.Ldarga, patchArgIndex);
					continue;
				}

				// Case 3
				il.Emit(OpCodes.Ldarg, patchArgIndex);
				il.Emit(GetIndOpcode(originalParameters[idx].ParameterType));
			}
		}
	}
}