﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Mond.Compiler;
using Mond.Debugger;

namespace Mond.VirtualMachine
{
    partial class Machine
    {
        private readonly MondState _state;
        internal MondValue Global;

        private MondDebugAction _debugAction;
        private bool _debugSkip;
        private bool _debugAlign;
        private int _debugDepth;
        internal MondDebugger Debugger;

        public Machine(MondState state)
            : this()
        {
            _state = state;
            Global = MondValue.Object(state);

            _debugAction = MondDebugAction.Run;
            _debugSkip = false;
            _debugAlign = false;
            _debugDepth = 0;
            Debugger = null;
        }

        public string CurrentScript
        {
            get
            {
                if (_callStackSize == 0)
                    throw new InvalidOperationException("No scripts are running");

                return _callStack[_callStackSize - 1].Program.DebugInfo?.FileName;
            }
        }

        public MondValue Load(MondProgram program)
        {
            if (program == null)
                throw new ArgumentNullException(nameof(program));

            var function = new MondValue(new Closure(program, 0, null, null));
            return Call(function);
        }

        public MondValue Call(MondValue function, params MondValue[] arguments)
        {
            if (function.Type == MondValueType.Object)
            {
                // insert "this" value into argument array
                Array.Resize(ref arguments, arguments.Length + 1);
                Array.Copy(arguments, 0, arguments, 1, arguments.Length - 1);
                arguments[0] = function;

                if (function.TryDispatch("__call", out var result, arguments))
                    return result;
            }

            if (function.Type != MondValueType.Function)
                throw new MondRuntimeException(RuntimeError.ValueNotCallable, function.Type.GetName());

            var closure = function.FunctionValue;

            switch (closure.Type)
            {
                case ClosureType.Mond:
                    var argFrame = closure.Arguments;
                    if (argFrame == null)
                        argFrame = new Frame(0, null, arguments.Length);
                    else
                        argFrame = new Frame(argFrame.Depth + 1, argFrame, arguments.Length);

                    for (var i = 0; i < arguments.Length; i++)
                    {
                        argFrame.Values[i] = arguments[i];
                    }

                    PushCall(new ReturnAddress(closure.Program, closure.Address, argFrame, _evalStackSize));
                    PushLocal(closure.Locals);
                    break;

                case ClosureType.Native:
                    return closure.NativeFunction(_state, arguments);

                default:
                    throw new NotSupportedException();
            }

            return Run();
        }

