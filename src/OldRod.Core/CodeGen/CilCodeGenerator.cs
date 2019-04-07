// Project OldRod - A KoiVM devirtualisation utility.
// Copyright (C) 2019 Washi
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.Net;
using AsmResolver.Net.Cil;
using AsmResolver.Net.Cts;
using AsmResolver.Net.Signatures;
using OldRod.Core.Architecture;
using OldRod.Core.Ast.Cil;
using OldRod.Core.Disassembly.ControlFlow;
using OldRod.Core.Disassembly.DataFlow;
using Rivers;
using Rivers.Analysis;

namespace OldRod.Core.CodeGen
{
    public class CilCodeGenerator : ICilAstVisitor<IList<CilInstruction>>
    {
        private const string InvalidAstMessage =
            "The provided CIL AST is invalid or incomplete. " +
            "This might be because the IL to CIL recompiler contains a bug. " +
            "For more details, inspect the control flow graphs generated by the recompiler.";

        private readonly CilAstFormatter _formatter;
        private readonly CodeGenerationContext _context;

        private readonly IDictionary<Node, CilInstruction> _blockEntries = new Dictionary<Node, CilInstruction>();
        private readonly IDictionary<Node, CilInstruction> _blockExits = new Dictionary<Node, CilInstruction>();
        
        public CilCodeGenerator(CodeGenerationContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _formatter = new CilAstFormatter(context.MethodBody);
        }
        
        public IList<CilInstruction> VisitCompilationUnit(CilCompilationUnit unit)
        {
            // Add variable signatures to the end result.
            CreateVariables(unit);
            
            var result = GenerateInstructions(unit);

            CreateExceptionHandlers(unit);

            return result;
        }

        private void CreateVariables(CilCompilationUnit unit)
        {
            foreach (var variable in unit.Variables)
                _context.Variables.Add(variable.Signature);
        }

        private List<CilInstruction> GenerateInstructions(CilCompilationUnit unit)
        {
            var result = new List<CilInstruction>();

            // Define block headers to use as branch targets later.
            foreach (var node in unit.ControlFlowGraph.Nodes)
                _context.BlockHeaders[node] = CilInstruction.Create(CilOpCodes.Nop);

            // Traverse all blocks in an order that keeps dominance in mind.
            // This way, the resulting code has a more natural structure rather than
            // a somewhat arbitrary order of blocks. 

            var dominatorInfo = new DominatorInfo(unit.ControlFlowGraph.Entrypoint);
            var dominatorTree = dominatorInfo.ToDominatorTree();
            var comparer = new DominatorAwareNodeComparer(unit.ControlFlowGraph, dominatorInfo, dominatorTree);

            var stack = new Stack<Node>();
            stack.Push(dominatorTree.Nodes[unit.ControlFlowGraph.Entrypoint.Name]);

            int currentOffset = 0;

            while (stack.Count > 0)
            {
                var treeNode = stack.Pop();
                var cfgNode = unit.ControlFlowGraph.Nodes[treeNode.Name];
                var block = (CilAstBlock) cfgNode.UserData[CilAstBlock.AstBlockProperty];

                // Generate and add instructions of current block to result.
                var instructions = block.AcceptVisitor(this);
                _blockEntries[cfgNode] = instructions[0];
                _blockExits[cfgNode] = instructions[instructions.Count - 1];

                foreach (var instruction in instructions)
                {
                    result.Add(instruction);
                    instruction.Offset = currentOffset;
                    currentOffset += instruction.Size;
                }

                // Sort all successor by dominance.
                comparer.CurrentNode = cfgNode;
                var successors = treeNode.GetSuccessors()
                    .Select(n => unit.ControlFlowGraph.Nodes[n.Name])
                    .OrderBy(x => x, comparer)
                    #if DEBUG
                    .ToArray()
                    #endif
                    ;

                // Schedule the successors for code generation. 
                foreach (var successor in successors.Reverse())
                    stack.Push(dominatorTree.Nodes[successor.Name]);
            }

            return result;
        }

