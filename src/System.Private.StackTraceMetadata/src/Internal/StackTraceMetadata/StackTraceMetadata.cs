// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime;
using System.Text;
using System.IO;

using Internal.Metadata.NativeFormat;
using Internal.Runtime;
using Internal.Runtime.Augments;
using Internal.Runtime.TypeLoader;
using Internal.TypeSystem;

using ReflectionExecution = Internal.Reflection.Execution.ReflectionExecution;

#if BIT64
using nint = System.Int64;
using nuint = System.UInt64;
#else
using nint = System.Int32;
using nuint = System.UInt32;
#endif

namespace Internal.StackTraceMetadata
{
    /// <summary>
    /// This helper class is used to resolve non-reflectable method names using a special
    /// compiler-generated metadata blob to enhance quality of exception call stacks
    /// in situations where symbol information is not available.
    /// </summary>
    internal static class StackTraceMetadata
    {
        /// <summary>
        /// Module address-keyed map of per-module method name resolvers.
        /// </summary>
        static PerModuleMethodNameResolverHashtable _perModuleMethodNameResolverHashtable;

        /// <summary>
        /// Eager startup initialization of stack trace metadata support creates
        /// the per-module method name resolver hashtable and registers the runtime augment
        /// for metadata-based stack trace resolution.
        /// </summary>
        internal static void Initialize()
        {
            _perModuleMethodNameResolverHashtable = new PerModuleMethodNameResolverHashtable();
            RuntimeAugments.InitializeStackTraceMetadataSupport(new StackTraceMetadataCallbacksImpl());
        }

        /// <summary>
        /// Locate the containing module for a method and try to resolve its name based on start address.
        /// </summary>
        public static bool TryGetMethodNameFromStartAddressIfAvailable(IntPtr methodStartAddress, int offset, out string methodName, out string fileName, out int lineNumber)
        {
            methodName = null;
            fileName = null;
            lineNumber = 0;

            IntPtr moduleStartAddress = RuntimeAugments.GetOSModuleFromPointer(methodStartAddress);
            int rva = (int)((nuint)methodStartAddress - (nuint)moduleStartAddress);
            foreach (TypeManagerHandle handle in ModuleList.Enumerate())
            {
                if (handle.OsModuleBase == moduleStartAddress)
                {
                    if (_perModuleMethodNameResolverHashtable.GetOrCreateValue(handle.GetIntPtrUNSAFE()).TryGetMethodNameFromRvaIfAvailable(rva, offset, out methodName, out fileName, out lineNumber))
                        return true;
                }
            }

            // We haven't found information in the stack trace metadata tables, but maybe reflection will have this
            if (ReflectionExecution.TryGetMethodMetadataFromStartAddress(methodStartAddress,
                out MetadataReader reader,
                out TypeDefinitionHandle typeHandle,
                out MethodHandle methodHandle))
            {
                methodName = MethodNameFormatter.FormatMethodName(reader, typeHandle, methodHandle);
            }

            return methodName != null;
        }

        /// <summary>
        /// This hashtable supports mapping from module start addresses to per-module method name resolvers.
        /// </summary>
        private sealed class PerModuleMethodNameResolverHashtable : LockFreeReaderHashtable<IntPtr, PerModuleMethodNameResolver>
        {
            /// <summary>
            /// Given a key, compute a hash code. This function must be thread safe.
            /// </summary>
            protected override int GetKeyHashCode(IntPtr key)
            {
                return key.GetHashCode();
            }
    
            /// <summary>
            /// Given a value, compute a hash code which would be identical to the hash code
            /// for a key which should look up this value. This function must be thread safe.
            /// This function must also not cause additional hashtable adds.
            /// </summary>
            protected override int GetValueHashCode(PerModuleMethodNameResolver value)
            {
                return GetKeyHashCode(value.ModuleAddress);
            }
    
