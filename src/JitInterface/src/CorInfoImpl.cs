// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

#if SUPPORT_JIT
using Internal.Runtime.CompilerServices;
#endif

using Internal.IL;
using Internal.TypeSystem;
using Internal.TypeSystem.Ecma;

using ILCompiler;
using ILCompiler.DependencyAnalysis;

#if READYTORUN
using System.Reflection.Metadata.Ecma335;
using ILCompiler.DependencyAnalysis.ReadyToRun;
#endif

namespace Internal.JitInterface
{
    internal unsafe sealed partial class CorInfoImpl
    {
        //
        // Global initialization and state
        //
        private enum ImageFileMachine
        {
            I386 = 0x014c,
            IA64 = 0x0200,
            AMD64 = 0x8664,
            ARM = 0x01c4,
        }

#if SUPPORT_JIT
        private const string JitSupportLibrary = "*";
#else
        private const string JitSupportLibrary = "jitinterface";
#endif

        private IntPtr _jit;

        private IntPtr _unmanagedCallbacks; // array of pointers to JIT-EE interface callbacks
        private Object _keepAlive; // Keeps delegates for the callbacks alive

        private ExceptionDispatchInfo _lastException;

        [DllImport("clrjitilc", CallingConvention=CallingConvention.StdCall)] // stdcall in CoreCLR!
        private extern static IntPtr jitStartup(IntPtr host);

        [DllImport("clrjitilc", CallingConvention=CallingConvention.StdCall)]
        private extern static IntPtr getJit();

        [DllImport(JitSupportLibrary)]
        private extern static IntPtr GetJitHost(IntPtr configProvider);

        //
        // Per-method initialization and state
        //
        private static CorInfoImpl GetThis(IntPtr thisHandle)
        {
            CorInfoImpl _this = Unsafe.Read<CorInfoImpl>((void*)thisHandle);
            Debug.Assert(_this is CorInfoImpl);
            return _this;
        }

        [DllImport(JitSupportLibrary)]
        private extern static CorJitResult JitCompileMethod(out IntPtr exception, 
            IntPtr jit, IntPtr thisHandle, IntPtr callbacks,
            ref CORINFO_METHOD_INFO info, uint flags, out IntPtr nativeEntry, out uint codeSize);

        [DllImport(JitSupportLibrary)]
        private extern static uint GetMaxIntrinsicSIMDVectorLength(IntPtr jit, CORJIT_FLAGS* flags);

        [DllImport(JitSupportLibrary)]
        private extern static IntPtr AllocException([MarshalAs(UnmanagedType.LPWStr)]string message, int messageLength);

        private IntPtr AllocException(Exception ex)
        {
            _lastException = ExceptionDispatchInfo.Capture(ex);

            string exString = ex.ToString();
            IntPtr nativeException = AllocException(exString, exString.Length);
            if (_nativeExceptions == null)
            {
                _nativeExceptions = new List<IntPtr>();
            }
            _nativeExceptions.Add(nativeException);
            return nativeException;
        }

        [DllImport(JitSupportLibrary)]
        private extern static void FreeException(IntPtr obj);

        [DllImport(JitSupportLibrary)]
        private extern static char* GetExceptionMessage(IntPtr obj);

        private JitConfigProvider _jitConfig;

        private readonly UnboxingMethodDescFactory _unboxingThunkFactory;

        public CorInfoImpl(JitConfigProvider jitConfig)
        {
            //
            // Global initialization
            //
            _jitConfig = jitConfig;

            jitStartup(GetJitHost(_jitConfig.UnmanagedInstance));

            _jit = getJit();
            if (_jit == IntPtr.Zero)
            {
                throw new IOException("Failed to initialize JIT");
            }

            _unmanagedCallbacks = GetUnmanagedCallbacks(out _keepAlive);

            _unboxingThunkFactory = new UnboxingMethodDescFactory();
        }

        public TextWriter Log
        {
            get
            {
                return _compilation.Logger.Writer;
            }
        }

        private CORINFO_MODULE_STRUCT_* _methodScope; // Needed to resolve CORINFO_EH_CLAUSE tokens

        private bool _isFallbackBodyCompilation; // True if we're compiling a fallback method body after compiling the real body failed

        private void CompileMethodInternal(IMethodNode methodCodeNodeNeedingCode, MethodIL methodIL = null)
        {
#if READYTORUN
            bool codeGotPublished = false;
#endif
            try
            {
                _isFallbackBodyCompilation = methodIL != null;

                CORINFO_METHOD_INFO methodInfo;
                methodIL = Get_CORINFO_METHOD_INFO(MethodBeingCompiled, methodIL, &methodInfo);

                // This is e.g. an "extern" method in C# without a DllImport or InternalCall.
                if (methodIL == null)
                {
                    ThrowHelper.ThrowInvalidProgramException(ExceptionStringID.InvalidProgramSpecific, MethodBeingCompiled);
                }

                _methodScope = methodInfo.scope;

#if !READYTORUN
                SetDebugInformation(methodCodeNodeNeedingCode, methodIL);
#endif

                CorInfoImpl _this = this;

                IntPtr exception;
                IntPtr nativeEntry;
                uint codeSize;
                var result = JitCompileMethod(out exception,
                        _jit, (IntPtr)Unsafe.AsPointer(ref _this), _unmanagedCallbacks,
                        ref methodInfo, (uint)CorJitFlag.CORJIT_FLAG_CALL_GETJITFLAGS, out nativeEntry, out codeSize);
                if (exception != IntPtr.Zero)
                {
                    if (_lastException != null)
                    {
                        // If we captured a managed exception, rethrow that.
                        // TODO: might not actually be the real reason. It could be e.g. a JIT failure/bad IL that followed
                        // an inlining attempt with a type system problem in it...
#if SUPPORT_JIT
                        _lastException.Throw();
#else
                        if (_lastException.SourceException is TypeSystemException)
                        {
                            // Type system exceptions can be turned into code that throws the exception at runtime.
                            _lastException.Throw();
                        }
#if READYTORUN
                        else if (_lastException.SourceException is RequiresRuntimeJitException)
                        {
                            // Runtime JIT requirement is not a cause for failure, we just mustn't JIT a particular method
                            _lastException.Throw();
                        }
#endif
                        else
                        {
                            // This is just a bug somewhere.
                            throw new CodeGenerationFailedException(_methodCodeNode.Method, _lastException.SourceException);
                        }
#endif
                    }

                    // This is a failure we don't know much about.
                    char* szMessage = GetExceptionMessage(exception);
                    string message = szMessage != null ? new string(szMessage) : "JIT Exception";
                    throw new Exception(message);
                }
                if (result == CorJitResult.CORJIT_BADCODE)
                {
                    ThrowHelper.ThrowInvalidProgramException();
                }
                if (result != CorJitResult.CORJIT_OK)
                {
#if SUPPORT_JIT
                    // FailFast?
                    throw new Exception("JIT failed");
#else
                    throw new CodeGenerationFailedException(_methodCodeNode.Method);
#endif
                }

                PublishCode();
#if READYTORUN
                codeGotPublished = true;
#endif
            }
            finally
            {
#if READYTORUN
                if (!codeGotPublished)
                {
                    PublishEmptyCode();
                }
#endif
                CompileMethodCleanup();
            }
        }

        private void PublishCode()
        {
            var relocs = _relocs.ToArray();
            Array.Sort(relocs, (x, y) => (x.Offset - y.Offset));

            int alignment = _jitConfig.HasFlag(CorJitFlag.CORJIT_FLAG_SIZE_OPT) ?
                _compilation.NodeFactory.Target.MinimumFunctionAlignment :
                _compilation.NodeFactory.Target.OptimumFunctionAlignment;

            var objectData = new ObjectNode.ObjectData(_code,
                                                       relocs,
                                                       alignment,
                                                       new ISymbolDefinitionNode[] { _methodCodeNode });
            ObjectNode.ObjectData ehInfo = _ehClauses != null ? EncodeEHInfo() : null;
            DebugEHClauseInfo[] debugEHClauseInfos = null;
            if (_ehClauses != null)
            {
                debugEHClauseInfos = new DebugEHClauseInfo[_ehClauses.Length];
                for (int i = 0; i < _ehClauses.Length; i++)
                {
                    var clause = _ehClauses[i];
                    debugEHClauseInfos[i] = new DebugEHClauseInfo(clause.TryOffset, clause.TryLength,
                                                        clause.HandlerOffset, clause.HandlerLength);
                }
            }

            _methodCodeNode.SetCode(objectData
#if !SUPPORT_JIT && !READYTORUN
                , isFoldable: (_compilation._compilationOptions & RyuJitCompilationOptions.MethodBodyFolding) != 0
#endif
                );

            _methodCodeNode.InitializeFrameInfos(_frameInfos);
            _methodCodeNode.InitializeDebugEHClauseInfos(debugEHClauseInfos);
            _methodCodeNode.InitializeGCInfo(_gcInfo);
            _methodCodeNode.InitializeEHInfo(ehInfo);

            _methodCodeNode.InitializeDebugLocInfos(_debugLocInfos);
            _methodCodeNode.InitializeDebugVarInfos(_debugVarInfos);
            PublishProfileData();
        }

        partial void PublishProfileData();

        private MethodDesc MethodBeingCompiled
        {
            get
            {
                return _methodCodeNode.Method;
            }
        }

        private int PointerSize
        {
            get
            {
                return _compilation.TypeSystemContext.Target.PointerSize;
            }
        }

        private Dictionary<Object, GCHandle> _pins = new Dictionary<object, GCHandle>();

        private IntPtr GetPin(Object obj)
        {
            GCHandle handle;
            if (!_pins.TryGetValue(obj, out handle))
            {
                handle = GCHandle.Alloc(obj, GCHandleType.Pinned);
                _pins.Add(obj, handle);
            }
            return handle.AddrOfPinnedObject();
        }

        private List<IntPtr> _nativeExceptions;

        private void CompileMethodCleanup()
        {
            foreach (var pin in _pins)
                pin.Value.Free();
            _pins.Clear();

            if (_nativeExceptions != null)
            {
                foreach (IntPtr ex in _nativeExceptions)
                    FreeException(ex);
                _nativeExceptions = null;
            }

            _methodCodeNode = null;

            _code = null;
            _coldCode = null;

            _roData = null;
            _roDataBlob = null;

            _relocs = new ArrayBuilder<Relocation>();

            _numFrameInfos = 0;
            _usedFrameInfos = 0;
            _frameInfos = null;

            _gcInfo = null;
            _ehClauses = null;

#if !READYTORUN
            _sequencePoints = null;
            _variableToTypeDesc = null;
#endif
            _debugLocInfos = null;
            _debugVarInfos = null;
            _lastException = null;

#if READYTORUN
            _profileDataNode = null;
#endif
        }

        private Dictionary<Object, IntPtr> _objectToHandle = new Dictionary<Object, IntPtr>();
        private List<Object> _handleToObject = new List<Object>();

        private const int handleMultipler = 8;
        private const int handleBase = 0x420000;

        private IntPtr ObjectToHandle(Object obj)
        {
            IntPtr handle;
            if (!_objectToHandle.TryGetValue(obj, out handle))
            {
                handle = (IntPtr)(handleMultipler * _handleToObject.Count + handleBase);
                _handleToObject.Add(obj);
                _objectToHandle.Add(obj, handle);
            }
            return handle;
        }

        private Object HandleToObject(IntPtr handle)
        {
            int index = ((int)handle - handleBase) / handleMultipler;
            return _handleToObject[index];
        }

        private MethodDesc HandleToObject(CORINFO_METHOD_STRUCT_* method) { return (MethodDesc)HandleToObject((IntPtr)method); }
        private CORINFO_METHOD_STRUCT_* ObjectToHandle(MethodDesc method) { return (CORINFO_METHOD_STRUCT_*)ObjectToHandle((Object)method); }

        private TypeDesc HandleToObject(CORINFO_CLASS_STRUCT_* type) { return (TypeDesc)HandleToObject((IntPtr)type); }
        private CORINFO_CLASS_STRUCT_* ObjectToHandle(TypeDesc type) { return (CORINFO_CLASS_STRUCT_*)ObjectToHandle((Object)type); }

        private FieldDesc HandleToObject(CORINFO_FIELD_STRUCT_* field) { return (FieldDesc)HandleToObject((IntPtr)field); }
        private CORINFO_FIELD_STRUCT_* ObjectToHandle(FieldDesc field) { return (CORINFO_FIELD_STRUCT_*)ObjectToHandle((Object)field); }

        private MethodIL Get_CORINFO_METHOD_INFO(MethodDesc method, MethodIL methodIL, CORINFO_METHOD_INFO* methodInfo)
        {
            // MethodIL can be provided externally for the case of a method whose IL was replaced because we couldn't compile it.
            if (methodIL == null)
                methodIL = _compilation.GetMethodIL(method);

            if (methodIL == null)
            {
                *methodInfo = default(CORINFO_METHOD_INFO);
                return null;
            }

            methodInfo->ftn = ObjectToHandle(method);
            methodInfo->scope = (CORINFO_MODULE_STRUCT_*)ObjectToHandle(methodIL);
            var ilCode = methodIL.GetILBytes();
            methodInfo->ILCode = (byte*)GetPin(ilCode);
            methodInfo->ILCodeSize = (uint)ilCode.Length;
            methodInfo->maxStack = (uint)methodIL.MaxStack;
            methodInfo->EHcount = (uint)methodIL.GetExceptionRegions().Length;
            methodInfo->options = methodIL.IsInitLocals ? CorInfoOptions.CORINFO_OPT_INIT_LOCALS : (CorInfoOptions)0;

            if (method.AcquiresInstMethodTableFromThis())
            {
                methodInfo->options |= CorInfoOptions.CORINFO_GENERICS_CTXT_FROM_THIS;
            }
            else if (method.RequiresInstMethodDescArg())
            {
                methodInfo->options |= CorInfoOptions.CORINFO_GENERICS_CTXT_FROM_METHODDESC;
            }
            else if (method.RequiresInstMethodTableArg())
            {
                methodInfo->options |= CorInfoOptions.CORINFO_GENERICS_CTXT_FROM_METHODTABLE;
            }

            methodInfo->regionKind = CorInfoRegionKind.CORINFO_REGION_NONE;
            Get_CORINFO_SIG_INFO(method, &methodInfo->args);
            Get_CORINFO_SIG_INFO(methodIL.GetLocals(), &methodInfo->locals);

            return methodIL;
        }