        private void CreateExceptionHandlers(CilCompilationUnit unit)
        {
            foreach (var subGraph in unit.ControlFlowGraph.SubGraphs)
            {
                var ehFrame = (EHFrame) subGraph.UserData[EHFrame.EHFrameProperty];
                ExceptionHandlerType type;
                switch (ehFrame.Type)
                {
                    case EHType.CATCH:
                        type = ExceptionHandlerType.Exception;
                        break;
                    case EHType.FILTER:
                        type = ExceptionHandlerType.Filter;
                        break;
                    case EHType.FAULT:
                        type = ExceptionHandlerType.Fault;
                        break;
                    case EHType.FINALLY:
                        type = ExceptionHandlerType.Finally;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                // Find first and last nodes of try block.
                var tryBody = (ICollection<Node>) subGraph.UserData[ControlFlowGraph.TryBlockProperty];
                var (tryStartNode, tryEndNode) = FindMinMaxNodes(tryBody);
                
                // Find first and last nodes of handler block.
                var handlerBody = (ICollection<Node>) subGraph.UserData[ControlFlowGraph.HandlerBlockProperty];
                var (handlerStartNode, handlerEndNode) = FindMinMaxNodes(handlerBody);

                // Create handler.
                var handler = new ExceptionHandler(type)
                {
                    TryStart = _blockEntries[tryStartNode],
                    TryEnd = _blockEntries[handlerStartNode], // TODO: Might have to use tryEndNode here instead.
                    HandlerStart = _blockEntries[handlerStartNode],
                    HandlerEnd = _blockEntries[handlerEndNode.GetSuccessors().First()]
                };

                _context.ExceptionHandlers.Add(ehFrame, handler);
            }
        }

        private static (Node minNode, Node maxNode) FindMinMaxNodes(ICollection<Node> nodes)
        {
            Node minNode = null;
            Node maxNode = null;
            int minOffset = int.MaxValue;
            int maxOffset = -1;
            foreach (var node in nodes)
            {
                var block = (CilAstBlock) node.UserData[CilAstBlock.AstBlockProperty];
                if (block.BlockHeader.Offset < minOffset)
                {
                    minNode = node;
                    minOffset = block.BlockHeader.Offset;
                }
                
                if (block.BlockHeader.Offset > maxOffset)
                {
                    maxNode = node;
                    maxOffset = block.BlockHeader.Offset;
                }
            }

            return (minNode, maxNode);
        }

        public IList<CilInstruction> VisitBlock(CilAstBlock block)
        {
            var result = new List<CilInstruction>();
            result.Add(block.BlockHeader);
            foreach (var statement in block.Statements)
                result.AddRange(statement.AcceptVisitor(this));
            return result;
        }

        public IList<CilInstruction> VisitExpressionStatement(CilExpressionStatement statement)
        {
            return statement.Expression.AcceptVisitor(this);
        }

        public IList<CilInstruction> VisitAssignmentStatement(CilAssignmentStatement statement)
        {
            var result = new List<CilInstruction>();
            result.AddRange(statement.Value.AcceptVisitor(this));
            result.Add(CilInstruction.Create(CilOpCodes.Stloc, statement.Variable.Signature));
            return result;
        }

        public IList<CilInstruction> VisitInstructionExpression(CilInstructionExpression expression)
        {
            var result = new List<CilInstruction>();

            // Sanity check for expression validity. 
            ValidateExpression(expression);

            // Decide whether to emit FL updates or not.
            if (expression.ShouldEmitFlagsUpdate)
            {
                var first = expression.Arguments[0];

                switch (expression.Arguments.Count)
                {
                    case 1:
                        result.AddRange(_context.BuildFlagAffectingExpression32(
                            first.AcceptVisitor(this),
                            expression.Instructions,
                            _context.Constants.GetFlagMask(expression.AffectedFlags), 
                            expression.ExpressionType != null));
                        break;
                    case 2:
                        var second = expression.Arguments[1];
                        
                        result.AddRange(_context.BuildFlagAffectingExpression32(
                            first.AcceptVisitor(this),
                            second.AcceptVisitor(this),
                            expression.Instructions,
                            _context.Constants.GetFlagMask(expression.AffectedFlags), 
                            expression.InvertedFlagsUpdate,
                            expression.ExpressionType != null));
                        break;
                }
            }
            else
            {
                foreach (var argument in expression.Arguments)
                    result.AddRange(argument.AcceptVisitor(this));
                result.AddRange(expression.Instructions);
            }
            
            return result;
        }

        public IList<CilInstruction> VisitUnboxToVmExpression(CilUnboxToVmExpression expression)
        {
            var convertMethod = _context.VmHelperType.Methods.First(x => x.Name == nameof(VmHelper.ConvertToVmType));
            
            var result = new List<CilInstruction>(expression.Expression.AcceptVisitor(this));
            
            if (expression.Type.IsTypeOf("System", "Object"))
            {
                var getType = _context.ReferenceImporter.ImportMethod(typeof(object).GetMethod("GetType"));
                
                var endif = CilInstruction.Create(CilOpCodes.Nop);
                var @else = CilInstruction.Create(CilOpCodes.Nop);
                result.AddRange(new[]
                {
                    CilInstruction.Create(CilOpCodes.Dup),
                    CilInstruction.Create(CilOpCodes.Brtrue_S, @else), 
                    CilInstruction.Create(CilOpCodes.Pop),
                    CilInstruction.Create(CilOpCodes.Ldnull),
                    CilInstruction.Create(CilOpCodes.Br_S, endif),
                    @else,
                    CilInstruction.Create(CilOpCodes.Dup),
                    CilInstruction.Create(CilOpCodes.Callvirt, getType),
                    CilInstruction.Create(CilOpCodes.Call, convertMethod),
                    endif
                });
            }
            else
            {
                var typeFromHandle = _context.ReferenceImporter.ImportMethod(typeof(Type).GetMethod("GetTypeFromHandle"));
                result.AddRange(new[]
                {
                    CilInstruction.Create(CilOpCodes.Ldtoken, expression.Type),
                    CilInstruction.Create(CilOpCodes.Call, typeFromHandle), 
                    CilInstruction.Create(CilOpCodes.Call, convertMethod),
                });
            }
            
            return result;
        }

        public IList<CilInstruction> VisitVariableExpression(CilVariableExpression expression)
        {
            return new[] {CilInstruction.Create(CilOpCodes.Ldloc, expression.Variable.Signature)};
        }

        private void ValidateExpression(CilInstructionExpression expression)
        {
            int stackSize = expression.Arguments.Count;
            foreach (var instruction in expression.Instructions)
            {
                stackSize += instruction.GetStackPopCount(_context.MethodBody);
                if (stackSize < 0)
                {
                    throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                        $"Insufficient arguments are pushed onto the stack'{expression.AcceptVisitor(_formatter)}'."));
                }

                stackSize += instruction.GetStackPushCount(_context.MethodBody);

                ValidateInstruction(expression, instruction);
            }
        }