            /// <summary>
            /// Compare a key and value. If the key refers to this value, return true.
            /// This function must be thread safe.
            /// </summary>
            protected override bool CompareKeyToValue(IntPtr key, PerModuleMethodNameResolver value)
            {
                return key == value.ModuleAddress;
            }
    
            /// <summary>
            /// Compare a value with another value. Return true if values are equal.
            /// This function must be thread safe.
            /// </summary>
            protected override bool CompareValueToValue(PerModuleMethodNameResolver value1, PerModuleMethodNameResolver value2)
            {
                return value1.ModuleAddress == value2.ModuleAddress;
            }
    
            /// <summary>
            /// Create a new value from a key. Must be threadsafe. Value may or may not be added
            /// to collection. Return value must not be null.
            /// </summary>
            protected override PerModuleMethodNameResolver CreateValueFromKey(IntPtr key)
            {
                return new PerModuleMethodNameResolver(key);
            }
        }

        /// <summary>
        /// Implementation of stack trace metadata callbacks.
        /// </summary>
        private sealed class StackTraceMetadataCallbacksImpl : StackTraceMetadataCallbacks
        {
            public override bool TryGetMethodNameFromStartAddress(IntPtr methodStartAddress, int offset, out string methodName, out string fileName, out int lineNumber)
            {
                return TryGetMethodNameFromStartAddressIfAvailable(methodStartAddress, offset, out methodName, out fileName, out lineNumber);
            }
        }

        /// <summary>
        /// Method name resolver for a single binary module
        /// </summary>
        private sealed class PerModuleMethodNameResolver
        {
            /// <summary>
            /// Start address of the module in question.
            /// </summary>
            private readonly IntPtr _moduleAddress;
            
            /// <summary>
            /// Dictionary mapping method RVA's to tokens within the metadata blob.
            /// </summary>
            private readonly Dictionary<int, nuint> _methodRvaToTokenMap;

            /// <summary>
            /// Metadata reader for the stack trace metadata.
            /// </summary>
            private readonly MetadataReader _metadataReader;

            /// <summary>
            /// Pointer to the beginning of the sequence point table blob
            /// </summary>
            private readonly unsafe int* _sequencePointTable;

            /// <summary>
            /// Pointer to the beginning of the string heap containing the filenames
            /// </summary>
            private readonly unsafe int* _stringHeap;

            /// <summary>
            /// Publicly exposed module address property.
            /// </summary>
            public IntPtr ModuleAddress { get { return _moduleAddress; } }

            /// <summary>
            /// Construct the per-module resolver by looking up the necessary blobs.
            /// </summary>
            public unsafe PerModuleMethodNameResolver(IntPtr moduleAddress)
            {
                _moduleAddress = moduleAddress;

                TypeManagerHandle handle = new TypeManagerHandle(moduleAddress);
                ModuleInfo moduleInfo;
                if (!ModuleList.Instance.TryGetModuleInfoByHandle(handle, out moduleInfo))
                {
                    // Module not found
                    return;
                }

                NativeFormatModuleInfo nativeFormatModuleInfo = moduleInfo as NativeFormatModuleInfo;
                if (nativeFormatModuleInfo == null)
                {
                    // It is not a native format module
                    return;
                }

                byte *metadataBlob;
                uint metadataBlobSize;

                byte *rvaToTokenMapBlob;
                uint rvaToTokenMapBlobSize;
                
                if (nativeFormatModuleInfo.TryFindBlob(
                        (int)ReflectionMapBlob.EmbeddedMetadata,
                        out metadataBlob,
                        out metadataBlobSize) &&
                    nativeFormatModuleInfo.TryFindBlob(
                        (int)ReflectionMapBlob.BlobIdStackTraceMethodRvaToTokenMapping,
                        out rvaToTokenMapBlob,
                        out rvaToTokenMapBlobSize))
                {
                    _metadataReader = new MetadataReader(new IntPtr(metadataBlob), (int)metadataBlobSize);

                    int* rvaToTokenMap = (int*)rvaToTokenMapBlob;
                    int rvaToTokenMapEntryCount = rvaToTokenMap[0];
                    int sequencePointOffset = rvaToTokenMap[1];
                    int stringHeapOffset = rvaToTokenMap[2];

                    if (sequencePointOffset != -1)
                    {
                        _sequencePointTable = (int*)(rvaToTokenMapBlob + sequencePointOffset);
                        _stringHeap = (int*)(rvaToTokenMapBlob + stringHeapOffset);
                    }

                    _methodRvaToTokenMap = new Dictionary<int, nuint>(rvaToTokenMapEntryCount);
                    PopulateRvaToTokenMap(handle, &rvaToTokenMap[3], rvaToTokenMapEntryCount, sequencePointOffset != -1);
                }
            }

