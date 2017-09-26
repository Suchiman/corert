﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using ILCompiler.DependencyAnalysis.ARM64;

namespace ILCompiler.DependencyAnalysis
{
    public partial class UnboxingStubNode
    {
        protected override void EmitCode(NodeFactory factory, ref ARM64Emitter encoder, bool relocsOnly)
        {
            encoder.EmitADD(encoder.TargetRegister.Arg0, (byte)factory.Target.PointerSize); // add r0, sizeof(void*);         
            encoder.EmitJMP(factory.MethodEntrypoint(Method)); // b methodEntryPoint
        }
    }
}
