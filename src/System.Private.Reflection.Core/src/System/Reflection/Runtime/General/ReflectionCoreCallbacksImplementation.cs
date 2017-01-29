// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Reflection;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.Reflection.Runtime.General;
using System.Reflection.Runtime.TypeInfos;
using System.Reflection.Runtime.TypeInfos.NativeFormat;
using System.Reflection.Runtime.Assemblies;
using System.Reflection.Runtime.FieldInfos.NativeFormat;
using System.Reflection.Runtime.MethodInfos;
using System.Reflection.Runtime.BindingFlagSupport;

using Internal.Reflection.Augments;
using Internal.Reflection.Core.Execution;
using Internal.Metadata.NativeFormat;

namespace System.Reflection.Runtime.General
{
    internal sealed class ReflectionCoreCallbacksImplementation : ReflectionCoreCallbacks
    {
        internal ReflectionCoreCallbacksImplementation()
        {
        }

        public sealed override Assembly Load(AssemblyName refName)
        {
            if (refName == null)
                throw new ArgumentNullException("assemblyRef");
            return RuntimeAssembly.GetRuntimeAssembly(refName.ToRuntimeAssemblyName());
        }

        public sealed override Assembly Load(byte[] rawAssembly, byte[] pdbSymbolStore)
        {
            if (rawAssembly == null)
                throw new ArgumentNullException(nameof(rawAssembly));

            return RuntimeAssembly.GetRuntimeAssemblyFromByteArray(rawAssembly, pdbSymbolStore);
        }

        //
        // This overload of GetMethodForHandle only accepts handles for methods declared on non-generic types (the method, however,
        // can be an instance of a generic method.) To resolve handles for methods declared on generic types, you must pass
        // the declaring type explicitly using the two-argument overload of GetMethodFromHandle.
        //
        // This is a vestige from desktop generic sharing that got itself enshrined in the code generated by the C# compiler for Linq Expressions.
        //
        public sealed override MethodBase GetMethodFromHandle(RuntimeMethodHandle runtimeMethodHandle)
        {
            ExecutionEnvironment executionEnvironment = ReflectionCoreExecution.ExecutionEnvironment;
            QMethodDefinition methodHandle;
            RuntimeTypeHandle declaringTypeHandle;
            RuntimeTypeHandle[] genericMethodTypeArgumentHandles;
            if (!executionEnvironment.TryGetMethodFromHandle(runtimeMethodHandle, out declaringTypeHandle, out methodHandle, out genericMethodTypeArgumentHandles))
                throw new ArgumentException(SR.Argument_InvalidHandle);

            MethodBase methodBase = ReflectionCoreExecution.ExecutionDomain.GetMethod(declaringTypeHandle, methodHandle, genericMethodTypeArgumentHandles);
            if (methodBase.DeclaringType.IsConstructedGenericType)  // For compat with desktop, insist that the caller pass us the declaring type to resolve members of generic types.
                throw new ArgumentException(SR.Format(SR.Argument_MethodDeclaringTypeGeneric, methodBase));
            return methodBase;
        }

        //
        // This overload of GetMethodHandle can handle all method handles.
        //
        public sealed override MethodBase GetMethodFromHandle(RuntimeMethodHandle runtimeMethodHandle, RuntimeTypeHandle declaringTypeHandle)
        {
            ExecutionEnvironment executionEnvironment = ReflectionCoreExecution.ExecutionEnvironment;
            QMethodDefinition methodHandle;
            RuntimeTypeHandle[] genericMethodTypeArgumentHandles;
            if (!executionEnvironment.TryGetMethodFromHandleAndType(runtimeMethodHandle, declaringTypeHandle, out methodHandle, out genericMethodTypeArgumentHandles))
            {
                // This may be a method declared on a non-generic type: this api accepts that too so try the other table.
                RuntimeTypeHandle actualDeclaringTypeHandle;
                if (!executionEnvironment.TryGetMethodFromHandle(runtimeMethodHandle, out actualDeclaringTypeHandle, out methodHandle, out genericMethodTypeArgumentHandles))
                    throw new ArgumentException(SR.Argument_InvalidHandle);
                if (!actualDeclaringTypeHandle.Equals(declaringTypeHandle))
                    throw new ArgumentException(SR.Format(SR.Argument_ResolveMethodHandle,
                        declaringTypeHandle.GetTypeForRuntimeTypeHandle(),
                        actualDeclaringTypeHandle.GetTypeForRuntimeTypeHandle()));
            }

            MethodBase methodBase = ReflectionCoreExecution.ExecutionDomain.GetMethod(declaringTypeHandle, methodHandle, genericMethodTypeArgumentHandles);
            return methodBase;
        }