        private MondValue Run()
        {
            var functionAddress = PeekCall();
            var program = functionAddress.Program;
            var code = program.Bytecode;

            var initialCallDepth = _callStackSize - 1; // "- 1" to not include values pushed by Call()
            var initialLocalDepth = _localStackSize - 1;
            var initialEvalDepth = _evalStackSize;

            var ip = functionAddress.Address;
            var errorIp = 0;

            var args = functionAddress.Arguments;
            Frame locals = null;

            try
            {
                while (true)
                {
                    if (Debugger != null)
                    {
                        var skip = _debugSkip;
                        _debugSkip = false;

                        var shouldStopAtStmt =
                            (_debugAction == MondDebugAction.StepInto) ||
                            (_debugAction == MondDebugAction.StepOver && _debugDepth == 0);

                        var shouldBreak =
                            (_debugAlign && program.DebugInfo == null) ||
                            (_debugAlign && program.DebugInfo.IsStatementStart(ip)) ||
                            (Debugger.ShouldBreak(program, ip)) ||
                            (shouldStopAtStmt && program.DebugInfo != null && program.DebugInfo.IsStatementStart(ip));

                        if (!skip && shouldBreak)
                            DebuggerBreak(program, locals, args, ip, initialCallDepth);
                    }

                    errorIp = ip;

                    switch (code[ip++])
                    {
                        #region Stack Manipulation
                        case (int)InstructionType.Dup:
                            {
                                Push(Peek());
                                break;
                            }

                        case (int)InstructionType.Dup2:
                            {
                                var value2 = Pop();
                                var value1 = Pop();
                                Push(value1);
                                Push(value2);
                                Push(value1);
                                Push(value2);
                                break;
                            }

                        case (int)InstructionType.Drop:
                            {
                                Pop();
                                break;
                            }

                        case (int)InstructionType.Swap:
                            {
                                var value1 = Pop();
                                var value2 = Pop();
                                Push(value1);
                                Push(value2);
                                break;
                            }

                        case (int)InstructionType.Swap1For2:
                            {
                                var one = Pop();
                                var two2 = Pop();
                                var two1 = Pop();
                                Push(one);
                                Push(two1);
                                Push(two2);
                                break;
                            }
                        #endregion

                        #region Constants
                        case (int)InstructionType.LdUndef:
                            {
                                Push(MondValue.Undefined);
                                break;
                            }

                        case (int)InstructionType.LdNull:
                            {
                                Push(MondValue.Null);
                                break;
                            }

                        case (int)InstructionType.LdTrue:
                            {
                                Push(MondValue.True);
                                break;
                            }

                        case (int)InstructionType.LdFalse:
                            {
                                Push(MondValue.False);
                                break;
                            }

                        case (int)InstructionType.LdNum:
                            {
                                var numId = ReadInt32(code, ref ip);
                                Push(program.Numbers[numId]);
                                break;
                            }

                        case (int)InstructionType.LdStr:
                            {
                                var strId = ReadInt32(code, ref ip);
                                Push(program.Strings[strId]);
                                break;
                            }

                        case (int)InstructionType.LdGlobal:
                            {
                                Push(Global);
                                break;
                            }
                        #endregion

                        #region Storables
                        case (int)InstructionType.LdLocF:
                            {
                                var index = ReadInt32(code, ref ip);
                                Push(locals.Values[index]);
                                break;
                            }

                        case (int)InstructionType.StLocF:
                            {
                                var index = ReadInt32(code, ref ip);
                                locals.Values[index] = Pop();
                                break;
                            }

                        case (int)InstructionType.LdLoc:
                            {
                                var depth = ReadInt32(code, ref ip);
                                var index = ReadInt32(code, ref ip);

                                if (depth < 0)
                                    Push(args.Get(-depth, index));
                                else
                                    Push(locals.Get(depth, index));

                                break;
                            }

                        case (int)InstructionType.StLoc:
                            {
                                var depth = ReadInt32(code, ref ip);
                                var index = ReadInt32(code, ref ip);

                                if (depth < 0)
                                    args.Set(-depth, index, Pop());
                                else
                                    locals.Set(depth, index, Pop());

                                break;
                            }

                        case (int)InstructionType.LdFld:
                            {
                                var obj = Pop();
                                Push(obj[program.Strings[ReadInt32(code, ref ip)]]);
                                break;
                            }

                        case (int)InstructionType.StFld:
                            {
                                var obj = Pop();
                                var value = Pop();

                                obj[program.Strings[ReadInt32(code, ref ip)]] = value;
                                break;
                            }

                        case (int)InstructionType.LdArr:
                            {
                                var index = Pop();
                                var array = Pop();
                                Push(array[index]);
                                break;
                            }

                        case (int)InstructionType.StArr:
                            {
                                var index = Pop();
                                var array = Pop();
                                var value = Pop();
                                array[index] = value;
                                break;
                            }

                        case (int)InstructionType.LdState:
                            {
                                var depth = ReadInt32(code, ref ip);
                                var frame = locals.GetFrame(depth);
                                locals = frame.StoredFrame;

                                PopLocal();
                                PushLocal(locals);

                                var evals = frame.StoredEvals;
                                if (evals != null)
                                {
                                    for (var i = evals.Count - 1; i >= 0; i--)
                                    {
                                        Push(evals[i]);
                                    }

                                    evals.Clear();
                                }

                                break;
                            }

                        case (int)InstructionType.StState:
                            {
                                var depth = ReadInt32(code, ref ip);
                                var frame = locals.GetFrame(depth);
                                frame.StoredFrame = locals;

                                var initialEvals = _callStackSize > 0 ? PeekCall().EvalDepth : 0;
                                var currentEvals = _evalStackSize;

                                if (currentEvals != initialEvals)
                                {
                                    var evals = frame.StoredEvals ?? (frame.StoredEvals = new List<MondValue>());

                                    while (currentEvals != initialEvals)
                                    {
                                        evals.Add(Pop());
                                        currentEvals--;
                                    }
                                }

                                break;
                            }
                        #endregion

                        #region Object Creation
                        case (int)InstructionType.NewObject:
                            {
                                var obj = MondValue.Object(_state);
                                Push(obj);
                                break;
                            }

                        case (int)InstructionType.NewArray:
                            {
                                var count = ReadInt32(code, ref ip);
                                var array = MondValue.Array();
                                array.ArrayValue.Capacity = count;

                                for (var i = 0; i < count; i++)
                                    array.ArrayValue.Add(default(MondValue));

                                Push(array);
                                break;
                            }

                        case (int)InstructionType.Slice:
                            {
                                var step = Pop();
                                var end = Pop();
                                var start = Pop();
                                var array = Pop();

                                Push(array.Slice(start, end, step));
                                break;
                            }
                        #endregion

                        #region Math
                        case (int)InstructionType.Add:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left + right);
                                break;
                            }

                        case (int)InstructionType.Sub:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left - right);
                                break;
                            }