        private void Get_CORINFO_SIG_INFO(MethodDesc method, CORINFO_SIG_INFO* sig, bool suppressHiddenArgument = false)
        {
            Get_CORINFO_SIG_INFO(method.Signature, sig);

            // Does the method have a hidden parameter?
            bool hasHiddenParameter = !suppressHiddenArgument && method.RequiresInstArg();

            if (method.IsIntrinsic)
            {
                // Some intrinsics will beg to differ about the hasHiddenParameter decision
#if !READYTORUN
                if (_compilation.TypeSystemContext.IsSpecialUnboxingThunkTargetMethod(method))
                    hasHiddenParameter = false;
#endif

                if (method.IsArrayAddressMethod())
                    hasHiddenParameter = true;
                
                // We only populate sigInst for intrinsic methods because most of the time,
                // JIT doesn't care what the instantiation is and this is expensive.
                Instantiation owningTypeInst = method.OwningType.Instantiation;
                sig->sigInst.classInstCount = (uint)owningTypeInst.Length;
                if (owningTypeInst.Length > 0)
                {
                    var classInst = new IntPtr[owningTypeInst.Length];
                    for (int i = 0; i < owningTypeInst.Length; i++)
                        classInst[i] = (IntPtr)ObjectToHandle(owningTypeInst[i]);
                    sig->sigInst.classInst = (CORINFO_CLASS_STRUCT_**)GetPin(classInst);
                }
            }

            if (hasHiddenParameter)
            {
                sig->callConv |= CorInfoCallConv.CORINFO_CALLCONV_PARAMTYPE;
            }
        }

        private void Get_CORINFO_SIG_INFO(MethodSignature signature, CORINFO_SIG_INFO* sig)
        {
            sig->callConv = (CorInfoCallConv)(signature.Flags & MethodSignatureFlags.UnmanagedCallingConventionMask);

            // Varargs are not supported in .NET Core
            if (sig->callConv == CorInfoCallConv.CORINFO_CALLCONV_VARARG)
                ThrowHelper.ThrowBadImageFormatException();

            if (!signature.IsStatic) sig->callConv |= CorInfoCallConv.CORINFO_CALLCONV_HASTHIS;

            TypeDesc returnType = signature.ReturnType;

            CorInfoType corInfoRetType = asCorInfoType(signature.ReturnType, &sig->retTypeClass);
            sig->_retType = (byte)corInfoRetType;
            sig->retTypeSigClass = sig->retTypeClass; // The difference between the two is not relevant for ILCompiler

            sig->flags = 0;    // used by IL stubs code

            sig->numArgs = (ushort)signature.Length;

            sig->args = (CORINFO_ARG_LIST_STRUCT_*)0; // CORINFO_ARG_LIST_STRUCT_ is argument index

            sig->sigInst.classInst = null; // Not used by the JIT 
            sig->sigInst.classInstCount = 0; // Not used by the JIT 
            sig->sigInst.methInst = null; // Not used by the JIT 
            sig->sigInst.methInstCount = (uint)signature.GenericParameterCount;

            sig->pSig = (byte*)ObjectToHandle(signature);
            sig->cbSig = 0; // Not used by the JIT
            sig->scope = null; // Not used by the JIT
            sig->token = 0; // Not used by the JIT
        }

        private void Get_CORINFO_SIG_INFO(LocalVariableDefinition[] locals, CORINFO_SIG_INFO* sig)
        {
            sig->callConv = CorInfoCallConv.CORINFO_CALLCONV_DEFAULT;
            sig->_retType = (byte)CorInfoType.CORINFO_TYPE_VOID;
            sig->retTypeClass = null;
            sig->retTypeSigClass = null;
            sig->flags = (byte)CorInfoSigInfoFlags.CORINFO_SIGFLAG_IS_LOCAL_SIG;

            sig->numArgs = (ushort)locals.Length;

            sig->sigInst.classInst = null;
            sig->sigInst.classInstCount = 0;
            sig->sigInst.methInst = null;
            sig->sigInst.methInstCount = 0;

            sig->args = (CORINFO_ARG_LIST_STRUCT_*)0; // CORINFO_ARG_LIST_STRUCT_ is argument index

            sig->pSig = (byte*)ObjectToHandle(locals);
            sig->cbSig = 0; // Not used by the JIT
            sig->scope = null; // Not used by the JIT
            sig->token = 0; // Not used by the JIT
        }

        private CorInfoType asCorInfoType(TypeDesc type)
        {
            if (type.IsEnum)
            {
                type = type.UnderlyingType;
            }

            if (type.IsPrimitive)
            {
                Debug.Assert((CorInfoType)TypeFlags.Void == CorInfoType.CORINFO_TYPE_VOID);
                Debug.Assert((CorInfoType)TypeFlags.Double == CorInfoType.CORINFO_TYPE_DOUBLE);

                return (CorInfoType)type.Category;
            }

            if (type.IsPointer || type.IsFunctionPointer)
            {
                return CorInfoType.CORINFO_TYPE_PTR;
            }

            if (type.IsByRef)
            {
                return CorInfoType.CORINFO_TYPE_BYREF;
            }

            if (type.IsValueType)
            {
                return CorInfoType.CORINFO_TYPE_VALUECLASS;
            }

            return CorInfoType.CORINFO_TYPE_CLASS;
        }

        private CorInfoType asCorInfoType(TypeDesc type, CORINFO_CLASS_STRUCT_** structType)
        {
            var corInfoType = asCorInfoType(type);
            *structType = ((corInfoType == CorInfoType.CORINFO_TYPE_CLASS) ||
                (corInfoType == CorInfoType.CORINFO_TYPE_VALUECLASS) ||
                (corInfoType == CorInfoType.CORINFO_TYPE_BYREF)) ? ObjectToHandle(type) : null;
            return corInfoType;
        }