        private void ValidateInstruction(CilInstructionExpression expression, CilInstruction instruction)
        {
            switch (instruction.OpCode.OperandType)
            {
                case CilOperandType.ShortInlineBrTarget:
                case CilOperandType.InlineBrTarget:
                    if (!(instruction.Operand is CilInstruction))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a branch target operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineMethod:
                case CilOperandType.InlineField:
                case CilOperandType.InlineType:
                case CilOperandType.InlineTok:
                    if (!(instruction.Operand is IMemberReference))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a member reference operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineSig:
                    if (!(instruction.Operand is StandAloneSignature))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a signature operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineI:
                    if (!(instruction.Operand is int))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected an int32 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineI8:
                    if (!(instruction.Operand is long))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected an int64 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineNone:
                    if (instruction.Operand != null)
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Unexpected operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;

                case CilOperandType.InlineR:
                    if (!(instruction.Operand is double))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a float64 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.ShortInlineI:
                    if (!(instruction.Operand is sbyte))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected an int8 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.ShortInlineR:
                    if (!(instruction.Operand is float))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a float32 operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineString:
                    if (!(instruction.Operand is string))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a string operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineSwitch:
                    if (!(instruction.Operand is IList<CilInstruction>))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a switch table operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;

                case CilOperandType.ShortInlineVar:
                case CilOperandType.InlineVar:
                    if (!(instruction.Operand is VariableSignature))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a variable operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;
                case CilOperandType.InlineArgument:
                case CilOperandType.ShortInlineArgument:
                    if (!(instruction.Operand is ParameterSignature))
                    {
                        throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                            $"Expected a parameter operand in '{expression.AcceptVisitor(_formatter)}'."));
                    }

                    break;

                default:
                    throw new CilCodeGeneratorException(InvalidAstMessage, new ArgumentException(
                        $"Unexpected opcode in '{expression.AcceptVisitor(_formatter)}'."));
            }
        }
        
    }
}