            /// <summary>
            /// Construct the dictionary mapping method RVAs to stack trace metadata tokens
            /// within a single binary module.
            /// </summary>
            /// <param name="rvaToTokenMap">List of RVA - token pairs</param>
            /// <param name="entryCount">Number of the RVA - token pairs in the list</param>
            private unsafe void PopulateRvaToTokenMap(TypeManagerHandle handle, int* rvaToTokenMap, int entryCount, bool hasSequencePoints)
            {
                int skippingOffset = hasSequencePoints ? 2 : 1; // do we just skip to the next element or do we skip over sequencePointsOffset
                for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
                {
                    int* pRelPtr32 = rvaToTokenMap++;
                    IntPtr pointer = (IntPtr)((byte*)pRelPtr32 + *pRelPtr32);
                    int methodRva = (int)((nuint)pointer - (nuint)handle.OsModuleBase);
                    nuint tokenAddress = (nuint)rvaToTokenMap;
                    rvaToTokenMap += skippingOffset;
                    _methodRvaToTokenMap[methodRva] = tokenAddress;
                }
            }

            /// <summary>
            /// Try to resolve method name based on its address using the stack trace metadata
            /// </summary>
            public unsafe bool TryGetMethodNameFromRvaIfAvailable(int rva, int offset, out string methodName, out string fileName, out int lineNumber)
            {
                methodName = null;
                fileName = null;
                lineNumber = 0;

                if (_methodRvaToTokenMap == null)
                {
                    // No stack trace metadata for this module
                    return false;
                }

                if (!_methodRvaToTokenMap.TryGetValue(rva, out nuint tokenAddress))
                {
                    // Method RVA not found in the map
                    return false;
                }

                int* tokenPtr = (int*)tokenAddress;
                int rawToken = tokenPtr[0];

                methodName = MethodNameFormatter.FormatMethodName(_metadataReader, Handle.FromIntToken(rawToken));
                if (offset == -1) // short circuit if we're not trying to get line numbers
                {
                    return true;
                }

                if (_sequencePointTable == default) // this module doesn't have sequence points
                {
                    return true;
                }

                int sequencePointsOffset = tokenPtr[1];
                if (sequencePointsOffset == -1) // no line numbers available
                {
                    return true;
                }

                int* sequencePointsPtr = (int*)((byte*)_sequencePointTable + sequencePointsOffset);
                int sequencePointsBlockCount = *sequencePointsPtr++;

                int fileNameOffset = -1;
                for (int blockCount = 0; blockCount < sequencePointsBlockCount; blockCount++)
                {
                    int consecutiveSequencePoints = *sequencePointsPtr++;
                    fileNameOffset = *sequencePointsPtr++;
                    for (int i = 0; i < consecutiveSequencePoints; i++)
                    {
                        int nativeOffset = *sequencePointsPtr++;
                        if (offset >= nativeOffset)
                        {
                            lineNumber = *sequencePointsPtr++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                if (fileNameOffset != -1)
                {
                    int* fileNamePtr = (int*)((byte*)_stringHeap + fileNameOffset);
                    int fileNameLength = *fileNamePtr++;
                    byte* fileNameData = (byte*)fileNamePtr;
                    fileName = Encoding.UTF8.GetString(fileNameData, fileNameLength);
                }

                return true;
            }
        }
    }
}