        //
        // This overload of GetFieldForHandle only accepts handles for fields declared on non-generic types. To resolve handles for fields
        // declared on generic types, you must pass the declaring type explicitly using the two-argument overload of GetFieldFromHandle.
        //
        // This is a vestige from desktop generic sharing that got itself enshrined in the code generated by the C# compiler for Linq Expressions.
        //
        public sealed override FieldInfo GetFieldFromHandle(RuntimeFieldHandle runtimeFieldHandle)
        {
            ExecutionEnvironment executionEnvironment = ReflectionCoreExecution.ExecutionEnvironment;
            FieldHandle fieldHandle;
            RuntimeTypeHandle declaringTypeHandle;
            if (!executionEnvironment.TryGetFieldFromHandle(runtimeFieldHandle, out declaringTypeHandle, out fieldHandle))
                throw new ArgumentException(SR.Argument_InvalidHandle);

            FieldInfo fieldInfo = GetFieldInfo(declaringTypeHandle, fieldHandle);
            if (fieldInfo.DeclaringType.IsConstructedGenericType) // For compat with desktop, insist that the caller pass us the declaring type to resolve members of generic types.
                throw new ArgumentException(SR.Format(SR.Argument_FieldDeclaringTypeGeneric, fieldInfo));
            return fieldInfo;
        }

        //
        // This overload of GetFieldHandle can handle all field handles.
        //
        public sealed override FieldInfo GetFieldFromHandle(RuntimeFieldHandle runtimeFieldHandle, RuntimeTypeHandle declaringTypeHandle)
        {
            ExecutionEnvironment executionEnvironment = ReflectionCoreExecution.ExecutionEnvironment;
            FieldHandle fieldHandle;
            if (!executionEnvironment.TryGetFieldFromHandleAndType(runtimeFieldHandle, declaringTypeHandle, out fieldHandle))
            {
                // This may be a field declared on a non-generic type: this api accepts that too so try the other table.
                RuntimeTypeHandle actualDeclaringTypeHandle;
                if (!executionEnvironment.TryGetFieldFromHandle(runtimeFieldHandle, out actualDeclaringTypeHandle, out fieldHandle))
                    throw new ArgumentException(SR.Argument_InvalidHandle);
                if (!actualDeclaringTypeHandle.Equals(declaringTypeHandle))
                    throw new ArgumentException(SR.Format(SR.Argument_ResolveFieldHandle,
                        declaringTypeHandle.GetTypeForRuntimeTypeHandle(),
                        actualDeclaringTypeHandle.GetTypeForRuntimeTypeHandle()));
            }

            FieldInfo fieldInfo = GetFieldInfo(declaringTypeHandle, fieldHandle);
            return fieldInfo;
        }

        public sealed override EventInfo GetImplicitlyOverriddenBaseClassEvent(EventInfo e)
        {
            return e.GetImplicitlyOverriddenBaseClassMember();
        }

        public sealed override MethodInfo GetImplicitlyOverriddenBaseClassMethod(MethodInfo m)
        {
            return m.GetImplicitlyOverriddenBaseClassMember();
        }

        public sealed override PropertyInfo GetImplicitlyOverriddenBaseClassProperty(PropertyInfo p)
        {
            return p.GetImplicitlyOverriddenBaseClassMember();
        }

        public sealed override Binder CreateDefaultBinder()
        {
            return new DefaultBinder();
        }

        private FieldInfo GetFieldInfo(RuntimeTypeHandle declaringTypeHandle, FieldHandle fieldHandle)
        {
            RuntimeTypeInfo contextTypeInfo = declaringTypeHandle.GetTypeForRuntimeTypeHandle();
            NativeFormatRuntimeNamedTypeInfo definingTypeInfo = contextTypeInfo.AnchoringTypeDefinitionForDeclaredMembers.CastToNativeFormatRuntimeNamedTypeInfo();
            MetadataReader reader = definingTypeInfo.Reader;

            // RuntimeFieldHandles always yield FieldInfo's whose ReflectedType equals the DeclaringType.
            RuntimeTypeInfo reflectedType = contextTypeInfo;
            return NativeFormatRuntimeFieldInfo.GetRuntimeFieldInfo(fieldHandle, definingTypeInfo, contextTypeInfo, reflectedType);
        }

        public sealed override object ActivatorCreateInstance(Type type, bool nonPublic)
        {
            return ActivatorImplementation.CreateInstance(type, nonPublic);
        }

        public sealed override object ActivatorCreateInstance(Type type, BindingFlags bindingAttr, Binder binder, object[] args, CultureInfo culture, object[] activationAttributes)
        {
            return ActivatorImplementation.CreateInstance(type, bindingAttr, binder, args, culture, activationAttributes);
        }
    }
}
