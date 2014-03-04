﻿#region License
/*
Copyright (c) 2013-2014 Daniil Rodin of Buhgalteria.Kontur team of SKB Kontur

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
#endregion

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using SharpRpc.Codecs;
using SharpRpc.Reflection;
using System.Linq;

namespace SharpRpc.ClientSide
{
    public class ServiceProxyClassFactory : IServiceProxyClassFactory
    {
        private struct DescriptionCodecPair
        {
            public readonly MethodParameterDescription Description;
            public readonly IServiceProxyMethodParameterCodec Codec;

            public DescriptionCodecPair(MethodParameterDescription description, IServiceProxyMethodParameterCodec codec)
            {
                Description = description;
                Codec = codec;
            }
        }

        private readonly IServiceDescriptionBuilder serviceDescriptionBuilder;
        private readonly ICodecContainer codecContainer;
        private readonly IServiceProxyMethodIoCodecFactory ioCodecFactory;
        private readonly ModuleBuilder moduleBuilder;
        private int classNameDisambiguator = 0;

        public ServiceProxyClassFactory(IServiceDescriptionBuilder serviceDescriptionBuilder, ICodecContainer codecContainer, IServiceProxyMethodIoCodecFactory ioCodecFactory)
        {
            this.serviceDescriptionBuilder = serviceDescriptionBuilder;
            this.codecContainer = codecContainer;
            this.ioCodecFactory = ioCodecFactory;
            var appDomain = AppDomain.CurrentDomain;
            var assemblyBuilder = appDomain.DefineDynamicAssembly(new AssemblyName("SharpRpcServiceProxies"), AssemblyBuilderAccess.Run);
            moduleBuilder = assemblyBuilder.DefineDynamicModule("SharpRpcServiceProxyModule");
        }

        private static readonly Type[] ConstructorParameterTypes = { typeof(IOutgoingMethodCallProcessor), typeof(string), typeof(TimeoutSettings), typeof(ICodecContainer) };
        private static readonly Type[] FuncConstructorParameters = { typeof(object), typeof(IntPtr) };
        private static readonly Type[] DecodeDeferredParameterTypes = { typeof(Task<byte[]>) };
        private static readonly MethodInfo GetManualCodecForMethod = typeof(ICodecContainer).GetMethod("GetManualCodecFor");
        private static readonly MethodInfo GetTypeFromHandleMethod = typeof(Type).GetMethod("GetTypeFromHandle");
        private static readonly MethodInfo ProcessMethod = typeof(IOutgoingMethodCallProcessor).GetMethod("Process");
        private static readonly MethodInfo ProcessAsyncMethod = typeof(IOutgoingMethodCallProcessor).GetMethod("ProcessAsync");
        private static readonly MethodInfo ContinueWithMethod = typeof(Task<byte[]>).GetMethods().Where(IsCorrectContinueWith).Single();
        private static readonly MethodInfo GetResultMethod = typeof(Task<byte[]>).GetMethod("get_Result");

        private static bool IsCorrectContinueWith(MethodInfo methodInfo)
        {
            if (methodInfo.Name != "ContinueWith")
                return false;

            var genericArguments = methodInfo.GetGenericArguments();
            if (genericArguments.Length != 1)
                return false;

            var parameters = methodInfo.GetParameters();
            if (parameters.Length != 1)
                return false;

            var parameterType = parameters[0].ParameterType;
            var expectedParameterType = typeof(Func<,>).MakeGenericType(typeof(Task<byte[]>), genericArguments[0]);
            if (parameterType != expectedParameterType)
                return false;

            return true;
        }

        public Func<IOutgoingMethodCallProcessor, string, TimeoutSettings, T> CreateProxyClass<T>()
        {
            var proxyClass = CreateProxyClass(typeof(T), typeof(T), null);
            return (p, s, t) => (T) Activator.CreateInstance(proxyClass, p, s, t, codecContainer);
        }

        private Type CreateProxyClass(Type rootType, Type type, string path)
        {
            var serviceDescription = serviceDescriptionBuilder.Build(type);
            path = path ?? serviceDescription.Name;

            var typeBuilder = DeclareType(type);
            var classContext = new ServiceProxyClassBuildingContext(typeBuilder, rootType);
            foreach (var methodDesc in serviceDescription.Methods)
                CreateMethod(classContext, path, methodDesc);
            CreateConstructor(classContext, path, serviceDescription);
            return typeBuilder.CreateType();
        }

        private TypeBuilder DeclareType(Type serviceInterface)
        {
            int disambiguator = Interlocked.Increment(ref classNameDisambiguator);
            return moduleBuilder.DefineType("__rpc_proxy_" + serviceInterface.FullName + "_" + disambiguator,
                                            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                                            typeof(object), new[] { serviceInterface });
        }

        private void CreateMethod(IServiceProxyClassBuildingContext classContext, string path, MethodDescription methodDesc)
        {
            var methodBuilder = classContext.Builder.DefineMethod(methodDesc.Name,
                    MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig |
                    MethodAttributes.NewSlot | MethodAttributes.Virtual);

            var genericTypeParameterBuilders = methodDesc.GenericParameters.Any()
                ? methodBuilder.DefineGenericParameters(methodDesc.GenericParameters.Select(x => x.Name).ToArray())
                : new GenericTypeParameterBuilder[0];
            var genericTypeParameterCodecs = genericTypeParameterBuilders.Select(x => ioCodecFactory.CreateGenericTypeParameterCodec(x)).ToArray();
            var genericArgumentMap = genericTypeParameterBuilders.ToDictionary(x => x.Name, x => (Type)x);

            var parameters = methodDesc.Parameters.Select(x => x.DeepSubstituteGenerics(genericArgumentMap)).ToArray();
            var parameterTypesAdjustedForRefs = parameters.Select(x => x.Way == MethodParameterWay.Val ? x.Type : x.Type.MakeByRefType()).ToArray();
            var allParameterCodecs = parameters.Select(x => ioCodecFactory.CreateParameterCodec(x)).ToArray();

            var retvalType = methodDesc.ReturnType.DeepSubstituteGenerics(genericArgumentMap);
            
            methodBuilder.SetParameters(parameterTypesAdjustedForRefs);
            methodBuilder.SetReturnType(retvalType);
            methodBuilder.SetImplementationFlags(MethodImplAttributes.Managed);

            var il = methodBuilder.GetILGenerator();
            var emittingContext = new EmittingContext(il);

            var requestDataArrayVar = il.DeclareLocal(typeof(byte[]));              // byte[] dataArray

            var requestParameterCodecs = allParameterCodecs.Zip(parameters, (c, d) => new DescriptionCodecPair(d, c))
                .Where(x => x.Description.Way == MethodParameterWay.Val || x.Description.Way == MethodParameterWay.Ref)
                .Select(x => x.Codec)
                .ToArray();

            if (requestParameterCodecs.Any() || genericTypeParameterCodecs.Any())
            {
                bool haveSizeOnStack = false;
                foreach (var codec in genericTypeParameterCodecs)
                {
                    codec.EmitCalculateSize(classContext, emittingContext);         // stack_0 += CalculateSize(T_i)
                    EmitAddIf(il, ref haveSizeOnStack);
                }
                foreach (var codec in requestParameterCodecs)
                {
                    codec.EmitCalculateSize(classContext, emittingContext);         // stack_0 += CalculateSize(arg_i)
                    EmitAddIf(il, ref haveSizeOnStack);
                }

                il.Emit(OpCodes.Newarr, typeof(byte));                              // dataArray = new byte[stack_0]
                il.Emit(OpCodes.Stloc, requestDataArrayVar);
                var pinnedVar = il.Emit_PinArray(typeof(byte), requestDataArrayVar);// var pinned dataPointer = pin(dataArray)
                il.Emit(OpCodes.Ldloc, pinnedVar);                                  // data = dataPointer
                il.Emit(OpCodes.Stloc, emittingContext.DataPointerVar);

                foreach (var codec in genericTypeParameterCodecs)
                    codec.EmitEncode(classContext, emittingContext);                // Encode(T_i, data)
                foreach (var codec in requestParameterCodecs)
                    codec.EmitEncode(classContext, emittingContext);                // Encode(arg_i, data)
            }
            else
            {
                il.Emit(OpCodes.Ldnull);                                            // dataArray = null
                il.Emit(OpCodes.Stloc, requestDataArrayVar);
            }

            il.Emit_Ldarg(0);                                                       // stack_0 = methodCallProcessor
            il.Emit(OpCodes.Ldfld, classContext.ProcessorField);
            il.Emit(OpCodes.Ldtoken, classContext.InterfaceType);                   // stack_1 = typeof(T)
            il.Emit(OpCodes.Call, GetTypeFromHandleMethod);
            il.Emit(OpCodes.Ldstr, string.Format("{0}/{1}", path, methodDesc.Name));// stack_2 = SuperServicePath/ServiceName/MethodName
            il.Emit_Ldarg(0);                                                       // stack_3 = scope
            il.Emit(OpCodes.Ldfld, classContext.ScopeField);
            il.Emit(OpCodes.Ldloc, requestDataArrayVar);                            // stack_4 = dataArray
            il.Emit_Ldarg(0);                                                       // stack_5 = timeoutSettings
            il.Emit(OpCodes.Ldfld, classContext.TimeoutSettingsField);

            var responseParameterCodecs = allParameterCodecs.Zip(parameters, (c, d) => new DescriptionCodecPair(d, c))
                .Where(x => x.Description.Way == MethodParameterWay.Ref || x.Description.Way == MethodParameterWay.Out)
                .Select(x => x.Codec)
                .ToArray();

            if (responseParameterCodecs.Any() && methodDesc.RemotingType != MethodRemotingType.Direct)
                throw new ArgumentException(string.Format("Error processing {0} method: only direct methods can have Ref or Out parameters", path));

            switch (methodDesc.RemotingType)
            {
                case MethodRemotingType.Direct:
                    EmitProcessDirect(classContext, emittingContext, responseParameterCodecs, retvalType);
                    break;
                case MethodRemotingType.AsyncVoid:
                    EmitProcessAsyncVoid(il);
                    break;
                case MethodRemotingType.AsyncWithRetval:
                    EmitProcessAsyncWithRetval(classContext, emittingContext, methodDesc.Name, genericTypeParameterBuilders, retvalType);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            il.Emit(OpCodes.Ret);
        }

        private static void EmitAddIf(ILGenerator il, ref bool haveSizeOnStack)
        {
            if (haveSizeOnStack)
                il.Emit(OpCodes.Add);
            else
                haveSizeOnStack = true;
        }

        private void EmitProcessDirect(IServiceProxyClassBuildingContext classContext, IEmittingContext emittingContext, IServiceProxyMethodParameterCodec[] responseParameterCodecs, Type retvalType)
        {
            bool hasRetval = retvalType != typeof(void);
            var il = emittingContext.IL;
            il.Emit(OpCodes.Callvirt, ProcessMethod);                               // stack_0 = stack_0.Process(stack_1, stack_2, stack_3, stack_4, stack_5)

            if (responseParameterCodecs.Any() || hasRetval)
            {
                EmitPrepareToDecode(emittingContext);

                foreach (var codec in responseParameterCodecs)
                    codec.EmitDecodeAndStore(classContext, emittingContext);        // arg_i+1 = Decode(data, remainingBytes, false)

                if (hasRetval)
                {
                    var retvalCodec = ioCodecFactory.CreateRetvalCodec(retvalType);
                    retvalCodec.EmitDecode(classContext, emittingContext);          // stack_0 = Decode(data, remainingBytes, false)
                }
            }
            else
            {
                il.Emit(OpCodes.Pop);                                               // pop(stack_0)
            }
        }

        private static void EmitProcessAsyncVoid(ILGenerator il)
        {
            il.Emit(OpCodes.Callvirt, ProcessAsyncMethod); // stack_0 = stack_0.ProcessAsync(stack_1, stack_2, stack_3, stack_4, stack_5)
        }

        private void EmitProcessAsyncWithRetval(IServiceProxyClassBuildingContext classContext, IEmittingContext emittingContext, string parameterMethodName, GenericTypeParameterBuilder[] genericTypeArguments, Type retvalType)
        {
            var pureRetvalType = retvalType.GetGenericArguments().Single();
            var decodeDeferredMethod = CreateDecodeDeferredMethod(classContext, parameterMethodName, genericTypeArguments, pureRetvalType);
            if (decodeDeferredMethod.IsGenericMethodDefinition)
                decodeDeferredMethod = decodeDeferredMethod.MakeGenericMethod(genericTypeArguments);
            var funcType = typeof(Func<,>).MakeGenericType(new[] { typeof(Task<byte[]>), pureRetvalType });
            var funcConstructor = pureRetvalType.ContainsGenericParameters 
                ? TypeBuilder.GetConstructor(funcType, typeof(Func<,>).GetConstructor(FuncConstructorParameters))
                : funcType.GetConstructor(FuncConstructorParameters);
            var continueWithMethod = ContinueWithMethod.MakeGenericMethod(new[] { pureRetvalType });

            var il = emittingContext.IL;
            il.Emit(OpCodes.Callvirt, ProcessAsyncMethod);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldftn, decodeDeferredMethod);
            il.Emit(OpCodes.Newobj, funcConstructor);
            il.Emit(OpCodes.Callvirt, continueWithMethod);
        }

        private MethodInfo CreateDecodeDeferredMethod(IServiceProxyClassBuildingContext classContext, string parentMethodName, GenericTypeParameterBuilder[] genericTypeArguments, Type pureRetvalType)
        {
            var methodBuilder = classContext.Builder.DefineMethod("__rpc_decode_deferred_" + parentMethodName,
                    MethodAttributes.Private | MethodAttributes.HideBySig);

            var genericTypeParameterBuilders = genericTypeArguments.Any()
                ? methodBuilder.DefineGenericParameters(genericTypeArguments.Select(x => x.Name).ToArray())
                : new GenericTypeParameterBuilder[0];
            var genericArgumentMap = genericTypeParameterBuilders.ToDictionary(x => x.Name, x => (Type)x);

            var retvalType = pureRetvalType.DeepSubstituteGenerics(genericArgumentMap);

            methodBuilder.SetParameters(DecodeDeferredParameterTypes);
            methodBuilder.SetReturnType(retvalType);
            methodBuilder.SetImplementationFlags(MethodImplAttributes.Managed);

            var il = methodBuilder.GetILGenerator();
            var emittingContext = new EmittingContext(il);

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, GetResultMethod);
            EmitPrepareToDecode(emittingContext);

            var retvalCodec = ioCodecFactory.CreateRetvalCodec(retvalType);
            retvalCodec.EmitDecode(classContext, emittingContext);
            il.Emit(OpCodes.Ret);

            return methodBuilder;
        }

        private static void EmitPrepareToDecode(IEmittingContext emittingContext)
        {
            var il = emittingContext.IL;
            var responseDataArrayVar = il.DeclareLocal(typeof(byte[]));
            il.Emit(OpCodes.Stloc, responseDataArrayVar);                       // dataArray = stack_0
            il.Emit(OpCodes.Ldloc, responseDataArrayVar);                       // remainingBytes = dataArray.Length
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Stloc, emittingContext.RemainingBytesVar);
            var pinnedVar = il.Emit_PinArray(typeof(byte), responseDataArrayVar);// var pinned dataPointer = pin(dataArray)
            il.Emit(OpCodes.Ldloc, pinnedVar);                                  // data = dataPointer
            il.Emit(OpCodes.Stloc, emittingContext.DataPointerVar);
        }

        private void CreateConstructor(IServiceProxyClassBuildingContext classContext, string path, ServiceDescription serviceDescription)
        {
            var constructorBuilder = classContext.Builder.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard, ConstructorParameterTypes);
            var baseConstructor = typeof(object).GetConstructor(Type.EmptyTypes);
            var il = constructorBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseConstructor);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, classContext.ProcessorField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Stfld, classContext.ScopeField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Stfld, classContext.TimeoutSettingsField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit_Ldarg(4);
            il.Emit(OpCodes.Stfld, classContext.CodecContainerField);

            var manualCodecTypes = classContext.GetAllManualCodecTypes();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit_Ldc_I4(manualCodecTypes.Length);
            il.Emit(OpCodes.Newarr, typeof(IManualCodec));
            il.Emit(OpCodes.Stfld, classContext.ManualCodecsField);
            for (int i = 0; i < manualCodecTypes.Length; i++)
            {
                var codecType = manualCodecTypes[i];
                il.Emit_Ldarg(0);
                il.Emit(OpCodes.Ldfld, classContext.ManualCodecsField);
                il.Emit_Ldc_I4(i);
                il.Emit_Ldarg(4);
                il.Emit(OpCodes.Call, GetManualCodecForMethod.MakeGenericMethod(codecType));
                il.Emit(OpCodes.Stelem_Ref);
            }

            foreach (var subserviceDesc in serviceDescription.Subservices)
            {
                var proxyClass = CreateProxyClass(classContext.InterfaceType, subserviceDesc.Type, path + "/" + subserviceDesc.Name);
                var fieldBuilder = CreateSubserviceField(classContext, subserviceDesc, proxyClass);
                CreateSubserviceProperty(classContext, subserviceDesc, fieldBuilder);
                
                il.Emit_Ldarg(0);
                il.Emit_Ldarg(1);
                il.Emit_Ldarg(2);
                il.Emit_Ldarg(3);
                il.Emit_Ldarg(4);
                il.Emit(OpCodes.Newobj, proxyClass.GetConstructor(ConstructorParameterTypes));
                il.Emit(OpCodes.Stfld, fieldBuilder);
            }

            il.Emit(OpCodes.Ret);
        }

        private static FieldBuilder CreateSubserviceField(IServiceProxyClassBuildingContext classContext, ServiceDescription subserviceDescription, Type proxyClass)
        {
            return classContext.Builder.DefineField("_" + subserviceDescription.Name, proxyClass,
                    FieldAttributes.Private | FieldAttributes.InitOnly);
        }

        private static void CreateSubserviceProperty(IServiceProxyClassBuildingContext classContext, ServiceDescription subserviceDescription, FieldBuilder fieldBuilder)
        {
            var methodBuilder = classContext.Builder.DefineMethod("get_" + subserviceDescription.Name,
                    MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.HideBySig |
                    MethodAttributes.SpecialName | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                    subserviceDescription.Type, Type.EmptyTypes);
            methodBuilder.SetImplementationFlags(MethodImplAttributes.Managed);
            var il = methodBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fieldBuilder);
            il.Emit(OpCodes.Ret);

            var propertyBuilder = classContext.Builder.DefineProperty(subserviceDescription.Name,
                PropertyAttributes.None, subserviceDescription.Type, Type.EmptyTypes);
            propertyBuilder.SetGetMethod(methodBuilder);
        }
    }
}