                        case (int)InstructionType.Mul:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left * right);
                                break;
                            }

                        case (int)InstructionType.Div:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left / right);
                                break;
                            }

                        case (int)InstructionType.Mod:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left % right);
                                break;
                            }

                        case (int)InstructionType.Exp:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left.Pow(right));
                                break;
                            }

                        case (int)InstructionType.BitLShift:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left.LShift(right));
                                break;
                            }

                        case (int)InstructionType.BitRShift:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left.RShift(right));
                                break;
                            }

                        case (int)InstructionType.BitAnd:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left & right);
                                break;
                            }

                        case (int)InstructionType.BitOr:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left | right);
                                break;
                            }

                        case (int)InstructionType.BitXor:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left ^ right);
                                break;
                            }

                        case (int)InstructionType.Neg:
                            {
                                Push(-Pop());
                                break;
                            }

                        case (int)InstructionType.BitNot:
                            {
                                Push(~Pop());
                                break;
                            }
                        #endregion

                        #region Logic
                        case (int)InstructionType.Eq:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left == right);
                                break;
                            }

                        case (int)InstructionType.Neq:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left != right);
                                break;
                            }

                        case (int)InstructionType.Gt:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left > right);
                                break;
                            }

                        case (int)InstructionType.Gte:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left >= right);
                                break;
                            }

                        case (int)InstructionType.Lt:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left < right);
                                break;
                            }

                        case (int)InstructionType.Lte:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(left <= right);
                                break;
                            }

                        case (int)InstructionType.Not:
                            {
                                Push(!Pop());
                                break;
                            }

                        case (int)InstructionType.In:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(right.Contains(left));
                                break;
                            }

                        case (int)InstructionType.NotIn:
                            {
                                var right = Pop();
                                var left = Pop();
                                Push(!right.Contains(left));
                                break;
                            }
                        #endregion

                        #region Functions
                        case (int)InstructionType.Closure:
                            {
                                var address = ReadInt32(code, ref ip);
                                Push(new MondValue(new Closure(program, address, args, locals)));
                                break;
                            }

                        case (int)InstructionType.Call:
                            {
                                var argCount = ReadInt32(code, ref ip);
                                var unpackCount = code[ip++];

                                var function = Pop();

                                List<MondValue> unpackedArgs = null;

                                if (unpackCount > 0)
                                    unpackedArgs = UnpackArgs(code, ref ip, argCount, unpackCount);

                                var returnAddress = ip;

                                if (function.Type == MondValueType.Object)
                                {
                                    MondValue[] argArr;

                                    if (unpackedArgs == null)
                                    {
                                        argArr = new MondValue[argCount + 1];

                                        for (var i = argCount; i >= 1; i--)
                                        {
                                            argArr[i] = Pop();
                                        }

                                        argArr[0] = function;
                                    }
                                    else
                                    {
                                        unpackedArgs.Insert(0, function);
                                        argArr = unpackedArgs.ToArray();
                                    }

                                    if (function.TryDispatch("__call", out var result, argArr))
                                    {
                                        Push(result);
                                        break;
                                    }
                                }

                                if (function.Type != MondValueType.Function)
                                {
                                    var ldFldBase = ip - 1 - 4 - 1 - 4 - 1;
                                    if (ldFldBase >= 0 && code[ldFldBase] == (int)InstructionType.LdFld)
                                    {
                                        var ldFldIdx = ldFldBase + 1;
                                        var fieldNameIdx = ReadInt32(code, ref ldFldIdx);

                                        if (fieldNameIdx >= 0 && fieldNameIdx < program.Strings.Count)
                                        {
                                            var fieldName = program.Strings[fieldNameIdx];
                                            throw new MondRuntimeException(RuntimeError.FieldNotCallable, (string)fieldName);
                                        }
                                    }

                                    throw new MondRuntimeException(RuntimeError.ValueNotCallable, function.Type.GetName());
                                }

                                var closure = function.FunctionValue;

                                var argFrame = function.FunctionValue.Arguments;
                                var argFrameCount = unpackedArgs?.Count ?? argCount;

                                if (argFrame == null)
                                    argFrame = new Frame(1, null, argFrameCount);
                                else
                                    argFrame = new Frame(argFrame.Depth + 1, argFrame, argFrameCount);

                                // copy arguments into frame
                                if (unpackedArgs == null)
                                {
                                    for (var i = argFrameCount - 1; i >= 0; i--)
                                    {
                                        argFrame.Values[i] = Pop();
                                    }
                                }
                                else
                                {
                                    for (var i = 0; i < argFrameCount; i++)
                                    {
                                        argFrame.Values[i] = unpackedArgs[i];
                                    }
                                }

                                switch (closure.Type)
                                {
                                    case ClosureType.Mond:
                                        PushCall(new ReturnAddress(program, returnAddress, argFrame, _evalStackSize));
                                        PushLocal(closure.Locals);

                                        program = closure.Program;
                                        code = program.Bytecode;
                                        ip = closure.Address;
                                        args = argFrame;
                                        locals = closure.Locals;

                                        if (Debugger != null)
                                            DebuggerCheckCall();

                                        break;

                                    case ClosureType.Native:
                                        var result = closure.NativeFunction(_state, argFrame.Values);
                                        Push(result);
                                        break;

                                    default:
                                        throw new MondRuntimeException(RuntimeError.UnhandledClosureType);
                                }

                                break;
                            }

                        case (int)InstructionType.TailCall:
                            {
                                var argCount = ReadInt32(code, ref ip);
                                var address = ReadInt32(code, ref ip);
                                var unpackCount = code[ip++];

                                List<MondValue> unpackedArgs = null;

                                if (unpackCount > 0)
                                    unpackedArgs = UnpackArgs(code, ref ip, argCount, unpackCount);

                                var returnAddress = PopCall();
                                var argFrame = returnAddress.Arguments;
                                var argFrameCount = unpackedArgs?.Count ?? argCount;

                                // make sure we have the correct number of values
                                if (argFrameCount != argFrame.Values.Length)
                                    argFrame.Values = new MondValue[argFrameCount];

                                // copy arguments into frame
                                if (unpackedArgs == null)
                                {
                                    for (var i = argFrameCount - 1; i >= 0; i--)
                                    {
                                        argFrame.Values[i] = Pop();
                                    }
                                }
                                else
                                {
                                    for (var i = 0; i < argFrameCount; i++)
                                    {
                                        argFrame.Values[i] = unpackedArgs[i];
                                    }
                                }

                                // get rid of old locals
                                PushLocal(PopLocal().Previous);

                                PushCall(new ReturnAddress(returnAddress.Program, returnAddress.Address, argFrame, _evalStackSize));

                                ip = address;
                                break;
                            }

                        case (int)InstructionType.Enter:
                            {
                                var localCount = ReadInt32(code, ref ip);

                                var frame = PopLocal();
                                frame = new Frame(frame?.Depth + 1 ?? 0, frame, localCount);

                                PushLocal(frame);
                                locals = frame;
                                break;
                            }

                        case (int)InstructionType.Leave:
                            {
                                var frame = PopLocal();
                                frame = frame.Previous;

                                PushLocal(frame);
                                locals = frame;
                                break;
                            }

                        case (int)InstructionType.Ret:
                            {
                                var returnAddress = PopCall();
                                PopLocal();

                                program = returnAddress.Program;
                                code = program.Bytecode;
                                ip = returnAddress.Address;

                                args = _callStackSize > 0 ? PeekCall().Arguments : null;
                                locals = _localStackSize > 0 ? PeekLocal() : null;

                                if (_callStackSize == initialCallDepth)
                                    return Pop();

                                if (Debugger != null && DebuggerCheckReturn())
                                    DebuggerBreak(program, locals, args, ip, initialCallDepth);

                                break;
                            }

                        case (int)InstructionType.VarArgs:
                            {
                                var fixedCount = ReadInt32(code, ref ip);
                                var varArgs = MondValue.Array();

                                for (var i = fixedCount; i < args.Values.Length; i++)
                                {
                                    varArgs.ArrayValue.Add(args.Values[i]);
                                }

                                args.Set(args.Depth, fixedCount, varArgs);
                                break;
                            }
                        #endregion

                        #region Branching
                        case (int)InstructionType.Jmp:
                            {
                                var address = ReadInt32(code, ref ip);
                                ip = address;
                                break;
                            }

                        case (int)InstructionType.JmpTrueP:
                            {
                                var address = ReadInt32(code, ref ip);

                                if (Peek())
                                    ip = address;

                                break;
                            }

                        case (int)InstructionType.JmpFalseP:
                            {
                                var address = ReadInt32(code, ref ip);

                                if (!Peek())
                                    ip = address;

                                break;
                            }

                        case (int)InstructionType.JmpTrue:
                            {
                                var address = ReadInt32(code, ref ip);

                                if (Pop())
                                    ip = address;

                                break;
                            }

                        case (int)InstructionType.JmpFalse:
                            {
                                var address = ReadInt32(code, ref ip);

                                if (!Pop())
                                    ip = address;

                                break;
                            }

                        case (int)InstructionType.JmpTable:
                            {
                                var start = ReadInt32(code, ref ip);
                                var count = ReadInt32(code, ref ip);

                                var endIp = ip + count * 4;

                                var value = Pop();
                                if (value.Type == MondValueType.Number)
                                {
                                    var number = (double)value;
                                    var numberInt = (int)number;

                                    if (number >= start && number < start + count &&
                                        Math.Abs(number - numberInt) <= double.Epsilon)
                                    {
                                        ip += (numberInt - start) * 4;
                                        ip = ReadInt32(code, ref ip);
                                        break;
                                    }
                                }

                                ip = endIp;
                                break;
                            }
                        #endregion

                        case (int)InstructionType.Breakpoint:
                            {
                                if (Debugger == null)
                                    break;

                                DebuggerBreak(program, locals, args, ip, initialCallDepth);

                                // we stop for the statement *after* the debugger statement so we
                                // skip the next break opportunity, otherwise we break twice
                                _debugSkip = true;
                                break;
                            }

                        default:
                            throw new MondRuntimeException(RuntimeError.UnhandledOpcode);
                    }
                }
            }
            catch (Exception e)
            {
                var message = e.Message.Trim();

                // we skip the OOB checks in the stack methods because the CLR has issues eliminating 
                // its own checks, so we let it throw and check here for a bit of a speed boost
                if (e is IndexOutOfRangeException)
                {
                    if (_callStackSize >= CallStackCapacity || _localStackSize >= CallStackCapacity || _evalStackSize >= EvalStackCapacity)
                    {
                        message = RuntimeError.StackOverflow;
                    }
                    else if (_callStackSize < 0 || _localStackSize < 0 || _evalStackSize < 0)
                    {
                        message = RuntimeError.StackEmpty;
                    }
                }

                StringBuilder stackTraceBuilder;

                if (e is MondRuntimeException runtimeException &&
                    runtimeException.MondStackTrace != null)
                {
                    stackTraceBuilder = new StringBuilder(runtimeException.MondStackTrace);

                    // check if we are running in a wrapped function
                    var stackTrace = new System.Diagnostics.StackTrace(e, false);
                    var frames = stackTrace.GetFrames();
                    var foundWrapper = false;

                    // skip the first frame because it's this method? need to verify
                    for (var i = 1; i < frames.Length; i++)
                    {
                        var method = frames[i].GetMethod();
                        if (method == null)
                            continue; // ???

                        var type = method.DeclaringType;

                        // stop at the next call to Machine.Run because it can be recursive
                        if (type == typeof(Machine) && method.Name == "Run")
                            break;

                        // the wrapper is a lambda so it's in a compiler generated type, which will be nested
                        var parentType = type.DeclaringType;
                        if (parentType == null)
                            continue;

                        // the type and method are compiler generated so they have a weird (and compiler specific) name
                        const string wrapperMagic = "<CheckWrapFunction>";

                        // make sure the type is nested in MondValue and check both the type and method name
                        if (parentType == typeof(MondValue) && (method.Name.Contains(wrapperMagic) || type.Name.Contains(wrapperMagic)))
                        {
                            foundWrapper = true;
                            break;
                        }
                    }
                    
                    // don't show a native transition for wrappers
                    if (!foundWrapper)
                        stackTraceBuilder.AppendLine("[... native ...]");
                }
                else
                {
                    stackTraceBuilder = new StringBuilder();
                }

                // first line of the stack trace is where we are running
                stackTraceBuilder.AppendLine(GetAddressDebugInfo(program, errorIp));

                // generate stack trace and reset stacks
                for (var i = Math.Min(_callStackSize - 1, CallStackCapacity - 1); i > initialCallDepth; i--)
                {
                    var returnAddress = _callStack[i];
                    stackTraceBuilder.AppendLine(GetAddressDebugInfo(returnAddress.Program, returnAddress.Address));
                }

                _callStackSize = initialCallDepth;
                for (var i = _callStackSize; i < CallStackCapacity; i++)
                {
                    _callStack[i] = default(ReturnAddress);
                }

                _localStackSize = initialLocalDepth;
                for (var i = _localStackSize; i < CallStackCapacity; i++)
                {
                    _localStack[i] = default(Frame);
                }

                _evalStackSize = initialEvalDepth;
                for (var i = _evalStackSize; i < EvalStackCapacity; i++)
                {
                    _evalStack[i] = default(MondValue);
                }

                throw new MondRuntimeException(message, e)
                {
                    MondStackTrace = stackTraceBuilder.ToString()
                };
            }
        }

        private List<MondValue> UnpackArgs(byte[] code, ref int ip, int argCount, int unpackCount)
        {
            var unpackIndices = new List<int>(unpackCount);

            for (var i = 0; i < unpackCount; i++)
            {
                unpackIndices.Add(ReadInt32(code, ref ip));
            }

            var unpackedArgs = new List<MondValue>(argCount + unpackCount * 16);
            var argIndex = 0;
            var unpackIndex = 0;

            for (var i = argCount - 1; i >= 0; i--)
            {
                var value = Pop();

                if (unpackIndex < unpackIndices.Count && i == unpackIndices[unpackIndex])
                {
                    unpackIndex++;

                    var start = argIndex;
                    var count = 0;

                    foreach (var unpackedValue in value.Enumerate(_state))
                    {
                        unpackedArgs.Add(unpackedValue);
                        argIndex++;
                        count++;
                    }

                    unpackedArgs.Reverse(start, count);

                    continue;
                }

                unpackedArgs.Add(value);
                argIndex++;
            }

            unpackedArgs.Reverse();
            return unpackedArgs;
        }

        private void DebuggerCheckCall()
        {
            switch (_debugAction)
            {
                case MondDebugAction.StepInto:
                    _debugAlign = true;
                    return;

                case MondDebugAction.StepOver:
                case MondDebugAction.StepOut:
                    _debugDepth++;
                    _debugAlign = false;
                    return;
            }
        }

        private bool DebuggerCheckReturn()
        {
            switch (_debugAction)
            {
                case MondDebugAction.StepInto:
                    return !_debugAlign;

                case MondDebugAction.StepOver:
                    --_debugDepth;

                    if (_debugDepth < 0)
                        return true;

                    _debugAlign = _debugDepth == 0;
                    return false;

                case MondDebugAction.StepOut:
                    return --_debugDepth < 0;
            }

            return false;
        }

        private void DebuggerBreak(MondProgram program, Frame locals, Frame args, int address, int initialCallDepth)
        {
            var context = new MondDebugContext(
                _state, program, address, locals, args, _callStack, _callStackSize, initialCallDepth);

            _debugAction = Debugger.Break(context, address);
            _debugAlign = false;
            _debugDepth = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadInt32(byte[] buffer, ref int offset)
        {
            return buffer[offset++] <<  0 |
                   buffer[offset++] <<  8 |
                   buffer[offset++] << 16 |
                   buffer[offset++] << 24;
        }

        private static string GetAddressDebugInfo(MondProgram program, int address)
        {
            if (program.DebugInfo != null)
            {
                var func = program.DebugInfo.FindFunction(address);
                var position = program.DebugInfo.FindPosition(address);

                if (func.HasValue && position.HasValue)
                {
                    var prefix = "";
                    var funcName = program.Strings[func.Value.Name];
                    var fileName = program.DebugInfo.FileName ?? program.GetHashCode().ToString("X8");

                    if (!string.IsNullOrEmpty(funcName))
                        prefix = string.Format("at {0} ", funcName);

                    return string.Format("{0}in {1}: line {2}:{3}", prefix, fileName, position.Value.LineNumber, position.Value.ColumnNumber);
                }
            }

            return address.ToString("X8");
        }
    }
}
