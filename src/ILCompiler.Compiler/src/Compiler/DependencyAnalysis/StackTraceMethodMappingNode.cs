// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler.DependencyAnalysis
{
    /// <summary>
    /// BlobIdStackTraceMethodRvaToTokenMapping - list of 8-byte pairs (method RVA-method token)
    /// </summary>
    public sealed class StackTraceMethodMappingNode : ObjectNode, ISymbolDefinitionNode
    {
        public StackTraceMethodMappingNode()
        {
            _endSymbol = new ObjectAndOffsetSymbolNode(this, 0, "_stacktrace_methodRVA_to_token_mapping_End", true);
        }

        private ObjectAndOffsetSymbolNode _endSymbol;
        public ISymbolDefinitionNode EndSymbol => _endSymbol;

        public override bool IsShareable => false;

        public override ObjectNodeSection Section => ObjectNodeSection.ReadOnlyDataSection;

        public override bool StaticDependenciesAreComputed => true;

        public int Offset => 0;

        protected internal override int Phase => (int)ObjectNodePhase.Ordered;
        public override int ClassCode => (int)ObjectNodeOrder.StackTraceMethodMappingNode;

        public void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb)
        {
            sb.Append(nameMangler.CompilationUnitPrefix).Append("_stacktrace_methodRVA_to_token_mapping");
        }

        protected override string GetName(NodeFactory factory) => this.GetMangledName(factory.NameMangler);

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly = false)
        {
            // The dependency tracking of this node currently does nothing because the data emission relies
            // the set of compiled methods which has an incomplete state during dependency tracking.
            if (relocsOnly)
            {
                return new ObjectData(Array.Empty<byte>(), Array.Empty<Relocation>(), 1, new ISymbolDefinitionNode[] { this });
            }

            ObjectDataBuilder objData = new ObjectDataBuilder(factory, relocsOnly);
            objData.RequireInitialPointerAlignment();
            objData.AddSymbol(this);
            objData.AddSymbol(_endSymbol);

            RelocType reloc = factory.Target.Abi == TargetAbi.CoreRT ?
                RelocType.IMAGE_REL_BASED_RELPTR32 : RelocType.IMAGE_REL_BASED_ADDR32NB;

            IEnumerable<MetadataMapping<MethodDesc>> mappingEntries = factory.MetadataManager.GetStackTraceMapping(factory);
            if (!(mappingEntries is ICollection<MetadataMapping<MethodDesc>> collection))
            {
                mappingEntries = collection = mappingEntries.ToList();
            }
            objData.EmitInt(collection.Count);
            var reservedSequencePointsOffset = objData.ReserveInt();
            var reservedStringOffset = objData.ReserveInt();

            int byteCounter = 0;
            var fileNameToOffset = new Dictionary<string, int>();
            int GetOrAddStringToHeap(string fileName)
            {
                if (!fileNameToOffset.TryGetValue(fileName, out int offset))
                {
                    fileNameToOffset.Add(fileName, offset = byteCounter);
                    byteCounter += sizeof(int) + Encoding.UTF8.GetByteCount(fileName);
                }
                return offset;
            }

            ObjectDataBuilder sequencePointsBuilder = new ObjectDataBuilder(factory, relocsOnly);
            foreach (var mappingEntry in mappingEntries)
            {
                IMethodNode entryPoint = factory.MethodEntrypoint(mappingEntry.Entity);
                objData.EmitReloc(entryPoint, reloc);
                objData.EmitInt(mappingEntry.MetadataHandle);

                var debug = entryPoint as INodeWithDebugInfo;
                int debugLocLength = debug?.DebugLocInfos?.Length ?? 0;
                if (debugLocLength == 0)
                {
                    objData.EmitInt(-1); // no sequence points available
                    continue;
                }

                objData.EmitInt(sequencePointsBuilder.CountBytes); // offset to sequence points

                /* ------------------------- Sequence Points emit ------------------------- */

                var fileName = debug.DebugLocInfos[0].FileName;

                var blockCount = sequencePointsBuilder.ReserveInt(); // number of consecutive sequence points blocks
                var consecutiveLength = sequencePointsBuilder.ReserveInt(); // length of current same-file consecutive debugLocs
                sequencePointsBuilder.EmitInt(GetOrAddStringToHeap(fileName)); // offset to current fileName on string heap

                int blockCounter = 1;
                int consecutiveCounter = 0;
                foreach (var loc in debug.DebugLocInfos)
                {
                    if (loc.FileName != fileName)
                    {
                        // record number of consecutive sequence points from the same file written and reset
                        sequencePointsBuilder.EmitInt(consecutiveLength, consecutiveCounter);
                        consecutiveCounter = 0;
                        blockCounter++;
                        fileName = loc.FileName;

                        consecutiveLength = sequencePointsBuilder.ReserveInt(); // length of debugLocs in the same file
                        sequencePointsBuilder.EmitInt(GetOrAddStringToHeap(fileName)); // offset to fileName on string heap
                    }

                    sequencePointsBuilder.EmitInt(loc.NativeOffset);
                    sequencePointsBuilder.EmitInt(loc.LineNumber);
                    consecutiveCounter++;
                }

                sequencePointsBuilder.EmitInt(blockCount, blockCounter);
                sequencePointsBuilder.EmitInt(consecutiveLength, consecutiveCounter);
            }

            objData.EmitInt(reservedSequencePointsOffset, objData.CountBytes);
            objData.EmitBytes(sequencePointsBuilder.ToObjectData().Data);

            objData.EmitInt(reservedStringOffset, objData.CountBytes);
            foreach (var kvp in fileNameToOffset.OrderBy(x => x.Value))
            {
                var bytes = Encoding.UTF8.GetBytes(kvp.Key);
                objData.EmitInt(bytes.Length);
                objData.EmitBytes(bytes);
            }

            _endSymbol.SetSymbolOffset(objData.CountBytes);
            return objData.ToObjectData();
        }
    }
}