        private CORINFO_CONTEXT_STRUCT* contextFromMethod(MethodDesc method)
        {
            return (CORINFO_CONTEXT_STRUCT*)(((ulong)ObjectToHandle(method)) | (ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_METHOD);
        }

        private CORINFO_CONTEXT_STRUCT* contextFromType(TypeDesc type)
        {
            return (CORINFO_CONTEXT_STRUCT*)(((ulong)ObjectToHandle(type)) | (ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_CLASS);
        }

        private MethodDesc methodFromContext(CORINFO_CONTEXT_STRUCT* contextStruct)
        {
            if (((ulong)contextStruct & (ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_MASK) == (ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_CLASS)
            {
                return null;
            }
            else
            {
                return HandleToObject((CORINFO_METHOD_STRUCT_*)((ulong)contextStruct & ~(ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_MASK));
            }
        }

        private TypeDesc typeFromContext(CORINFO_CONTEXT_STRUCT* contextStruct)
        {
            if (((ulong)contextStruct & (ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_MASK) == (ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_CLASS)
            {
                return HandleToObject((CORINFO_CLASS_STRUCT_*)((ulong)contextStruct & ~(ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_MASK));
            }
            else
            {
                return methodFromContext(contextStruct).OwningType;
            }
        }

        private TypeSystemEntity entityFromContext(CORINFO_CONTEXT_STRUCT* contextStruct)
        {
            return (TypeSystemEntity)HandleToObject((IntPtr)((ulong)contextStruct & ~(ulong)CorInfoContextFlags.CORINFO_CONTEXTFLAGS_MASK));
        }

        private uint getMethodAttribsInternal(MethodDesc method)
        {
            CorInfoFlag result = 0;

            // CORINFO_FLG_PROTECTED - verification only

            if (method.Signature.IsStatic)
                result |= CorInfoFlag.CORINFO_FLG_STATIC;

            if (method.IsSynchronized)
                result |= CorInfoFlag.CORINFO_FLG_SYNCH;
            if (method.IsIntrinsic)
                result |= CorInfoFlag.CORINFO_FLG_INTRINSIC | CorInfoFlag.CORINFO_FLG_JIT_INTRINSIC;
            if (method.IsVirtual)
                result |= CorInfoFlag.CORINFO_FLG_VIRTUAL;
            if (method.IsAbstract)
                result |= CorInfoFlag.CORINFO_FLG_ABSTRACT;
            if (method.IsConstructor || method.IsStaticConstructor)
                result |= CorInfoFlag.CORINFO_FLG_CONSTRUCTOR;

            //
            // See if we need to embed a .cctor call at the head of the
            // method body.
            //

            // method or class might have the final bit
            if (_compilation.IsEffectivelySealed(method))
                result |= CorInfoFlag.CORINFO_FLG_FINAL;

            if (method.IsSharedByGenericInstantiations)
                result |= CorInfoFlag.CORINFO_FLG_SHAREDINST;

            if (method.IsPInvoke)
            {
                result |= CorInfoFlag.CORINFO_FLG_PINVOKE;
            }

            if (method.IsAggressiveOptimization)
            {
                result |= CorInfoFlag.CORINFO_FLG_AGGRESSIVE_OPT;
            }

            // TODO: Cache inlining hits
            // Check for an inlining directive.

            if (method.IsNoInlining)
            {
                /* Function marked as not inlineable */
                result |= CorInfoFlag.CORINFO_FLG_DONT_INLINE;
            }
            else if (method.IsAggressiveInlining)
            {
                result |= CorInfoFlag.CORINFO_FLG_FORCEINLINE;
            }

            if (method.OwningType.IsDelegate && method.Name == "Invoke")
            {
                // This is now used to emit efficient invoke code for any delegate invoke,
                // including multicast.
                result |= CorInfoFlag.CORINFO_FLG_DELEGATE_INVOKE;

                // RyuJIT special cases this method; it would assert if it's not final
                // and we might not have set the bit in the code above.
                result |= CorInfoFlag.CORINFO_FLG_FINAL;
            }

            // Check for hardware intrinsics
            if (HardwareIntrinsicHelpers.IsHardwareIntrinsic(method))
            {
#if !READYTORUN
                // Do not report the get_IsSupported method as an intrinsic - RyuJIT would expand it to
                // a constant depending on the code generation flags passed to it, but we would like to
                // do a dynamic check instead.
                if (!HardwareIntrinsicHelpers.IsIsSupportedMethod(method)
                    || HardwareIntrinsicHelpers.IsKnownSupportedIntrinsicAtCompileTime(method))
#endif
                {
                    result |= CorInfoFlag.CORINFO_FLG_JIT_INTRINSIC;
                }
            }

            result |= CorInfoFlag.CORINFO_FLG_NOSECURITYWRAP;

            return (uint)result;
        }

        private uint getMethodAttribs(CORINFO_METHOD_STRUCT_* ftn)
        {
            return getMethodAttribsInternal(HandleToObject(ftn));
        }

        private void setMethodAttribs(CORINFO_METHOD_STRUCT_* ftn, CorInfoMethodRuntimeFlags attribs)
        {
            // TODO: Inlining
        }

        private void getMethodSig(CORINFO_METHOD_STRUCT_* ftn, CORINFO_SIG_INFO* sig, CORINFO_CLASS_STRUCT_* memberParent)
        {
            MethodDesc method = HandleToObject(ftn);

            // There might be a more concrete parent type specified - this can happen when inlining.
            if (memberParent != null)
            {
                TypeDesc type = HandleToObject(memberParent);

                // Typically, the owning type of the method is a canonical type and the member parent
                // supplied by RyuJIT is a concrete instantiation.
                if (type != method.OwningType)
                {
                    Debug.Assert(type.HasSameTypeDefinition(method.OwningType));
                    Instantiation methodInst = method.Instantiation;
                    method = _compilation.TypeSystemContext.GetMethodForInstantiatedType(method.GetTypicalMethodDefinition(), (InstantiatedType)type);
                    if (methodInst.Length > 0)
                    {
                        method = method.MakeInstantiatedMethod(methodInst);
                    }
                }
            }

            Get_CORINFO_SIG_INFO(method, sig);
        }

        private bool getMethodInfo(CORINFO_METHOD_STRUCT_* ftn, CORINFO_METHOD_INFO* info)
        {
            MethodIL methodIL = Get_CORINFO_METHOD_INFO(HandleToObject(ftn), null, info);
            return methodIL != null;
        }

        private CorInfoInline canInline(CORINFO_METHOD_STRUCT_* callerHnd, CORINFO_METHOD_STRUCT_* calleeHnd, ref uint pRestrictions)
        {
            MethodDesc callerMethod = HandleToObject(callerHnd);
            MethodDesc calleeMethod = HandleToObject(calleeHnd);
            if (_compilation.CanInline(callerMethod, calleeMethod))
            {
                // No restrictions on inlining
                return CorInfoInline.INLINE_PASS;
            }
            else
            {
                // Call may not be inlined
                return CorInfoInline.INLINE_NEVER;
            }
        }

        private void reportInliningDecision(CORINFO_METHOD_STRUCT_* inlinerHnd, CORINFO_METHOD_STRUCT_* inlineeHnd, CorInfoInline inlineResult, byte* reason)
        {
        }

        private void reportTailCallDecision(CORINFO_METHOD_STRUCT_* callerHnd, CORINFO_METHOD_STRUCT_* calleeHnd, bool fIsTailPrefix, CorInfoTailCall tailCallResult, byte* reason)
        {
        }

        private void getEHinfo(CORINFO_METHOD_STRUCT_* ftn, uint EHnumber, ref CORINFO_EH_CLAUSE clause)
        {
            var methodIL = _compilation.GetMethodIL(HandleToObject(ftn));

            var ehRegion = methodIL.GetExceptionRegions()[EHnumber];

            clause.Flags = (CORINFO_EH_CLAUSE_FLAGS)ehRegion.Kind;
            clause.TryOffset = (uint)ehRegion.TryOffset;
            clause.TryLength = (uint)ehRegion.TryLength;
            clause.HandlerOffset = (uint)ehRegion.HandlerOffset;
            clause.HandlerLength = (uint)ehRegion.HandlerLength;
            clause.ClassTokenOrOffset = (uint)((ehRegion.Kind == ILExceptionRegionKind.Filter) ? ehRegion.FilterOffset : ehRegion.ClassToken);
        }

        private CORINFO_CLASS_STRUCT_* getMethodClass(CORINFO_METHOD_STRUCT_* method)
        {
            var m = HandleToObject(method);
            return ObjectToHandle(m.OwningType);
        }

        private CORINFO_MODULE_STRUCT_* getMethodModule(CORINFO_METHOD_STRUCT_* method)
        {
            MethodDesc m = HandleToObject(method);
            if (m is UnboxingMethodDesc unboxingMethodDesc)
            {
                m = unboxingMethodDesc.Target;
            }

            MethodIL methodIL = _compilation.GetMethodIL(m);
            if (methodIL == null)
            {
                return null;
            }
            return (CORINFO_MODULE_STRUCT_*)ObjectToHandle(methodIL);
        }

        private CORINFO_METHOD_STRUCT_* resolveVirtualMethod(CORINFO_METHOD_STRUCT_* baseMethod, CORINFO_CLASS_STRUCT_* derivedClass, CORINFO_CONTEXT_STRUCT* ownerType)
        {
            TypeDesc implType = HandleToObject(derivedClass);

            // __Canon cannot be devirtualized
            if (implType.IsCanonicalDefinitionType(CanonicalFormKind.Any))
            {
                return null;
            }

            MethodDesc decl = HandleToObject(baseMethod);
            Debug.Assert(!decl.HasInstantiation);

            if (ownerType != null)
            {
                TypeDesc ownerTypeDesc = typeFromContext(ownerType);
                if (decl.OwningType != ownerTypeDesc)
                {
                    Debug.Assert(ownerTypeDesc is InstantiatedType);
                    decl = _compilation.TypeSystemContext.GetMethodForInstantiatedType(decl.GetTypicalMethodDefinition(), (InstantiatedType)ownerTypeDesc);
                }
            }

            MethodDesc impl = _compilation.ResolveVirtualMethod(decl, implType);

            if (impl != null)
            {
                if (impl.OwningType.IsValueType)
                {
                    impl = _unboxingThunkFactory.GetUnboxingMethod(impl);
                }

                return ObjectToHandle(impl);
            }

            return null;
        }

        private CORINFO_METHOD_STRUCT_* getUnboxedEntry(CORINFO_METHOD_STRUCT_* ftn, byte* requiresInstMethodTableArg)
        {
            MethodDesc result = null;
            bool requiresInstMTArg = false;

            MethodDesc method = HandleToObject(ftn);
            if (method.IsUnboxingThunk())
            {
                result = method.GetUnboxedMethod();
                requiresInstMTArg = method.RequiresInstMethodTableArg();
            }

            if (requiresInstMethodTableArg != null)
            {
                *requiresInstMethodTableArg = requiresInstMTArg ? (byte)1 : (byte)0;
            }

            return result != null ? ObjectToHandle(result) : null;
        }

        private CORINFO_CLASS_STRUCT_* getDefaultEqualityComparerClass(CORINFO_CLASS_STRUCT_* elemType)
        {
            TypeDesc comparand = HandleToObject(elemType);
            TypeDesc comparer = IL.Stubs.ComparerIntrinsics.GetEqualityComparerForType(comparand);
            return comparer != null ? ObjectToHandle(comparer) : null;
        }

        private SimdHelper _simdHelper;
        private bool isInSIMDModule(CORINFO_CLASS_STRUCT_* classHnd)
        {
            TypeDesc type = HandleToObject(classHnd);
            
            if (_simdHelper.IsSimdType(type))
            {
#if DEBUG
                // If this is Vector<T>, make sure the codegen and the type system agree on what instructions/registers
                // we're generating code for.

                CORJIT_FLAGS flags = default(CORJIT_FLAGS);
                getJitFlags(ref flags, (uint)sizeof(CORJIT_FLAGS));

                Debug.Assert(!_simdHelper.IsVectorOfT(type)
                    || ((DefType)type).InstanceFieldSize.AsInt == GetMaxIntrinsicSIMDVectorLength(_jit, &flags));
#endif

                return true;
            }

            return false;
        }

        private CorInfoUnmanagedCallConv getUnmanagedCallConv(CORINFO_METHOD_STRUCT_* method)
        {
            MethodSignatureFlags unmanagedCallConv = HandleToObject(method).GetPInvokeMethodMetadata().Flags.UnmanagedCallingConvention;

            // Verify that it is safe to convert MethodSignatureFlags.UnmanagedCallingConvention to CorInfoUnmanagedCallConv via a simple cast
            Debug.Assert((int)CorInfoUnmanagedCallConv.CORINFO_UNMANAGED_CALLCONV_C == (int)MethodSignatureFlags.UnmanagedCallingConventionCdecl);
            Debug.Assert((int)CorInfoUnmanagedCallConv.CORINFO_UNMANAGED_CALLCONV_STDCALL == (int)MethodSignatureFlags.UnmanagedCallingConventionStdCall);
            Debug.Assert((int)CorInfoUnmanagedCallConv.CORINFO_UNMANAGED_CALLCONV_THISCALL == (int)MethodSignatureFlags.UnmanagedCallingConventionThisCall);

            return (CorInfoUnmanagedCallConv)unmanagedCallConv;
        }

        private bool satisfiesMethodConstraints(CORINFO_CLASS_STRUCT_* parent, CORINFO_METHOD_STRUCT_* method)
        { throw new NotImplementedException("satisfiesMethodConstraints"); }
        private bool isCompatibleDelegate(CORINFO_CLASS_STRUCT_* objCls, CORINFO_CLASS_STRUCT_* methodParentCls, CORINFO_METHOD_STRUCT_* method, CORINFO_CLASS_STRUCT_* delegateCls, ref bool pfIsOpenDelegate)
        { throw new NotImplementedException("isCompatibleDelegate"); }
        private CorInfoInstantiationVerification isInstantiationOfVerifiedGeneric(CORINFO_METHOD_STRUCT_* method)
        { throw new NotImplementedException("isInstantiationOfVerifiedGeneric"); }
        private void initConstraintsForVerification(CORINFO_METHOD_STRUCT_* method, ref bool pfHasCircularClassConstraints, ref bool pfHasCircularMethodConstraint)
        { throw new NotImplementedException("isInstantiationOfVerifiedGeneric"); }

        private CorInfoCanSkipVerificationResult canSkipMethodVerification(CORINFO_METHOD_STRUCT_* ftnHandle)
        {
            return CorInfoCanSkipVerificationResult.CORINFO_VERIFICATION_CAN_SKIP;
        }

        private void methodMustBeLoadedBeforeCodeIsRun(CORINFO_METHOD_STRUCT_* method)
        {
        }

        private CORINFO_METHOD_STRUCT_* mapMethodDeclToMethodImpl(CORINFO_METHOD_STRUCT_* method)
        { throw new NotImplementedException("mapMethodDeclToMethodImpl"); }

        private static object ResolveTokenWithSubstitution(MethodIL methodIL, mdToken token, Instantiation typeInst, Instantiation methodInst)
        {
            // Grab the generic definition of the method IL, resolve the token within the definition,
            // and instantiate it with the given context.
            object result = methodIL.GetMethodILDefinition().GetObject((int)token);

            if (result is MethodDesc methodResult)
            {
                result = methodResult.InstantiateSignature(typeInst, methodInst);
            }
            else if (result is FieldDesc fieldResult)
            {
                result = fieldResult.InstantiateSignature(typeInst, methodInst);
            }
            else
            {
                result = ((TypeDesc)result).InstantiateSignature(typeInst, methodInst);
            }

            return result;
        }

        private static object ResolveTokenInScope(MethodIL methodIL, object typeOrMethodContext, mdToken token)
        {
            MethodDesc owningMethod = methodIL.OwningMethod;

            // If token context differs from the scope, it means we're inlining.
            // If we're inlining a shared method body, we might be able to un-share
            // the referenced token and avoid runtime lookups.
            // Resolve the token in the inlining context.

            object result;
            if (owningMethod != typeOrMethodContext &&
                owningMethod.IsCanonicalMethod(CanonicalFormKind.Any))
            {
                Instantiation typeInst = default;
                Instantiation methodInst = default;

                if (typeOrMethodContext is TypeDesc typeContext)
                {
                    Debug.Assert(typeContext.HasSameTypeDefinition(owningMethod.OwningType));
                    typeInst = typeContext.Instantiation;
                }
                else
                {
                    var methodContext = (MethodDesc)typeOrMethodContext;
                    Debug.Assert(methodContext.GetTypicalMethodDefinition() == owningMethod.GetTypicalMethodDefinition());
                    typeInst = methodContext.OwningType.Instantiation;
                    methodInst = methodContext.Instantiation;
                }

                result = ResolveTokenWithSubstitution(methodIL, token, typeInst, methodInst);
            }
            else
            {
                // Not inlining - just resolve the token within the methodIL
                result = methodIL.GetObject((int)token);
            }

            return result;
        }

        private object GetRuntimeDeterminedObjectForToken(ref CORINFO_RESOLVED_TOKEN pResolvedToken)
        {
            // Since RyuJIT operates on canonical types (as opposed to runtime determined ones), but the
            // dependency analysis operates on runtime determined ones, we convert the resolved token 
            // to the runtime determined form (e.g. Foo<__Canon> becomes Foo<T__Canon>). 

            var methodIL = (MethodIL)HandleToObject((IntPtr)pResolvedToken.tokenScope);
            var typeOrMethodContext = HandleToObject((IntPtr)pResolvedToken.tokenContext);

            object result = ResolveTokenInScope(methodIL, typeOrMethodContext, pResolvedToken.token);

            if (result is MethodDesc method)
            {
                if (method.IsSharedByGenericInstantiations)
                {
                    MethodDesc sharedMethod = methodIL.OwningMethod.GetSharedRuntimeFormMethodTarget();
                    result = ResolveTokenWithSubstitution(methodIL, pResolvedToken.token, sharedMethod.OwningType.Instantiation, sharedMethod.Instantiation);
                    Debug.Assert(((MethodDesc)result).IsRuntimeDeterminedExactMethod);
                }
            }
            else if (result is FieldDesc field)
            {
                if (field.OwningType.IsCanonicalSubtype(CanonicalFormKind.Any))
                {
                    MethodDesc sharedMethod = methodIL.OwningMethod.GetSharedRuntimeFormMethodTarget();
                    result = ResolveTokenWithSubstitution(methodIL, pResolvedToken.token, sharedMethod.OwningType.Instantiation, sharedMethod.Instantiation);
                    Debug.Assert(((FieldDesc)result).OwningType.IsRuntimeDeterminedSubtype);
                }
            }
            else
            {
                TypeDesc type = (TypeDesc)result;
                if (type.IsCanonicalSubtype(CanonicalFormKind.Any))
                {
                    MethodDesc sharedMethod = methodIL.OwningMethod.GetSharedRuntimeFormMethodTarget();
                    result = ResolveTokenWithSubstitution(methodIL, pResolvedToken.token, sharedMethod.OwningType.Instantiation, sharedMethod.Instantiation);
                    Debug.Assert(((TypeDesc)result).IsRuntimeDeterminedSubtype ||
                        /* If the resolved type is not runtime determined there's a chance we went down this path
                           because there was a literal typeof(__Canon) in the compiled IL - check for that
                           by resolving the token in the definition. */
                        ((TypeDesc)methodIL.GetMethodILDefinition().GetObject((int)pResolvedToken.token)).IsCanonicalDefinitionType(CanonicalFormKind.Any));
                }

                if (pResolvedToken.tokenType == CorInfoTokenKind.CORINFO_TOKENKIND_Newarr)
                    result = ((TypeDesc)result).MakeArrayType();
            }

            return result;
        }

        private void resolveToken(ref CORINFO_RESOLVED_TOKEN pResolvedToken)
        {
            var methodIL = (MethodIL)HandleToObject((IntPtr)pResolvedToken.tokenScope);
            var typeOrMethodContext = HandleToObject((IntPtr)pResolvedToken.tokenContext);

            object result = ResolveTokenInScope(methodIL, typeOrMethodContext, pResolvedToken.token);

            pResolvedToken.hClass = null;
            pResolvedToken.hMethod = null;
            pResolvedToken.hField = null;

#if READYTORUN
            TypeDesc owningType = methodIL.OwningMethod.GetTypicalMethodDefinition().OwningType;
            bool recordToken = _compilation.NodeFactory.CompilationModuleGroup.VersionsWithType(owningType) && owningType is EcmaType;
#endif

            if (result is MethodDesc)
            {
                MethodDesc method = result as MethodDesc;
                pResolvedToken.hMethod = ObjectToHandle(method);

                TypeDesc owningClass = method.OwningType;
                pResolvedToken.hClass = ObjectToHandle(owningClass);

#if !SUPPORT_JIT
                _compilation.TypeSystemContext.EnsureLoadableType(owningClass);
#endif

#if READYTORUN
                if (recordToken)
                {
                    _compilation.NodeFactory.Resolver.AddModuleTokenForMethod(method, HandleToModuleToken(ref pResolvedToken));
                }
#endif
            }
            else
            if (result is FieldDesc)
            {
                FieldDesc field = result as FieldDesc;

                // References to literal fields from IL body should never resolve.
                // The CLR would throw a MissingFieldException while jitting and so should we.
                if (field.IsLiteral)
                    ThrowHelper.ThrowMissingFieldException(field.OwningType, field.Name);

                pResolvedToken.hField = ObjectToHandle(field);

                TypeDesc owningClass = field.OwningType;
                pResolvedToken.hClass = ObjectToHandle(owningClass);

#if !SUPPORT_JIT
                _compilation.TypeSystemContext.EnsureLoadableType(owningClass);
#endif

#if READYTORUN
                if (recordToken)
                {
                    _compilation.NodeFactory.Resolver.AddModuleTokenForField(field, HandleToModuleToken(ref pResolvedToken));
                }
#endif
            }
            else
            {
                TypeDesc type = (TypeDesc)result;

#if READYTORUN
                if (recordToken)
                {
                    _compilation.NodeFactory.Resolver.AddModuleTokenForType(type, HandleToModuleToken(ref pResolvedToken));
                }
#endif

                if (pResolvedToken.tokenType == CorInfoTokenKind.CORINFO_TOKENKIND_Newarr)
                {
                    if (type.IsVoid)
                        ThrowHelper.ThrowInvalidProgramException(ExceptionStringID.InvalidProgramSpecific, methodIL.OwningMethod);

                    type = type.MakeArrayType();
                }
                pResolvedToken.hClass = ObjectToHandle(type);

#if !SUPPORT_JIT
                _compilation.TypeSystemContext.EnsureLoadableType(type);
#endif
            }

            pResolvedToken.pTypeSpec = null;
            pResolvedToken.cbTypeSpec = 0;
            pResolvedToken.pMethodSpec = null;
            pResolvedToken.cbMethodSpec = 0;
        }

        private bool tryResolveToken(ref CORINFO_RESOLVED_TOKEN pResolvedToken)
        {
            resolveToken(ref pResolvedToken);
            return true;
        }

        private void findSig(CORINFO_MODULE_STRUCT_* module, uint sigTOK, CORINFO_CONTEXT_STRUCT* context, CORINFO_SIG_INFO* sig)
        {
            var methodIL = (MethodIL)HandleToObject((IntPtr)module);
            Get_CORINFO_SIG_INFO((MethodSignature)methodIL.GetObject((int)sigTOK), sig);
        }

        private void findCallSiteSig(CORINFO_MODULE_STRUCT_* module, uint methTOK, CORINFO_CONTEXT_STRUCT* context, CORINFO_SIG_INFO* sig)
        {
            var methodIL = (MethodIL)HandleToObject((IntPtr)module);
            Get_CORINFO_SIG_INFO(((MethodDesc)methodIL.GetObject((int)methTOK)), sig);
        }

        private CORINFO_CLASS_STRUCT_* getTokenTypeAsHandle(ref CORINFO_RESOLVED_TOKEN pResolvedToken)
        {
            WellKnownType result = WellKnownType.RuntimeTypeHandle;

            if (pResolvedToken.hMethod != null)
            {
                result = WellKnownType.RuntimeMethodHandle;
            }
            else
            if (pResolvedToken.hField != null)
            {
                result = WellKnownType.RuntimeFieldHandle;
            }

            return ObjectToHandle(_compilation.TypeSystemContext.GetWellKnownType(result));
        }

        private CorInfoCanSkipVerificationResult canSkipVerification(CORINFO_MODULE_STRUCT_* module)
        {
            return CorInfoCanSkipVerificationResult.CORINFO_VERIFICATION_CAN_SKIP;
        }

        private bool isValidToken(CORINFO_MODULE_STRUCT_* module, uint metaTOK)
        { throw new NotImplementedException("isValidToken"); }
        private bool isValidStringRef(CORINFO_MODULE_STRUCT_* module, uint metaTOK)
        { throw new NotImplementedException("isValidStringRef"); }
        private bool shouldEnforceCallvirtRestriction(CORINFO_MODULE_STRUCT_* scope)
        { throw new NotImplementedException("shouldEnforceCallvirtRestriction"); }

        private CorInfoType asCorInfoType(CORINFO_CLASS_STRUCT_* cls)
        {
            var type = HandleToObject(cls);
            return asCorInfoType(type);
        }

        private byte* getClassName(CORINFO_CLASS_STRUCT_* cls)
        {
            var type = HandleToObject(cls);
            return (byte*)GetPin(StringToUTF8(type.ToString()));
        }

        private byte* getClassNameFromMetadata(CORINFO_CLASS_STRUCT_* cls, byte** namespaceName)
        {
            var type = HandleToObject(cls) as MetadataType;
            if (type != null)
            {
                if (namespaceName != null)
                    *namespaceName = (byte*)GetPin(StringToUTF8(type.Namespace));
                return (byte*)GetPin(StringToUTF8(type.Name));
            }

            if (namespaceName != null)
                *namespaceName = null;
            return null;
        }
        
        private CORINFO_CLASS_STRUCT_* getTypeInstantiationArgument(CORINFO_CLASS_STRUCT_* cls, uint index)
        {
            TypeDesc type = HandleToObject(cls);
            Instantiation inst = type.Instantiation;

            return index < (uint)inst.Length ? ObjectToHandle(inst[(int)index]) : null;
        }


        private int appendClassName(short** ppBuf, ref int pnBufLen, CORINFO_CLASS_STRUCT_* cls, bool fNamespace, bool fFullInst, bool fAssembly)
        {
            // We support enough of this to make SIMD work, but not much else.

            Debug.Assert(fNamespace && !fFullInst && !fAssembly);

            var type = HandleToObject(cls);
            string name = TypeString.Instance.FormatName(type);

            int length = name.Length;
            if (pnBufLen > 0)
            {
                short* buffer = *ppBuf;
                for (int i = 0; i < Math.Min(name.Length, pnBufLen); i++)
                    buffer[i] = (short)name[i];
                if (name.Length < pnBufLen)
                    buffer[name.Length] = 0;
                else
                    buffer[pnBufLen - 1] = 0;
                pnBufLen -= length;
                *ppBuf = buffer + length;
            }

            return length;
        }

        private bool isValueClass(CORINFO_CLASS_STRUCT_* cls)
        {
            return HandleToObject(cls).IsValueType;
        }

        private CorInfoInlineTypeCheck canInlineTypeCheck(CORINFO_CLASS_STRUCT_* cls, CorInfoInlineTypeCheckSource source)
        {
            // TODO: when we support multiple modules at runtime, this will need to do more work
            // NOTE: cls can be null
            return CorInfoInlineTypeCheck.CORINFO_INLINE_TYPECHECK_PASS;
        }

        private bool canInlineTypeCheckWithObjectVTable(CORINFO_CLASS_STRUCT_* cls) { throw new NotImplementedException(); }

        private uint getClassAttribs(CORINFO_CLASS_STRUCT_* cls)
        {
            TypeDesc type = HandleToObject(cls);
            return getClassAttribsInternal(type);
        }

        private uint getClassAttribsInternal(TypeDesc type)
        {
            // TODO: Support for verification (CORINFO_FLG_GENERIC_TYPE_VARIABLE)

            CorInfoFlag result = (CorInfoFlag)0;

            var metadataType = type as MetadataType;

            // The array flag is used to identify the faked-up methods on
            // array types, i.e. .ctor, Get, Set and Address
            if (type.IsArray)
                result |= CorInfoFlag.CORINFO_FLG_ARRAY;

            if (type.IsInterface)
                result |= CorInfoFlag.CORINFO_FLG_INTERFACE;

            if (type.IsArray || type.IsString)
                result |= CorInfoFlag.CORINFO_FLG_VAROBJSIZE;

            if (type.IsValueType)
            {
                result |= CorInfoFlag.CORINFO_FLG_VALUECLASS;

                if (metadataType.IsByRefLike)
                    result |= CorInfoFlag.CORINFO_FLG_CONTAINS_STACK_PTR;

                // The CLR has more complicated rules around CUSTOMLAYOUT, but this will do.
                if (metadataType.IsExplicitLayout || metadataType.IsWellKnownType(WellKnownType.TypedReference))
                    result |= CorInfoFlag.CORINFO_FLG_CUSTOMLAYOUT;

                // TODO
                // if (type.IsUnsafeValueType)
                //    result |= CorInfoFlag.CORINFO_FLG_UNSAFE_VALUECLASS;
            }

            if (type.IsCanonicalSubtype(CanonicalFormKind.Any))
                result |= CorInfoFlag.CORINFO_FLG_SHAREDINST;

            if (type.HasVariance)
                result |= CorInfoFlag.CORINFO_FLG_VARIANCE;

            if (type.IsDelegate)
                result |= CorInfoFlag.CORINFO_FLG_DELEGATE;

            if (_compilation.IsEffectivelySealed(type))
                result |= CorInfoFlag.CORINFO_FLG_FINAL;

            if (type.IsIntrinsic)
                result |= CorInfoFlag.CORINFO_FLG_INTRINSIC_TYPE;

            if (metadataType != null)
            {
                if (metadataType.ContainsGCPointers)
                    result |= CorInfoFlag.CORINFO_FLG_CONTAINS_GC_PTR;

                if (metadataType.IsBeforeFieldInit)
                    result |= CorInfoFlag.CORINFO_FLG_BEFOREFIELDINIT;

                // Assume overlapping fields for explicit layout.
                if (metadataType.IsExplicitLayout)
                    result |= CorInfoFlag.CORINFO_FLG_OVERLAPPING_FIELDS;

                if (metadataType.IsAbstract)
                    result |= CorInfoFlag.CORINFO_FLG_ABSTRACT;
            }

            return (uint)result;
        }

        private bool isStructRequiringStackAllocRetBuf(CORINFO_CLASS_STRUCT_* cls)
        {
            // Disable this optimization. It has limited value (only kicks in on x86, and only for less common structs),
            // causes bugs and introduces odd ABI differences not compatible with ReadyToRun.
            return false;
        }

        private CORINFO_MODULE_STRUCT_* getClassModule(CORINFO_CLASS_STRUCT_* cls)
        { throw new NotImplementedException("getClassModule"); }
        private CORINFO_ASSEMBLY_STRUCT_* getModuleAssembly(CORINFO_MODULE_STRUCT_* mod)
        { throw new NotImplementedException("getModuleAssembly"); }
        private byte* getAssemblyName(CORINFO_ASSEMBLY_STRUCT_* assem)
        { throw new NotImplementedException("getAssemblyName"); }

        private void* LongLifetimeMalloc(UIntPtr sz)
        {
            return (void*)Marshal.AllocCoTaskMem((int)sz);
        }

        private void LongLifetimeFree(void* obj)
        {
            Marshal.FreeCoTaskMem((IntPtr)obj);
        }

        private byte* getClassModuleIdForStatics(CORINFO_CLASS_STRUCT_* cls, CORINFO_MODULE_STRUCT_** pModule, void** ppIndirection)
        { throw new NotImplementedException("getClassModuleIdForStatics"); }

        private uint getClassSize(CORINFO_CLASS_STRUCT_* cls)
        {
            TypeDesc type = HandleToObject(cls);
            LayoutInt classSize = type.GetElementSize();
#if READYTORUN
            if (classSize.IsIndeterminate)
            {
                throw new RequiresRuntimeJitException(type);
            }
#endif
            return (uint)classSize.AsInt;
        }

        private uint getHeapClassSize(CORINFO_CLASS_STRUCT_* cls)
        {
            TypeDesc type = HandleToObject(cls);

            Debug.Assert(!type.IsValueType);
            Debug.Assert(type.IsDefType);
            Debug.Assert(!type.IsString);
#if READYTORUN
            Debug.Assert(_compilation.IsInheritanceChainLayoutFixedInCurrentVersionBubble(type));
#endif

            return (uint)((DefType)type).InstanceByteCount.AsInt;
        }

        private bool canAllocateOnStack(CORINFO_CLASS_STRUCT_* cls)
        {
            TypeDesc type = HandleToObject(cls);

            Debug.Assert(!type.IsValueType);
            Debug.Assert(type.IsDefType);

            bool result = !type.HasFinalizer;

#if READYTORUN
            if (!_compilation.IsInheritanceChainLayoutFixedInCurrentVersionBubble(type))
                result = false;
#endif

            return result;
        }

        private uint getClassAlignmentRequirement(CORINFO_CLASS_STRUCT_* cls, bool fDoubleAlignHint)
        {
            DefType type = (DefType)HandleToObject(cls);
            return (uint)type.InstanceFieldAlignment.AsInt;
        }

        private int GatherClassGCLayout(TypeDesc type, byte* gcPtrs)
        {
            int result = 0;

            if (type.IsByReferenceOfT)
            {
                *gcPtrs = (byte)CorInfoGCType.TYPE_GC_BYREF;
                return 1;
            }

            foreach (var field in type.GetFields())
            {
                if (field.IsStatic)
                    continue;

                CorInfoGCType gcType = CorInfoGCType.TYPE_GC_NONE;

                var fieldType = field.FieldType;
                if (fieldType.IsValueType)
                {
                    var fieldDefType = (DefType)fieldType;
                    if (!fieldDefType.ContainsGCPointers && !fieldDefType.IsByRefLike)
                        continue;

                    gcType = CorInfoGCType.TYPE_GC_OTHER;
                }
                else if (fieldType.IsGCPointer)
                {
                    gcType = CorInfoGCType.TYPE_GC_REF;
                }
                else if (fieldType.IsByRef)
                {
                    gcType = CorInfoGCType.TYPE_GC_BYREF;
                }
                else
                {
                    continue;
                }

                Debug.Assert(field.Offset.AsInt % PointerSize == 0);
                byte* fieldGcPtrs = gcPtrs + field.Offset.AsInt / PointerSize;

                if (gcType == CorInfoGCType.TYPE_GC_OTHER)
                {
                    result += GatherClassGCLayout(fieldType, fieldGcPtrs);
                }
                else
                {
                    // Ensure that if we have multiple fields with the same offset, 
                    // that we don't double count the data in the gc layout.
                    if (*fieldGcPtrs == (byte)CorInfoGCType.TYPE_GC_NONE)
                    {
                        *fieldGcPtrs = (byte)gcType;
                        result++;
                    }
                    else
                    {
                        Debug.Assert(*fieldGcPtrs == (byte)gcType);
                    }
                }
            }

            return result;
        }

        private uint getClassGClayout(CORINFO_CLASS_STRUCT_* cls, byte* gcPtrs)
        {
            uint result = 0;

            DefType type = (DefType)HandleToObject(cls);

            int pointerSize = PointerSize;

            int ptrsCount = AlignmentHelper.AlignUp(type.InstanceFieldSize.AsInt, pointerSize) / pointerSize;

            // Assume no GC pointers at first
            for (int i = 0; i < ptrsCount; i++)
                gcPtrs[i] = (byte)CorInfoGCType.TYPE_GC_NONE;

            if (type.ContainsGCPointers || type.IsByRefLike)
            {
                result = (uint)GatherClassGCLayout(type, gcPtrs);
            }
            return result;
        }

        private uint getClassNumInstanceFields(CORINFO_CLASS_STRUCT_* cls)
        {
            TypeDesc type = HandleToObject(cls);

            uint result = 0;
            foreach (var field in type.GetFields())
            {
                if (!field.IsStatic)
                    result++;
            }

            return result;
        }

        private CORINFO_FIELD_STRUCT_* getFieldInClass(CORINFO_CLASS_STRUCT_* clsHnd, int num)
        {
            TypeDesc classWithFields = HandleToObject(clsHnd);

            int iCurrentFoundField = -1;
            foreach (var field in classWithFields.GetFields())
            {
                if (field.IsStatic)
                    continue;

                ++iCurrentFoundField;
                if (iCurrentFoundField == num)
                {
                    return ObjectToHandle(field);
                }
            }

            // We could not find the field that was searched for.
            throw new InvalidOperationException();
        }

        private bool checkMethodModifier(CORINFO_METHOD_STRUCT_* hMethod, byte* modifier, bool fOptional)
        { throw new NotImplementedException("checkMethodModifier"); }

        private CorInfoHelpFunc getSharedCCtorHelper(CORINFO_CLASS_STRUCT_* clsHnd)
        { throw new NotImplementedException("getSharedCCtorHelper"); }
        private CorInfoHelpFunc getSecurityPrologHelper(CORINFO_METHOD_STRUCT_* ftn)
        { throw new NotImplementedException("getSecurityPrologHelper"); }

        private CORINFO_CLASS_STRUCT_* getTypeForBox(CORINFO_CLASS_STRUCT_* cls)
        {
            var type = HandleToObject(cls);

            var typeForBox = type.IsNullable ? type.Instantiation[0] : type;

            return ObjectToHandle(typeForBox);
        }

        private CorInfoHelpFunc getBoxHelper(CORINFO_CLASS_STRUCT_* cls)
        {
            var type = HandleToObject(cls);

            // we shouldn't allow boxing of types that contains stack pointers
            // csc and vbc already disallow it.
            if (type.IsByRefLike)
                ThrowHelper.ThrowInvalidProgramException(ExceptionStringID.InvalidProgramSpecific, MethodBeingCompiled);

            return type.IsNullable ? CorInfoHelpFunc.CORINFO_HELP_BOX_NULLABLE : CorInfoHelpFunc.CORINFO_HELP_BOX;
        }

        private CorInfoHelpFunc getUnBoxHelper(CORINFO_CLASS_STRUCT_* cls)
        {
            var type = HandleToObject(cls);

            return type.IsNullable ? CorInfoHelpFunc.CORINFO_HELP_UNBOX_NULLABLE : CorInfoHelpFunc.CORINFO_HELP_UNBOX;
        }

        private byte* getHelperName(CorInfoHelpFunc helpFunc)
        {
            return (byte*)GetPin(StringToUTF8(helpFunc.ToString()));
        }

        private CorInfoInitClassResult initClass(CORINFO_FIELD_STRUCT_* field, CORINFO_METHOD_STRUCT_* method, CORINFO_CONTEXT_STRUCT* context, bool speculative)
        {
            FieldDesc fd = field == null ? null : HandleToObject(field);
            Debug.Assert(fd == null || fd.IsStatic);

            MethodDesc md = HandleToObject(method);
            TypeDesc type = fd != null ? fd.OwningType : typeFromContext(context);

            if (_isFallbackBodyCompilation ||
#if READYTORUN
                IsClassPreInited(type)
#else
                !_compilation.HasLazyStaticConstructor(type)
#endif
                )
            {
                return CorInfoInitClassResult.CORINFO_INITCLASS_NOT_REQUIRED;
            }

            MetadataType typeToInit = (MetadataType)type;

            if (fd == null)
            {
                if (typeToInit.IsBeforeFieldInit)
                {
                    // We can wait for field accesses to run .cctor
                    return CorInfoInitClassResult.CORINFO_INITCLASS_NOT_REQUIRED;
                }

                // Run .cctor on statics & constructors
                if (md.Signature.IsStatic)
                {
                    // Except don't class construct on .cctor - it would be circular
                    if (md.IsStaticConstructor)
                    {
                        return CorInfoInitClassResult.CORINFO_INITCLASS_NOT_REQUIRED;
                    }
                }
                else if (!md.IsConstructor && !typeToInit.IsValueType)
                {
                    // According to the spec, we should be able to do this optimization for both reference and valuetypes.
                    // To maintain backward compatibility, we are doing it for reference types only.
                    // For instance methods of types with precise-initialization
                    // semantics, we can assume that the .ctor triggerred the
                    // type initialization.
                    // This does not hold for NULL "this" object. However, the spec does
                    // not require that case to work.
                    return CorInfoInitClassResult.CORINFO_INITCLASS_NOT_REQUIRED;
                }
            }

            if (typeToInit.IsCanonicalSubtype(CanonicalFormKind.Any))
            {
                // Shared generic code has to use helper. Moreover, tell JIT not to inline since
                // inlining of generic dictionary lookups is not supported.
                return CorInfoInitClassResult.CORINFO_INITCLASS_USE_HELPER | CorInfoInitClassResult.CORINFO_INITCLASS_DONT_INLINE;
            }

            //
            // Try to prove that the initialization is not necessary because of nesting
            //

            if (fd == null)
            {
                // Handled above
                Debug.Assert(!typeToInit.IsBeforeFieldInit);

                // Note that jit has both methods the same if asking whether to emit cctor
                // for a given method's code (as opposed to inlining codegen).
                MethodDesc contextMethod = methodFromContext(context);
                if (contextMethod != MethodBeingCompiled && typeToInit == MethodBeingCompiled.OwningType)
                {
                    // If we're inling a call to a method in our own type, then we should already
                    // have triggered the .cctor when caller was itself called.
                    return CorInfoInitClassResult.CORINFO_INITCLASS_NOT_REQUIRED;
                }
            }
            else
            {
                // This optimization may cause static fields in reference types to be accessed without cctor being triggered
                // for NULL "this" object. It does not conform with what the spec says. However, we have been historically 
                // doing it for perf reasons.
                if (!typeToInit.IsValueType && !typeToInit.IsBeforeFieldInit)
                {
                    if (typeToInit == typeFromContext(context) || typeToInit == MethodBeingCompiled.OwningType)
                    {
                        // The class will be initialized by the time we access the field.
                        return CorInfoInitClassResult.CORINFO_INITCLASS_NOT_REQUIRED;
                    }
                }

                // If we are currently compiling the class constructor for this static field access then we can skip the initClass 
                if (MethodBeingCompiled.OwningType == typeToInit && MethodBeingCompiled.IsStaticConstructor)
                {
                    // The class will be initialized by the time we access the field.
                    return CorInfoInitClassResult.CORINFO_INITCLASS_NOT_REQUIRED;
                }
            }

            return CorInfoInitClassResult.CORINFO_INITCLASS_USE_HELPER;
        }

        private void classMustBeLoadedBeforeCodeIsRun(CORINFO_CLASS_STRUCT_* cls)
        {
        }

        private CORINFO_CLASS_STRUCT_* getBuiltinClass(CorInfoClassId classId)
        {
            switch (classId)
            {
                case CorInfoClassId.CLASSID_SYSTEM_OBJECT:
                    return ObjectToHandle(_compilation.TypeSystemContext.GetWellKnownType(WellKnownType.Object));

                case CorInfoClassId.CLASSID_TYPED_BYREF:
                    return ObjectToHandle(_compilation.TypeSystemContext.GetWellKnownType(WellKnownType.TypedReference));

                case CorInfoClassId.CLASSID_TYPE_HANDLE:
                    return ObjectToHandle(_compilation.TypeSystemContext.GetWellKnownType(WellKnownType.RuntimeTypeHandle));

                case CorInfoClassId.CLASSID_FIELD_HANDLE:
                    return ObjectToHandle(_compilation.TypeSystemContext.GetWellKnownType(WellKnownType.RuntimeFieldHandle));

                case CorInfoClassId.CLASSID_METHOD_HANDLE:
                    return ObjectToHandle(_compilation.TypeSystemContext.GetWellKnownType(WellKnownType.RuntimeMethodHandle));

                case CorInfoClassId.CLASSID_ARGUMENT_HANDLE:
                    ThrowHelper.ThrowTypeLoadException("System", "RuntimeArgumentHandle", _compilation.TypeSystemContext.SystemModule);
                    return null;

                case CorInfoClassId.CLASSID_STRING:
                    return ObjectToHandle(_compilation.TypeSystemContext.GetWellKnownType(WellKnownType.String));

                case CorInfoClassId.CLASSID_RUNTIME_TYPE:
                    TypeDesc typeOfRuntimeType = _compilation.GetTypeOfRuntimeType();
                    return typeOfRuntimeType != null ? ObjectToHandle(typeOfRuntimeType) : null;

                default:
                    throw new NotImplementedException();
            }
        }

        private CorInfoType getTypeForPrimitiveValueClass(CORINFO_CLASS_STRUCT_* cls)
        {
            var type = HandleToObject(cls);

            if (!type.IsPrimitive && !type.IsEnum)
                return CorInfoType.CORINFO_TYPE_UNDEF;

            return asCorInfoType(type);
        }

        private CorInfoType getTypeForPrimitiveNumericClass(CORINFO_CLASS_STRUCT_* cls)
        {
            var type = HandleToObject(cls);

            switch (type.Category)
            {
                case TypeFlags.Byte:
                case TypeFlags.SByte:
                case TypeFlags.UInt16:
                case TypeFlags.Int16:
                case TypeFlags.UInt32:
                case TypeFlags.Int32:
                case TypeFlags.UInt64:
                case TypeFlags.Int64:
                case TypeFlags.Single:
                case TypeFlags.Double:
                    return asCorInfoType(type);

                default:
                    return CorInfoType.CORINFO_TYPE_UNDEF;
            }
        }

        private bool canCast(CORINFO_CLASS_STRUCT_* child, CORINFO_CLASS_STRUCT_* parent)
        { throw new NotImplementedException("canCast"); }
        private bool areTypesEquivalent(CORINFO_CLASS_STRUCT_* cls1, CORINFO_CLASS_STRUCT_* cls2)
        { throw new NotImplementedException("areTypesEquivalent"); }

        private TypeCompareState compareTypesForCast(CORINFO_CLASS_STRUCT_* fromClass, CORINFO_CLASS_STRUCT_* toClass)
        {
            TypeDesc fromType = HandleToObject(fromClass);
            TypeDesc toType = HandleToObject(toClass);

            TypeCompareState result = TypeCompareState.May;

            if (toType.IsNullable)
            {
                // If casting to Nullable<T>, don't try to optimize
                result = TypeCompareState.May;
            }
            else if (!fromType.IsCanonicalSubtype(CanonicalFormKind.Any) && !toType.IsCanonicalSubtype(CanonicalFormKind.Any))
            {
                // If the types are not shared, we can check directly.
                if (fromType.CanCastTo(toType))
                    result = TypeCompareState.Must;
                else
                    result = TypeCompareState.MustNot;
            }
            else if (fromType.IsCanonicalSubtype(CanonicalFormKind.Any) && !toType.IsCanonicalSubtype(CanonicalFormKind.Any))
            {
                // Casting from a shared type to an unshared type.
                // Only handle casts to interface types for now
                if (toType.IsInterface)
                {
                    // Do a preliminary check.
                    bool canCast = fromType.CanCastTo(toType);

                    // Pass back positive results unfiltered. The unknown type
                    // parameters in fromClass did not come into play.
                    if (canCast)
                    {
                        result = TypeCompareState.Must;
                    }
                    // For negative results, the unknown type parameter in
                    // fromClass might match some instantiated interface,
                    // either directly or via variance.
                    //
                    // However, CanCastTo will report failure in such cases since
                    // __Canon won't match the instantiated type on the
                    // interface (which can't be __Canon since we screened out
                    // canonical subtypes for toClass above). So only report
                    // failure if the interface is not instantiated.
                    else if (!toType.HasInstantiation)
                    {
                        result = TypeCompareState.MustNot;
                    }
                }
            }

#if READYTORUN
            // In R2R it is a breaking change for a previously positive
            // cast to become negative, but not for a previously negative
            // cast to become positive. So in R2R a negative result is
            // always reported back as May.
            if (result == TypeCompareState.MustNot)
            {
                result = TypeCompareState.May;
            }
#endif

            return result;
        }

        private TypeCompareState compareTypesForEquality(CORINFO_CLASS_STRUCT_* cls1, CORINFO_CLASS_STRUCT_* cls2)
        {
            TypeCompareState result = TypeCompareState.May;

            TypeDesc type1 = HandleToObject(cls1);
            TypeDesc type2 = HandleToObject(cls2);

            // If neither type is a canonical subtype, type handle comparison suffices
            if (!type1.IsCanonicalSubtype(CanonicalFormKind.Any) && !type2.IsCanonicalSubtype(CanonicalFormKind.Any))
            {
                result = (type1 == type2 ? TypeCompareState.Must : TypeCompareState.MustNot);
            }
            // If either or both types are canonical subtypes, we can sometimes prove inequality.
            else
            {
                // If either is a value type then the types cannot
                // be equal unless the type defs are the same.
                if (type1.IsValueType || type2.IsValueType)
                {
                    if (!type1.IsCanonicalDefinitionType(CanonicalFormKind.Universal) && !type2.IsCanonicalDefinitionType(CanonicalFormKind.Universal))
                    {
                        if (!type1.HasSameTypeDefinition(type2))
                        {
                            result = TypeCompareState.MustNot;
                        }
                    }
                }
                // If we have two ref types that are not __Canon, then the
                // types cannot be equal unless the type defs are the same.
                else
                {
                    if (!type1.IsCanonicalDefinitionType(CanonicalFormKind.Any) && !type2.IsCanonicalDefinitionType(CanonicalFormKind.Any))
                    {
                        if (!type1.HasSameTypeDefinition(type2))
                        {
                            result = TypeCompareState.MustNot;
                        }
                    }
                }
            }

            return result;
        }

        private CORINFO_CLASS_STRUCT_* mergeClasses(CORINFO_CLASS_STRUCT_* cls1, CORINFO_CLASS_STRUCT_* cls2)
        {
            TypeDesc type1 = HandleToObject(cls1);
            TypeDesc type2 = HandleToObject(cls2);

            TypeDesc merged = TypeExtensions.MergeTypesToCommonParent(type1, type2);

#if DEBUG
            // Make sure the merge is reflexive in the cases we "support".
            TypeDesc reflexive = TypeExtensions.MergeTypesToCommonParent(type2, type1);

            // If both sides are classes than either they have a common non-interface parent (in which case it is
            // reflexive)
            // OR they share a common interface, and it can be order dependent (if they share multiple interfaces
            // in common)
            if (!type1.IsInterface && !type2.IsInterface)
            {
                if (merged.IsInterface)
                {
                    Debug.Assert(reflexive.IsInterface);
                }
                else
                {
                    Debug.Assert(merged == reflexive);
                }
            }
            // Both results must either be interfaces or classes.  They cannot be mixed.
            Debug.Assert(merged.IsInterface == reflexive.IsInterface);

            // If the result of the merge was a class, then the result of the reflexive merge was the same class.
            if (!merged.IsInterface)
            {
                Debug.Assert(merged == reflexive);
            }

            // If both sides are arrays, then the result is either an array or g_pArrayClass.  The above is
            // actually true about the element type for references types, but I think that that is a little
            // excessive for sanity.
            if (type1.IsArray && type2.IsArray)
            {
                TypeDesc arrayClass = _compilation.TypeSystemContext.GetWellKnownType(WellKnownType.Array);
                Debug.Assert((merged.IsArray && reflexive.IsArray)
                         || ((merged == arrayClass) && (reflexive == arrayClass)));
            }

            // The results must always be assignable
            Debug.Assert(type1.CanCastTo(merged) && type2.CanCastTo(merged) && type1.CanCastTo(reflexive)
                     && type2.CanCastTo(reflexive));
#endif

            return ObjectToHandle(merged);
        }

        private bool isMoreSpecificType(CORINFO_CLASS_STRUCT_* cls1, CORINFO_CLASS_STRUCT_* cls2)
        {
            TypeDesc type1 = HandleToObject(cls1);
            TypeDesc type2 = HandleToObject(cls2);

            // If we have a mixture of shared and unshared types,
            // consider the unshared type as more specific.
            bool isType1CanonSubtype = type1.IsCanonicalSubtype(CanonicalFormKind.Any);
            bool isType2CanonSubtype = type2.IsCanonicalSubtype(CanonicalFormKind.Any);
            if (isType1CanonSubtype != isType2CanonSubtype)
            {
                // Only one of type1 and type2 is shared.
                // type2 is more specific if type1 is the shared type.
                return isType1CanonSubtype;
            }

            // Otherwise both types are either shared or not shared.
            // Look for a common parent type.
            TypeDesc merged = TypeExtensions.MergeTypesToCommonParent(type1, type2);

            // If the common parent is type1, then type2 is more specific.
            return merged == type1;
        }

        private CORINFO_CLASS_STRUCT_* getParentType(CORINFO_CLASS_STRUCT_* cls)
        { throw new NotImplementedException("getParentType"); }

        private CorInfoType getChildType(CORINFO_CLASS_STRUCT_* clsHnd, CORINFO_CLASS_STRUCT_** clsRet)
        {
            CorInfoType result = CorInfoType.CORINFO_TYPE_UNDEF;

            var td = HandleToObject(clsHnd);
            if (td.IsArray || td.IsByRef)
            {
                TypeDesc returnType = ((ParameterizedType)td).ParameterType;
                result = asCorInfoType(returnType, clsRet);
            }
            else
                clsRet = null;

            return result;
        }

        private bool satisfiesClassConstraints(CORINFO_CLASS_STRUCT_* cls)
        { throw new NotImplementedException("satisfiesClassConstraints"); }

        private bool isSDArray(CORINFO_CLASS_STRUCT_* cls)
        {
            var td = HandleToObject(cls);
            return td.IsSzArray;
        }

        private uint getArrayRank(CORINFO_CLASS_STRUCT_* cls)
        {
            var td = HandleToObject(cls) as ArrayType;
            Debug.Assert(td != null);
            return (uint) td.Rank;
        }

        private void* getArrayInitializationData(CORINFO_FIELD_STRUCT_* field, uint size)
        {
            var fd = HandleToObject(field);

            // Check for invalid arguments passed to InitializeArray intrinsic
            if (!fd.HasRva ||
                size > fd.FieldType.GetElementSize().AsInt)
            {
                return null;
            }

            return (void*)ObjectToHandle(_compilation.GetFieldRvaData(fd));
        }

        private CorInfoIsAccessAllowedResult canAccessClass(ref CORINFO_RESOLVED_TOKEN pResolvedToken, CORINFO_METHOD_STRUCT_* callerHandle, ref CORINFO_HELPER_DESC pAccessHelper)
        {
            // TODO: Access check
            return CorInfoIsAccessAllowedResult.CORINFO_ACCESS_ALLOWED;
        }

        private byte* getFieldName(CORINFO_FIELD_STRUCT_* ftn, byte** moduleName)
        {
            var field = HandleToObject(ftn);
            if (moduleName != null)
            {
                MetadataType typeDef = field.OwningType.GetTypeDefinition() as MetadataType;
                if (typeDef != null)
                    *moduleName = (byte*)GetPin(StringToUTF8(typeDef.GetFullName()));
                else
                    *moduleName = (byte*)GetPin(StringToUTF8("unknown"));
            }

            return (byte*)GetPin(StringToUTF8(field.Name));
        }

        private CORINFO_CLASS_STRUCT_* getFieldClass(CORINFO_FIELD_STRUCT_* field)
        {
            var fieldDesc = HandleToObject(field);
            return ObjectToHandle(fieldDesc.OwningType);
        }

        private CorInfoType getFieldType(CORINFO_FIELD_STRUCT_* field, CORINFO_CLASS_STRUCT_** structType, CORINFO_CLASS_STRUCT_* memberParent)
        {
            FieldDesc fieldDesc = HandleToObject(field);
            TypeDesc fieldType = fieldDesc.FieldType;

            CorInfoType type;
            if (structType != null)
            {
                type = asCorInfoType(fieldType, structType);
            }
            else
            {
                type = asCorInfoType(fieldType);
            }

            Debug.Assert(!fieldDesc.OwningType.IsByReferenceOfT ||
                fieldDesc.OwningType.GetKnownField("_value").FieldType.Category == TypeFlags.IntPtr);
            if (type == CorInfoType.CORINFO_TYPE_NATIVEINT && fieldDesc.OwningType.IsByReferenceOfT)
            {
                Debug.Assert(structType == null || *structType == null);
                Debug.Assert(fieldDesc.Offset.AsInt == 0);
                type = CorInfoType.CORINFO_TYPE_BYREF;
            }

            return type;
        }

        private uint getFieldOffset(CORINFO_FIELD_STRUCT_* field)
        {
            var fieldDesc = HandleToObject(field);

            Debug.Assert(fieldDesc.Offset != FieldAndOffset.InvalidOffset);

            return (uint)fieldDesc.Offset.AsInt;
        }

        private bool isWriteBarrierHelperRequired(CORINFO_FIELD_STRUCT_* field)
        { throw new NotImplementedException("isWriteBarrierHelperRequired"); }

        private CORINFO_FIELD_ACCESSOR getFieldIntrinsic(FieldDesc field)
        {
            Debug.Assert(field.IsIntrinsic);

            var owningType = field.OwningType;
            if ((owningType.IsWellKnownType(WellKnownType.IntPtr) ||
                    owningType.IsWellKnownType(WellKnownType.UIntPtr)) &&
                        field.Name == "Zero")
            {
                return CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_INTRINSIC_ZERO;
            }
            else if (owningType.IsString && field.Name == "Empty")
            {
                return CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_INTRINSIC_EMPTY_STRING;
            }
            else if (owningType.Name == "BitConverter" && owningType.Namespace == "System" &&
                field.Name == "IsLittleEndian")
            {
                return CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_INTRINSIC_ISLITTLEENDIAN;
            }

            return (CORINFO_FIELD_ACCESSOR)(-1);
        }

        private void getFieldInfo(ref CORINFO_RESOLVED_TOKEN pResolvedToken, CORINFO_METHOD_STRUCT_* callerHandle, CORINFO_ACCESS_FLAGS flags, CORINFO_FIELD_INFO* pResult)
        {
#if DEBUG
            // In debug, write some bogus data to the struct to ensure we have filled everything
            // properly.
            MemoryHelper.FillMemory((byte*)pResult, 0xcc, Marshal.SizeOf<CORINFO_FIELD_INFO>());
#endif

            Debug.Assert(((int)flags & ((int)CORINFO_ACCESS_FLAGS.CORINFO_ACCESS_GET |
                                        (int)CORINFO_ACCESS_FLAGS.CORINFO_ACCESS_SET |
                                        (int)CORINFO_ACCESS_FLAGS.CORINFO_ACCESS_ADDRESS |
                                        (int)CORINFO_ACCESS_FLAGS.CORINFO_ACCESS_INIT_ARRAY)) != 0);

            var field = HandleToObject(pResolvedToken.hField);
#if READYTORUN
            MethodDesc callerMethod = HandleToObject(callerHandle);

            if (field.Offset.IsIndeterminate)
                throw new RequiresRuntimeJitException(field);
#endif

            CORINFO_FIELD_ACCESSOR fieldAccessor;
            CORINFO_FIELD_FLAGS fieldFlags = (CORINFO_FIELD_FLAGS)0;
            uint fieldOffset = (field.IsStatic && field.HasRva ? 0xBAADF00D : (uint)field.Offset.AsInt);

            if (field.IsStatic)
            {
                fieldFlags |= CORINFO_FIELD_FLAGS.CORINFO_FLG_FIELD_STATIC;

#if READYTORUN
                if (field.FieldType.IsValueType && field.HasGCStaticBase && !field.HasRva)
                {
                    // statics of struct types are stored as implicitly boxed in CoreCLR i.e.
                    // we need to modify field access flags appropriately
                    fieldFlags |= CORINFO_FIELD_FLAGS.CORINFO_FLG_FIELD_STATIC_IN_HEAP;
                }
#endif

                if (field.HasRva)
                {
                    fieldFlags |= CORINFO_FIELD_FLAGS.CORINFO_FLG_FIELD_UNMANAGED;

                    // TODO: Handle the case when the RVA is in the TLS range
                    fieldAccessor = CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_STATIC_RVA_ADDRESS;

                    // We are not going through a helper. The constructor has to be triggered explicitly.
#if READYTORUN
                    if (!IsClassPreInited(field.OwningType))
#else
                    if (_compilation.HasLazyStaticConstructor(field.OwningType))
#endif
                    {
                        fieldFlags |= CORINFO_FIELD_FLAGS.CORINFO_FLG_FIELD_INITCLASS;
                    }
                }
                else if (field.OwningType.IsCanonicalSubtype(CanonicalFormKind.Any))
                {
                    // The JIT wants to know how to access a static field on a generic type. We need a runtime lookup.
#if READYTORUN
                    fieldAccessor = CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_STATIC_GENERICS_STATIC_HELPER;
                    if (field.IsThreadStatic)
                    {
                        pResult->helper = (field.HasGCStaticBase ?
                            CorInfoHelpFunc.CORINFO_HELP_GETGENERICS_GCTHREADSTATIC_BASE:
                            CorInfoHelpFunc.CORINFO_HELP_GETGENERICS_NONGCTHREADSTATIC_BASE);
                    }
                    else
                    {
                        pResult->helper = (field.HasGCStaticBase ?
                            CorInfoHelpFunc.CORINFO_HELP_GETGENERICS_GCSTATIC_BASE:
                            CorInfoHelpFunc.CORINFO_HELP_GETGENERICS_NONGCSTATIC_BASE);
                    }
#else
                    fieldAccessor = CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_STATIC_READYTORUN_HELPER;
                    pResult->helper = CorInfoHelpFunc.CORINFO_HELP_READYTORUN_GENERIC_STATIC_BASE;

                    // Don't try to compute the runtime lookup if we're inlining. The JIT is going to abort the inlining
                    // attempt anyway.
                    MethodDesc contextMethod = methodFromContext(pResolvedToken.tokenContext);
                    if (contextMethod == MethodBeingCompiled)
                    {
                        FieldDesc runtimeDeterminedField = (FieldDesc)GetRuntimeDeterminedObjectForToken(ref pResolvedToken);

                        ReadyToRunHelperId helperId;

                        // Find out what kind of base do we need to look up.
                        if (field.IsThreadStatic)
                        {
                            helperId = ReadyToRunHelperId.GetThreadStaticBase;
                        }
                        else if (field.HasGCStaticBase)
                        {
                            helperId = ReadyToRunHelperId.GetGCStaticBase;
                        }
                        else
                        {
                            helperId = ReadyToRunHelperId.GetNonGCStaticBase;
                        }

                        // What generic context do we look up the base from.
                        ISymbolNode helper;
                        if (contextMethod.AcquiresInstMethodTableFromThis() || contextMethod.RequiresInstMethodTableArg())
                        {
                            helper = _compilation.NodeFactory.ReadyToRunHelperFromTypeLookup(
                                helperId, runtimeDeterminedField.OwningType, contextMethod.OwningType);
                        }
                        else
                        {
                            Debug.Assert(contextMethod.RequiresInstMethodDescArg());
                            helper = _compilation.NodeFactory.ReadyToRunHelperFromDictionaryLookup(
                                helperId, runtimeDeterminedField.OwningType, contextMethod);
                        }

                        pResult->fieldLookup = CreateConstLookupToSymbol(helper);
                    }
#endif // READYTORUN
                }
                else
                {
                    fieldAccessor = CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_STATIC_SHARED_STATIC_HELPER;
                    pResult->helper = CorInfoHelpFunc.CORINFO_HELP_READYTORUN_STATIC_BASE;

                    ReadyToRunHelperId helperId = ReadyToRunHelperId.Invalid;
                    CORINFO_FIELD_ACCESSOR intrinsicAccessor;
                    if (field.IsIntrinsic &&
                        (flags & CORINFO_ACCESS_FLAGS.CORINFO_ACCESS_GET) != 0 &&
                        (intrinsicAccessor = getFieldIntrinsic(field)) != (CORINFO_FIELD_ACCESSOR)(-1))
                    {
                        fieldAccessor = intrinsicAccessor;
                    }
                    else if (field.IsThreadStatic)
                    {
#if READYTORUN
                        if (field.HasGCStaticBase)
                        {
                            helperId = ReadyToRunHelperId.GetThreadStaticBase;
                        }
                        else
                        {
                            helperId = ReadyToRunHelperId.GetThreadNonGcStaticBase;
                        }
#else
                        helperId = ReadyToRunHelperId.GetThreadStaticBase;
#endif
                    }
                    else if (field.HasGCStaticBase)
                    {
                        helperId = ReadyToRunHelperId.GetGCStaticBase;
                    }
                    else
                    {
                        helperId = ReadyToRunHelperId.GetNonGCStaticBase;
                    }

#if READYTORUN
                    if (!_compilation.NodeFactory.CompilationModuleGroup.VersionsWithType(field.OwningType) &&
                        fieldAccessor == CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_STATIC_SHARED_STATIC_HELPER)
                    {
                        PreventRecursiveFieldInlinesOutsideVersionBubble(field, callerMethod);
                        
                        // Static fields outside of the version bubble need to be accessed using the ENCODE_FIELD_ADDRESS
                        // helper in accordance with ZapInfo::getFieldInfo in CoreCLR.
                        pResult->fieldLookup = CreateConstLookupToSymbol(_compilation.SymbolNodeFactory.FieldAddress(field, GetSignatureContext()));

                        pResult->helper = CorInfoHelpFunc.CORINFO_HELP_READYTORUN_STATIC_BASE;

                        fieldFlags &= ~CORINFO_FIELD_FLAGS.CORINFO_FLG_FIELD_STATIC_IN_HEAP; // The dynamic helper takes care of the unboxing
                        fieldOffset = 0;
                    }
                    else
#endif

                    if (helperId != ReadyToRunHelperId.Invalid)
                    {
                        pResult->fieldLookup = CreateConstLookupToSymbol(
#if READYTORUN
                            _compilation.SymbolNodeFactory.ReadyToRunHelper(helperId, field.OwningType, GetSignatureContext())
#else
                            _compilation.NodeFactory.ReadyToRunHelper(helperId, field.OwningType)
#endif
                            );
                    }
                }
            }
            else
            {
                fieldAccessor = CORINFO_FIELD_ACCESSOR.CORINFO_FIELD_INSTANCE;
            }

            if (field.IsInitOnly)
                fieldFlags |= CORINFO_FIELD_FLAGS.CORINFO_FLG_FIELD_FINAL;

            pResult->fieldAccessor = fieldAccessor;
            pResult->fieldFlags = fieldFlags;
            pResult->fieldType = getFieldType(pResolvedToken.hField, &pResult->structType, pResolvedToken.hClass);
            pResult->accessAllowed = CorInfoIsAccessAllowedResult.CORINFO_ACCESS_ALLOWED;
            pResult->offset = fieldOffset;

#if READYTORUN
            EncodeFieldBaseOffset(field, pResult, callerMethod);
#endif

            // TODO: We need to implement access checks for fields and methods.  See JitInterface.cpp in mrtjit
            //       and STS::AccessCheck::CanAccess.
        }

        private bool isFieldStatic(CORINFO_FIELD_STRUCT_* fldHnd)
        {
            return HandleToObject(fldHnd).IsStatic;
        }

        private void getBoundaries(CORINFO_METHOD_STRUCT_* ftn, ref uint cILOffsets, ref uint* pILOffsets, BoundaryTypes* implicitBoundaries)
        {
            // TODO: Debugging
            cILOffsets = 0;
            pILOffsets = null;
            *implicitBoundaries = BoundaryTypes.DEFAULT_BOUNDARIES;
        }

        private void getVars(CORINFO_METHOD_STRUCT_* ftn, ref uint cVars, ILVarInfo** vars, ref bool extendOthers)
        {
            // TODO: Debugging

            cVars = 0;
            *vars = null;

            // Just tell the JIT to extend everything.
            extendOthers = true;
        }

        private void* allocateArray(uint cBytes)
        {
            return (void*)Marshal.AllocCoTaskMem((int)cBytes);
        }

        private void freeArray(void* array)
        {
            Marshal.FreeCoTaskMem((IntPtr)array);
        }

        private CORINFO_ARG_LIST_STRUCT_* getArgNext(CORINFO_ARG_LIST_STRUCT_* args)
        {
            return (CORINFO_ARG_LIST_STRUCT_*)((int)args + 1);
        }

        private CorInfoTypeWithMod getArgType(CORINFO_SIG_INFO* sig, CORINFO_ARG_LIST_STRUCT_* args, CORINFO_CLASS_STRUCT_** vcTypeRet)
        {
            int index = (int)args;
            Object sigObj = HandleToObject((IntPtr)sig->pSig);

            MethodSignature methodSig = sigObj as MethodSignature;

            if (methodSig != null)
            {
                TypeDesc type = methodSig[index];

                CorInfoType corInfoType = asCorInfoType(type, vcTypeRet);
                return (CorInfoTypeWithMod)corInfoType;
            }
            else
            {
                LocalVariableDefinition[] locals = (LocalVariableDefinition[])sigObj;
                TypeDesc type = locals[index].Type;

                CorInfoType corInfoType = asCorInfoType(type, vcTypeRet);

                return (CorInfoTypeWithMod)corInfoType | (locals[index].IsPinned ? CorInfoTypeWithMod.CORINFO_TYPE_MOD_PINNED : 0);
            }
        }

        private CORINFO_CLASS_STRUCT_* getArgClass(CORINFO_SIG_INFO* sig, CORINFO_ARG_LIST_STRUCT_* args)
        {
            int index = (int)args;
            Object sigObj = HandleToObject((IntPtr)sig->pSig);

            MethodSignature methodSig = sigObj as MethodSignature;
            if (methodSig != null)
            {
                TypeDesc type = methodSig[index];
                return ObjectToHandle(type);
            }
            else
            {
                LocalVariableDefinition[] locals = (LocalVariableDefinition[])sigObj;
                TypeDesc type = locals[index].Type;
                return ObjectToHandle(type);
            }
        }

        private CorInfoType getHFAType(CORINFO_CLASS_STRUCT_* hClass)
        {
            var type = (DefType)HandleToObject(hClass);
            return type.IsHfa ? asCorInfoType(type.HfaElementType) : CorInfoType.CORINFO_TYPE_UNDEF;
        }

        private HRESULT GetErrorHRESULT(_EXCEPTION_POINTERS* pExceptionPointers)
        { throw new NotImplementedException("GetErrorHRESULT"); }
        private uint GetErrorMessage(short* buffer, uint bufferLength)
        { throw new NotImplementedException("GetErrorMessage"); }

        private int FilterException(_EXCEPTION_POINTERS* pExceptionPointers)
        {
            // This method is completely handled by the C++ wrapper to the JIT-EE interface,
            // and should never reach the managed implementation.
            Debug.Fail("CorInfoImpl.FilterException should not be called");
            throw new NotSupportedException("FilterException");
        }

        private void HandleException(_EXCEPTION_POINTERS* pExceptionPointers)
        {
            // This method is completely handled by the C++ wrapper to the JIT-EE interface,
            // and should never reach the managed implementation.
            Debug.Fail("CorInfoImpl.HandleException should not be called");
            throw new NotSupportedException("HandleException");
        }

        private bool runWithErrorTrap(void* function, void* parameter)
        {
            // This method is completely handled by the C++ wrapper to the JIT-EE interface,
            // and should never reach the managed implementation.
            Debug.Fail("CorInfoImpl.runWithErrorTrap should not be called");
            throw new NotSupportedException("runWithErrorTrap");
        }

        private void ThrowExceptionForJitResult(HRESULT result)
        { throw new NotImplementedException("ThrowExceptionForJitResult"); }
        private void ThrowExceptionForHelper(ref CORINFO_HELPER_DESC throwHelper)
        { throw new NotImplementedException("ThrowExceptionForHelper"); }

        private uint SizeOfPInvokeTransitionFrame
        {
            get
            {
                // struct PInvokeTransitionFrame:
                // #ifdef _TARGET_ARM_
                //  m_ChainPointer
                // #endif
                //  m_RIP
                //  m_FramePointer
                //  m_pThread
                //  m_Flags + align (no align for ARM64 that has 64 bit m_Flags)
                //  m_PreserverRegs - RSP
                //      No need to save other preserved regs because of the JIT ensures that there are
                //      no live GC references in callee saved registers around the PInvoke callsite.
                int size = 5 * this.PointerSize;

                if (_compilation.TypeSystemContext.Target.Architecture == TargetArchitecture.ARM)
                    size += this.PointerSize; // m_ChainPointer

                return (uint)size;
            }
        }

        private void getEEInfo(ref CORINFO_EE_INFO pEEInfoOut)
        {
            pEEInfoOut = new CORINFO_EE_INFO();

#if DEBUG
            // In debug, write some bogus data to the struct to ensure we have filled everything
            // properly.
            fixed (CORINFO_EE_INFO* tmp = &pEEInfoOut)
                MemoryHelper.FillMemory((byte*)tmp, 0xcc, Marshal.SizeOf<CORINFO_EE_INFO>());
#endif

            int pointerSize = this.PointerSize;

            pEEInfoOut.inlinedCallFrameInfo.size = this.SizeOfPInvokeTransitionFrame;

            pEEInfoOut.offsetOfDelegateInstance = (uint)pointerSize;            // Delegate::m_firstParameter
            pEEInfoOut.offsetOfDelegateFirstTarget = OffsetOfDelegateFirstTarget;

            pEEInfoOut.offsetOfObjArrayData = (uint)(2 * pointerSize);

            pEEInfoOut.sizeOfReversePInvokeFrame = (uint)(2 * pointerSize);

            pEEInfoOut.osPageSize = new UIntPtr(0x1000);

            pEEInfoOut.maxUncheckedOffsetForNullObject = (_compilation.NodeFactory.Target.IsWindows) ?
                new UIntPtr(32 * 1024 - 1) : new UIntPtr((uint)pEEInfoOut.osPageSize / 2 - 1);

            pEEInfoOut.targetAbi = TargetABI;
        }

        private string getJitTimeLogFilename()
        {
            return null;
        }

        private mdToken getMethodDefFromMethod(CORINFO_METHOD_STRUCT_* hMethod)
        {
            MethodDesc method = HandleToObject(hMethod);
#if READYTORUN
            if (method is UnboxingMethodDesc unboxingMethodDesc)
            {
                method = unboxingMethodDesc.Target;
            }
#endif
            MethodDesc methodDefinition = method.GetTypicalMethodDefinition();

            // Need to cast down to EcmaMethod. Do not use this as a precedent that casting to Ecma*
            // within the JitInterface is fine. We might want to consider moving this to Compilation.
            TypeSystem.Ecma.EcmaMethod ecmaMethodDefinition = methodDefinition as TypeSystem.Ecma.EcmaMethod;
            if (ecmaMethodDefinition != null)
            {
                return (mdToken)System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(ecmaMethodDefinition.Handle);
            }

            return 0;
        }

        private static byte[] StringToUTF8(string s)
        {
            int byteCount = Encoding.UTF8.GetByteCount(s);
            byte[] bytes = new byte[byteCount + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }

        private byte* getMethodName(CORINFO_METHOD_STRUCT_* ftn, byte** moduleName)
        {
            MethodDesc method = HandleToObject(ftn);

            if (moduleName != null)
            {
                MetadataType typeDef = method.OwningType.GetTypeDefinition() as MetadataType;
                if (typeDef != null)
                    *moduleName = (byte*)GetPin(StringToUTF8(typeDef.GetFullName()));
                else
                    *moduleName = (byte*)GetPin(StringToUTF8("unknown"));
            }

            return (byte*)GetPin(StringToUTF8(method.Name));
        }

        private byte* getMethodNameFromMetadata(CORINFO_METHOD_STRUCT_* ftn, byte** className, byte** namespaceName, byte** enclosingClassName)
        {
            MethodDesc method = HandleToObject(ftn);

            string result = null;
            string classResult = null;
            string namespaceResult = null;
            string enclosingResult = null;

            result = method.Name;

            MetadataType owningType = method.OwningType as MetadataType;
            if (owningType != null)
            {
                classResult = owningType.Name;
                namespaceResult = owningType.Namespace;

                // Query enclosingClassName when the method is in a nested class
                // and get the namespace of enclosing classes (nested class's namespace is empty)
                var containingType = owningType.ContainingType;
                if (containingType != null)
                {
                    enclosingResult = containingType.Name;
                    namespaceResult = containingType.Namespace;
                }
            }
            
            if (className != null)
                *className = classResult != null ? (byte*)GetPin(StringToUTF8(classResult)) : null;
            if (namespaceName != null)
                *namespaceName = namespaceResult != null ? (byte*)GetPin(StringToUTF8(namespaceResult)) : null;
            if (enclosingClassName != null)
                *enclosingClassName = enclosingResult != null ? (byte*)GetPin(StringToUTF8(enclosingResult)) : null;

            return result != null ? (byte*)GetPin(StringToUTF8(result)) : null;
        }

        private uint getMethodHash(CORINFO_METHOD_STRUCT_* ftn)
        {
            return (uint)HandleToObject(ftn).GetHashCode();
        }

        private byte* findNameOfToken(CORINFO_MODULE_STRUCT_* moduleHandle, mdToken token, byte* szFQName, UIntPtr FQNameCapacity)
        { throw new NotImplementedException("findNameOfToken"); }

        SystemVStructClassificator _systemVStructClassificator = new SystemVStructClassificator();

        private bool getSystemVAmd64PassStructInRegisterDescriptor(CORINFO_CLASS_STRUCT_* structHnd, SYSTEMV_AMD64_CORINFO_STRUCT_REG_PASSING_DESCRIPTOR* structPassInRegDescPtr)
        {
            TypeDesc typeDesc = HandleToObject(structHnd);

            return _systemVStructClassificator.getSystemVAmd64PassStructInRegisterDescriptor(typeDesc, structPassInRegDescPtr);
        }

        private uint getThreadTLSIndex(ref void* ppIndirection)
        { throw new NotImplementedException("getThreadTLSIndex"); }
        private void* getInlinedCallFrameVptr(ref void* ppIndirection)
        { throw new NotImplementedException("getInlinedCallFrameVptr"); }
        private int* getAddrOfCaptureThreadGlobal(ref void* ppIndirection)
        { throw new NotImplementedException("getAddrOfCaptureThreadGlobal"); }

        private Dictionary<CorInfoHelpFunc, ISymbolNode> _helperCache = new Dictionary<CorInfoHelpFunc, ISymbolNode>();
        private void* getHelperFtn(CorInfoHelpFunc ftnNum, ref void* ppIndirection)
        {
            ISymbolNode entryPoint;
            if (!_helperCache.TryGetValue(ftnNum, out entryPoint))
            {
                entryPoint = GetHelperFtnUncached(ftnNum);
                _helperCache.Add(ftnNum, entryPoint);
            }
            if (entryPoint.RepresentsIndirectionCell)
            {
                ppIndirection = (void*)ObjectToHandle(entryPoint);
                return null;
            }
            else
            {
                ppIndirection = null;
                return (void*)ObjectToHandle(entryPoint);
            }
        }

        private void getFunctionFixedEntryPoint(CORINFO_METHOD_STRUCT_* ftn, ref CORINFO_CONST_LOOKUP pResult)
        { throw new NotImplementedException("getFunctionFixedEntryPoint"); }

        private CorInfoHelpFunc getLazyStringLiteralHelper(CORINFO_MODULE_STRUCT_* handle)
        {
            // TODO: Lazy string literal helper
            return CorInfoHelpFunc.CORINFO_HELP_UNDEF;
        }

        private CORINFO_MODULE_STRUCT_* embedModuleHandle(CORINFO_MODULE_STRUCT_* handle, ref void* ppIndirection)
        { throw new NotImplementedException("embedModuleHandle"); }
        private CORINFO_CLASS_STRUCT_* embedClassHandle(CORINFO_CLASS_STRUCT_* handle, ref void* ppIndirection)
        { throw new NotImplementedException("embedClassHandle"); }

        private CORINFO_FIELD_STRUCT_* embedFieldHandle(CORINFO_FIELD_STRUCT_* handle, ref void* ppIndirection)
        { throw new NotImplementedException("embedFieldHandle"); }

        private CORINFO_RUNTIME_LOOKUP_KIND GetGenericRuntimeLookupKind(MethodDesc method)
        {
            if (method.RequiresInstMethodDescArg())
                return CORINFO_RUNTIME_LOOKUP_KIND.CORINFO_LOOKUP_METHODPARAM;
            else if (method.RequiresInstMethodTableArg())
                return CORINFO_RUNTIME_LOOKUP_KIND.CORINFO_LOOKUP_CLASSPARAM;
            else
            {
                Debug.Assert(method.AcquiresInstMethodTableFromThis());
                return CORINFO_RUNTIME_LOOKUP_KIND.CORINFO_LOOKUP_THISOBJ;
            }
        }

        private void getLocationOfThisType(out CORINFO_LOOKUP_KIND result, CORINFO_METHOD_STRUCT_* context)
        {
            result = new CORINFO_LOOKUP_KIND();

            MethodDesc method = HandleToObject(context);

            if (method.IsSharedByGenericInstantiations)
            {
                result.needsRuntimeLookup = true;
                result.runtimeLookupKind = GetGenericRuntimeLookupKind(method);
            }
            else
            {
                result.needsRuntimeLookup = false;
                result.runtimeLookupKind = CORINFO_RUNTIME_LOOKUP_KIND.CORINFO_LOOKUP_THISOBJ;
            }
        }

        private void* getPInvokeUnmanagedTarget(CORINFO_METHOD_STRUCT_* method, ref void* ppIndirection)
        { throw new NotImplementedException("getPInvokeUnmanagedTarget"); }
        private void* getAddressOfPInvokeFixup(CORINFO_METHOD_STRUCT_* method, ref void* ppIndirection)
        { throw new NotImplementedException("getAddressOfPInvokeFixup"); }
        private void* GetCookieForPInvokeCalliSig(CORINFO_SIG_INFO* szMetaSig, ref void* ppIndirection)
        { throw new NotImplementedException("GetCookieForPInvokeCalliSig"); }
        private bool canGetCookieForPInvokeCalliSig(CORINFO_SIG_INFO* szMetaSig)
        { throw new NotImplementedException("canGetCookieForPInvokeCalliSig"); }
        private CORINFO_JUST_MY_CODE_HANDLE_* getJustMyCodeHandle(CORINFO_METHOD_STRUCT_* method, ref CORINFO_JUST_MY_CODE_HANDLE_* ppIndirection)
        {
            ppIndirection = null;
            return null;
        }
        private void GetProfilingHandle(ref bool pbHookFunction, ref void* pProfilerHandle, ref bool pbIndirectedHandles)
        { throw new NotImplementedException("GetProfilingHandle"); }

        /// <summary>
        /// Create a CORINFO_CONST_LOOKUP to a symbol and put the address into the addr field
        /// </summary>
        private CORINFO_CONST_LOOKUP CreateConstLookupToSymbol(ISymbolNode symbol)
        {
            CORINFO_CONST_LOOKUP constLookup = new CORINFO_CONST_LOOKUP();
            constLookup.addr = (void*)ObjectToHandle(symbol);
            constLookup.accessType = symbol.RepresentsIndirectionCell ? InfoAccessType.IAT_PVALUE : InfoAccessType.IAT_VALUE;
            return constLookup;
        }

        private bool canAccessFamily(CORINFO_METHOD_STRUCT_* hCaller, CORINFO_CLASS_STRUCT_* hInstanceType)
        { throw new NotImplementedException("canAccessFamily"); }
        private bool isRIDClassDomainID(CORINFO_CLASS_STRUCT_* cls)
        { throw new NotImplementedException("isRIDClassDomainID"); }
        private uint getClassDomainID(CORINFO_CLASS_STRUCT_* cls, ref void* ppIndirection)
        { throw new NotImplementedException("getClassDomainID"); }

        private void* getFieldAddress(CORINFO_FIELD_STRUCT_* field, void** ppIndirection)
        {
            FieldDesc fieldDesc = HandleToObject(field);
            Debug.Assert(fieldDesc.HasRva);
            ISymbolNode node = _compilation.GetFieldRvaData(fieldDesc);
            void *handle = (void *)ObjectToHandle(node);
            if (node.RepresentsIndirectionCell)
            {
                *ppIndirection = handle;
                return null;
            }
            else
            {
                if (ppIndirection != null)
                    *ppIndirection = null;
                return handle;
            }
        }

        private CORINFO_CLASS_STRUCT_* getStaticFieldCurrentClass(CORINFO_FIELD_STRUCT_* field, byte* pIsSpeculative)
        {
            if (pIsSpeculative != null)
                *pIsSpeculative = 1;

            return null;
        }

        private IntPtr getVarArgsHandle(CORINFO_SIG_INFO* pSig, ref void* ppIndirection)
        { throw new NotImplementedException("getVarArgsHandle"); }
        private bool canGetVarArgsHandle(CORINFO_SIG_INFO* pSig)
        { throw new NotImplementedException("canGetVarArgsHandle"); }

        private InfoAccessType emptyStringLiteral(ref void* ppValue)
        {
            return constructStringLiteral(_methodScope, (mdToken)CorTokenType.mdtString, ref ppValue);
        }

        private uint getFieldThreadLocalStoreID(CORINFO_FIELD_STRUCT_* field, ref void* ppIndirection)
        { throw new NotImplementedException("getFieldThreadLocalStoreID"); }
        private void setOverride(IntPtr pOverride, CORINFO_METHOD_STRUCT_* currentMethod)
        { throw new NotImplementedException("setOverride"); }
        private void addActiveDependency(CORINFO_MODULE_STRUCT_* moduleFrom, CORINFO_MODULE_STRUCT_* moduleTo)
        { throw new NotImplementedException("addActiveDependency"); }
        private CORINFO_METHOD_STRUCT_* GetDelegateCtor(CORINFO_METHOD_STRUCT_* methHnd, CORINFO_CLASS_STRUCT_* clsHnd, CORINFO_METHOD_STRUCT_* targetMethodHnd, ref DelegateCtorArgs pCtorData)
        { throw new NotImplementedException("GetDelegateCtor"); }
        private void MethodCompileComplete(CORINFO_METHOD_STRUCT_* methHnd)
        { throw new NotImplementedException("MethodCompileComplete"); }

        private void* getTailCallCopyArgsThunk(CORINFO_SIG_INFO* pSig, CorInfoHelperTailCallSpecialHandling flags)
        {
            // Slow tailcalls are not supported yet
            // https://github.com/dotnet/corert/issues/1683
            return null;
        }

        private void* getMemoryManager()
        {
            // This method is completely handled by the C++ wrapper to the JIT-EE interface,
            // and should never reach the managed implementation.
            Debug.Fail("CorInfoImpl.getMemoryManager should not be called");
            throw new NotSupportedException("getMemoryManager");
        }

        private byte[] _code;
        private byte[] _coldCode;

        private byte[] _roData;

        private BlobNode _roDataBlob;

        private int _numFrameInfos;
        private int _usedFrameInfos;
        private FrameInfo[] _frameInfos;

        private byte[] _gcInfo;
        private CORINFO_EH_CLAUSE[] _ehClauses;

        private void allocMem(uint hotCodeSize, uint coldCodeSize, uint roDataSize, uint xcptnsCount, CorJitAllocMemFlag flag, ref void* hotCodeBlock, ref void* coldCodeBlock, ref void* roDataBlock)
        {
            hotCodeBlock = (void*)GetPin(_code = new byte[hotCodeSize]);

            if (coldCodeSize != 0)
                coldCodeBlock = (void*)GetPin(_coldCode = new byte[coldCodeSize]);

            if (roDataSize != 0)
            {
                int alignment = 8;

                if ((flag & CorJitAllocMemFlag.CORJIT_ALLOCMEM_FLG_RODATA_16BYTE_ALIGN) != 0)
                {
                    alignment = 16;
                }
                else if (roDataSize < 8)
                {
                    alignment = PointerSize;
                }

                _roData = new byte[roDataSize];

                _roDataBlob = _compilation.NodeFactory.ReadOnlyDataBlob(
                    "__readonlydata_" + _compilation.NameMangler.GetMangledMethodName(MethodBeingCompiled),
                    _roData, alignment);

                roDataBlock = (void*)GetPin(_roData);
            }

            if (_numFrameInfos > 0)
            {
                _frameInfos = new FrameInfo[_numFrameInfos];
            }
        }

        private void reserveUnwindInfo(bool isFunclet, bool isColdCode, uint unwindSize)
        {
            _numFrameInfos++;
        }

        private void allocUnwindInfo(byte* pHotCode, byte* pColdCode, uint startOffset, uint endOffset, uint unwindSize, byte* pUnwindBlock, CorJitFuncKind funcKind)
        {
            Debug.Assert(FrameInfoFlags.Filter == (FrameInfoFlags)CorJitFuncKind.CORJIT_FUNC_FILTER);
            Debug.Assert(FrameInfoFlags.Handler == (FrameInfoFlags)CorJitFuncKind.CORJIT_FUNC_HANDLER);

            FrameInfoFlags flags = (FrameInfoFlags)funcKind;

            if (funcKind == CorJitFuncKind.CORJIT_FUNC_ROOT)
            {
                if (this.MethodBeingCompiled.IsNativeCallable)
                    flags |= FrameInfoFlags.ReversePInvoke;
            }

            byte[] blobData = new byte[unwindSize];

            for (uint i = 0; i < unwindSize; i++)
            {
                blobData[i] = pUnwindBlock[i];
            }

            _frameInfos[_usedFrameInfos++] = new FrameInfo(flags, (int)startOffset, (int)endOffset, blobData);
        }

        private void* allocGCInfo(UIntPtr size)
        {
            _gcInfo = new byte[(int)size];
            return (void*)GetPin(_gcInfo);
        }

        private void yieldExecution()
        {
            // Nothing to do
        }

        private void setEHcount(uint cEH)
        {
            _ehClauses = new CORINFO_EH_CLAUSE[cEH];
        }

        private void setEHinfo(uint EHnumber, ref CORINFO_EH_CLAUSE clause)
        {
            _ehClauses[EHnumber] = clause;
        }

        private bool logMsg(uint level, byte* fmt, IntPtr args)
        {
            // Console.WriteLine(Marshal.PtrToStringAnsi((IntPtr)fmt));
            return false;
        }

        private int doAssert(byte* szFile, int iLine, byte* szExpr)
        {
            Log.WriteLine(Marshal.PtrToStringAnsi((IntPtr)szFile) + ":" + iLine);
            Log.WriteLine(Marshal.PtrToStringAnsi((IntPtr)szExpr));

            return 1;
        }

        private void reportFatalError(CorJitResult result)
        {
            // We could add some logging here, but for now it's unnecessary.
            // CompileMethod is going to fail with this CorJitResult anyway.
        }

        private void recordCallSite(uint instrOffset, CORINFO_SIG_INFO* callSig, CORINFO_METHOD_STRUCT_* methodHandle)
        {
        }

        private ArrayBuilder<Relocation> _relocs;

        /// <summary>
        /// Various type of block.
        /// </summary>
        public enum BlockType : sbyte
        {
            /// <summary>Not a generated block.</summary>
            Unknown = -1,
            /// <summary>Represent code.</summary>
            Code = 0,
            /// <summary>Represent cold code (i.e. code not called frequently).</summary>
            ColdCode = 1,
            /// <summary>Read-only data.</summary>
            ROData = 2,
            /// <summary>Instrumented Block Count Data</summary>
            BBCounts = 3
        }

        private BlockType findKnownBlock(void* location, out int offset)
        {
            fixed (byte* pCode = _code)
            {
                if (pCode <= (byte*)location && (byte*)location < pCode + _code.Length)
                {
                    offset = (int)((byte*)location - pCode);
                    return BlockType.Code;
                }
            }

            if (_coldCode != null)
            {
                fixed (byte* pColdCode = _coldCode)
                {
                    if (pColdCode <= (byte*)location && (byte*)location < pColdCode + _coldCode.Length)
                    {
                        offset = (int)((byte*)location - pColdCode);
                        return BlockType.ColdCode;
                    }
                }
            }

            if (_roData != null)
            {
                fixed (byte* pROData = _roData)
                {
                    if (pROData <= (byte*)location && (byte*)location < pROData + _roData.Length)
                    {
                        offset = (int)((byte*)location - pROData);
                        return BlockType.ROData;
                    }
                }
            }

            {
                BlockType retBlockType = BlockType.Unknown;
                offset = 0;
                findKnownBBCountBlock(ref retBlockType, location, ref offset);
                if (retBlockType == BlockType.BBCounts)
                    return retBlockType;
            }

            offset = 0;
            return BlockType.Unknown;
        }

        partial void findKnownBBCountBlock(ref BlockType blockType, void* location, ref int offset);

        private void recordRelocation(void* location, void* target, ushort fRelocType, ushort slotNum, int addlDelta)
        {
            // slotNum is not unused
            Debug.Assert(slotNum == 0);

            int relocOffset;
            BlockType locationBlock = findKnownBlock(location, out relocOffset);
            Debug.Assert(locationBlock != BlockType.Unknown, "BlockType.Unknown not expected");

            if (locationBlock != BlockType.Code)
            {
                // TODO: https://github.com/dotnet/corert/issues/3877
                TargetArchitecture targetArchitecture = _compilation.TypeSystemContext.Target.Architecture;
                if (targetArchitecture == TargetArchitecture.ARM)
                    return;
                throw new NotImplementedException("Arbitrary relocs"); 
            }

            int relocDelta;
            BlockType targetBlock = findKnownBlock(target, out relocDelta);

            ISymbolNode relocTarget;
            switch (targetBlock)
            {
                case BlockType.Code:
                    relocTarget = _methodCodeNode;
                    break;

                case BlockType.ColdCode:
                    // TODO: Arbitrary relocs
                    throw new NotImplementedException("ColdCode relocs");

                case BlockType.ROData:
                    relocTarget = _roDataBlob;
                    break;

#if READYTORUN
                case BlockType.BBCounts:
                    relocTarget = _profileDataNode;
                    break;
#endif

                default:
                    // Reloc points to something outside of the generated blocks
                    var targetObject = HandleToObject((IntPtr)target);
                    relocTarget = (ISymbolNode)targetObject;
                    break;
            }

            relocDelta += addlDelta;

            // relocDelta is stored as the value
            Relocation.WriteValue((RelocType)fRelocType, location, relocDelta);

            if (_relocs.Count == 0)
                _relocs.EnsureCapacity(_code.Length / 32 + 1);
            _relocs.Add(new Relocation((RelocType)fRelocType, relocOffset, relocTarget));
        }

        private ushort getRelocTypeHint(void* target)
        {
            switch (_compilation.TypeSystemContext.Target.Architecture)
            {
                case TargetArchitecture.X64:
                    return (ushort)ILCompiler.DependencyAnalysis.RelocType.IMAGE_REL_BASED_REL32;

                case TargetArchitecture.ARM:
                    return (ushort)ILCompiler.DependencyAnalysis.RelocType.IMAGE_REL_BASED_THUMB_BRANCH24;

                default:
                    return UInt16.MaxValue;
            }
        }

        private void getModuleNativeEntryPointRange(ref void* pStart, ref void* pEnd)
        { throw new NotImplementedException("getModuleNativeEntryPointRange"); }

        private uint getExpectedTargetArchitecture()
        {
            TargetArchitecture arch = _compilation.TypeSystemContext.Target.Architecture;

            switch (arch)
            {
                case TargetArchitecture.X86:
                    return (uint)ImageFileMachine.I386;
                case TargetArchitecture.X64:
                    return (uint)ImageFileMachine.AMD64;
                case TargetArchitecture.ARM:
                    return (uint)ImageFileMachine.ARM;
                case TargetArchitecture.ARM64:
                    return (uint)ImageFileMachine.ARM;
                default:
                    throw new NotImplementedException("Expected target architecture is not supported");
            }
        }

        private uint getJitFlags(ref CORJIT_FLAGS flags, uint sizeInBytes)
        {
            // Read the user-defined configuration options.
            foreach (var flag in _jitConfig.Flags)
                flags.Set(flag);

            // Set the rest of the flags that don't make sense to expose publically.
            flags.Set(CorJitFlag.CORJIT_FLAG_SKIP_VERIFICATION);
            flags.Set(CorJitFlag.CORJIT_FLAG_READYTORUN);
            flags.Set(CorJitFlag.CORJIT_FLAG_RELOC);
            flags.Set(CorJitFlag.CORJIT_FLAG_PREJIT);
            flags.Set(CorJitFlag.CORJIT_FLAG_USE_PINVOKE_HELPERS);

#if !READYTORUN
            if (_compilation.TypeSystemContext.Target.Architecture == TargetArchitecture.X86
                || _compilation.TypeSystemContext.Target.Architecture == TargetArchitecture.X64)
            {
                // This list needs to match the list of intrinsics we can generate detection code for
                // in HardwareIntrinsicHelpers.EmitIsSupportedIL.
                flags.Set(CorJitFlag.CORJIT_FLAG_USE_AES);
                flags.Set(CorJitFlag.CORJIT_FLAG_USE_PCLMULQDQ);
                flags.Set(CorJitFlag.CORJIT_FLAG_USE_SSE3);
                flags.Set(CorJitFlag.CORJIT_FLAG_USE_SSSE3);
                flags.Set(CorJitFlag.CORJIT_FLAG_USE_LZCNT);
            }
#endif

            if (this.MethodBeingCompiled.IsNativeCallable)
                flags.Set(CorJitFlag.CORJIT_FLAG_REVERSE_PINVOKE);

            if (this.MethodBeingCompiled.IsPInvoke)
                flags.Set(CorJitFlag.CORJIT_FLAG_IL_STUB);

            if (this.MethodBeingCompiled.IsNoOptimization)
                flags.Set(CorJitFlag.CORJIT_FLAG_MIN_OPT);

            return (uint)sizeof(CORJIT_FLAGS);
        }
    }
}
