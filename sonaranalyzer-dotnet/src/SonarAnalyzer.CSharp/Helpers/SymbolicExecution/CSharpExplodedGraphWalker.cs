﻿/*
 * SonarAnalyzer for .NET
 * Copyright (C) 2015-2017 SonarSource SA
 * mailto: contact AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SonarAnalyzer.Helpers.FlowAnalysis.Common;

namespace SonarAnalyzer.Helpers.FlowAnalysis.CSharp
{
    internal class CSharpExplodedGraphWalker : AbstractExplodedGraphWalker
    {
        private readonly IEnumerable<ConstraintDecorator> decorators;
        protected override IEnumerable<ConstraintDecorator> ConstraintDecorators => decorators;

        public CSharpExplodedGraphWalker(IControlFlowGraph cfg, ISymbol declaration, SemanticModel semanticModel,
            Common.LiveVariableAnalysis lva)
            : base(cfg, declaration, semanticModel, lva)
        {
            decorators = ImmutableList.Create<ConstraintDecorator>(
                new ObjectConstraintDecorator(this),
                new NullableConstraintDecorator(this),
                new BooleanConstraintDecorator(this),
                new CollectionConstraintDecorator(this),
                new DisposableConstraintDecorator(this));
        }

        #region Visit*

        protected override void VisitSimpleBlock(SimpleBlock block, ExplodedGraphNode node)
        {
            var newProgramState = node.ProgramState;

            var usingFinalizerBlock = block as UsingEndBlock;
            if (usingFinalizerBlock != null)
            {
                // TODO: using block
                //newProgramState = InvokeChecks(newProgramState, (ps, check) => check.PreProcessUsingStatement(node.ProgramPoint, ps));
                newProgramState = CleanStateAfterBlock(newProgramState, block);
                EnqueueAllSuccessors(block, newProgramState);
                return;
            }

            newProgramState = CleanStateAfterBlock(newProgramState, block);

            if (block is ForeachCollectionProducerBlock)
            {
                newProgramState = newProgramState.PopValue();
                EnqueueAllSuccessors(block, newProgramState);
                return;
            }

            var forInitializerBlock = block as ForInitializerBlock;
            if (forInitializerBlock != null)
            {
                newProgramState = newProgramState
                    .PopValues(forInitializerBlock.ForNode.Initializers.Count)
                    .PushValues(Enumerable.Range(0, forInitializerBlock.ForNode.Incrementors.Count)
                                          .Select(i => new SymbolicValue()));

                EnqueueAllSuccessors(forInitializerBlock, newProgramState);
                return;
            }

            var lockBlock = block as LockBlock;
            if (lockBlock != null)
            {
                newProgramState = newProgramState
                    .PopValue()
                    .RemoveSymbols(IsFieldSymbol);

                EnqueueAllSuccessors(block, newProgramState);
                return;
            }

            base.VisitSimpleBlock(block, node);
        }

        protected override void VisitBinaryBranch(BinaryBranchBlock binaryBranchBlock, ExplodedGraphNode node)
        {
            var newProgramState = CleanStateAfterBlock(node.ProgramState, node.ProgramPoint.Block);

            switch (binaryBranchBlock.BranchingNode.Kind())
            {
                case SyntaxKind.ForEachStatement:
                    VisitForeachBinaryBranch(binaryBranchBlock, newProgramState);
                    return;
                case SyntaxKind.CoalesceExpression:
                    VisitCoalesceExpressionBinaryBranch(binaryBranchBlock, newProgramState);
                    return;
                case SyntaxKind.ConditionalAccessExpression:
                    VisitConditionalAccessBinaryBranch(binaryBranchBlock, newProgramState);
                    return;

                case SyntaxKind.LogicalAndExpression:
                case SyntaxKind.LogicalOrExpression:
                    VisitBinaryBranch(binaryBranchBlock, node,
                        ((BinaryExpressionSyntax)binaryBranchBlock.BranchingNode).Left);
                    return;

                case SyntaxKind.WhileStatement:
                    VisitBinaryBranch(binaryBranchBlock, node,
                        ((WhileStatementSyntax)binaryBranchBlock.BranchingNode).Condition);
                    return;
                case SyntaxKind.DoStatement:
                    VisitBinaryBranch(binaryBranchBlock, node,
                        ((DoStatementSyntax)binaryBranchBlock.BranchingNode).Condition);
                    return;
                case SyntaxKind.ForStatement:
                    VisitBinaryBranch(binaryBranchBlock, node,
                        ((ForStatementSyntax)binaryBranchBlock.BranchingNode).Condition);
                    return;

                case SyntaxKind.IfStatement:
                    VisitBinaryBranch(binaryBranchBlock, node,
                        ((IfStatementSyntax)binaryBranchBlock.BranchingNode).Condition);
                    return;
                case SyntaxKind.ConditionalExpression:
                    VisitBinaryBranch(binaryBranchBlock, node,
                        ((ConditionalExpressionSyntax)binaryBranchBlock.BranchingNode).Condition);
                    return;

                default:
                    Debug.Fail($"Branch kind '{binaryBranchBlock.BranchingNode.Kind()}' not handled");
                    VisitBinaryBranch(binaryBranchBlock, node, null);
                    return;
            }
        }

        protected override void VisitInstruction(ExplodedGraphNode node)
        {
            var instruction = node.ProgramPoint.Block.Instructions[node.ProgramPoint.Offset];
            var expression = instruction as ExpressionSyntax;
            var parenthesizedExpression = expression?.GetSelfOrTopParenthesizedExpression();
            var newProgramPoint = new ProgramPoint(node.ProgramPoint.Block, node.ProgramPoint.Offset + 1);
            var newProgramState = node.ProgramState;

            newProgramState = ConstraintDecorators.Aggregate(newProgramState,
                (ps, decorator) => decorator.PreProcessInstruction(node, ps));

            switch (instruction.Kind())
            {
                case SyntaxKind.CastExpression:
                case SyntaxKind.CheckedExpression:
                case SyntaxKind.UncheckedExpression:
                    // Do nothing
                    break;

                case SyntaxKind.VariableDeclarator:
                    newProgramState = VisitVariableDeclarator((VariableDeclaratorSyntax)instruction, newProgramState);
                    break;
                case SyntaxKind.SimpleAssignmentExpression:
                    newProgramState = VisitSimpleAssignment((AssignmentExpressionSyntax)instruction, newProgramState);
                    break;

                case SyntaxKind.OrAssignmentExpression:
                    newProgramState = VisitBooleanBinaryOpAssignment(newProgramState,
                        (AssignmentExpressionSyntax)instruction, (l, r) => new OrSymbolicValue(l, r));
                    break;
                case SyntaxKind.AndAssignmentExpression:
                    newProgramState = VisitBooleanBinaryOpAssignment(newProgramState,
                        (AssignmentExpressionSyntax)instruction, (l, r) => new AndSymbolicValue(l, r));
                    break;
                case SyntaxKind.ExclusiveOrAssignmentExpression:
                    newProgramState = VisitBooleanBinaryOpAssignment(newProgramState,
                        (AssignmentExpressionSyntax)instruction, (l, r) => new XorSymbolicValue(l, r));
                    break;

                case SyntaxKind.SubtractAssignmentExpression:
                case SyntaxKind.AddAssignmentExpression:
                case SyntaxKind.DivideAssignmentExpression:
                case SyntaxKind.MultiplyAssignmentExpression:
                case SyntaxKind.ModuloAssignmentExpression:
                case SyntaxKind.LeftShiftAssignmentExpression:
                case SyntaxKind.RightShiftAssignmentExpression:
                    newProgramState = VisitOpAssignment((AssignmentExpressionSyntax)instruction, newProgramState);
                    break;

                case SyntaxKind.PreIncrementExpression:
                case SyntaxKind.PreDecrementExpression:
                    newProgramState = VisitPrefixIncrement((PrefixUnaryExpressionSyntax)instruction, newProgramState);
                    break;

                case SyntaxKind.PostIncrementExpression:
                case SyntaxKind.PostDecrementExpression:
                    newProgramState = VisitPostfixIncrement((PostfixUnaryExpressionSyntax)instruction, newProgramState);
                    break;

                case SyntaxKind.IdentifierName:
                    newProgramState = VisitIdentifier((IdentifierNameSyntax)instruction, newProgramState);
                    break;

                case SyntaxKind.BitwiseOrExpression:
                    newProgramState = VisitBinaryOperator(newProgramState, (l, r) => new OrSymbolicValue(l, r));
                    break;
                case SyntaxKind.BitwiseAndExpression:
                    newProgramState = VisitBinaryOperator(newProgramState, (l, r) => new AndSymbolicValue(l, r));
                    break;
                case SyntaxKind.ExclusiveOrExpression:
                    newProgramState = VisitBinaryOperator(newProgramState, (l, r) => new XorSymbolicValue(l, r));
                    break;

                case SyntaxKind.LessThanExpression:
                    newProgramState = VisitComparisonBinaryOperator(newProgramState,
                        (BinaryExpressionSyntax)instruction,
                        (l, r) => new ComparisonSymbolicValue(ComparisonKind.Less, l, r));
                    break;
                case SyntaxKind.LessThanOrEqualExpression:
                    newProgramState = VisitComparisonBinaryOperator(newProgramState,
                        (BinaryExpressionSyntax)instruction,
                        (l, r) => new ComparisonSymbolicValue(ComparisonKind.LessOrEqual, l, r));
                    break;
                case SyntaxKind.GreaterThanExpression:
                    newProgramState = VisitComparisonBinaryOperator(newProgramState,
                        (BinaryExpressionSyntax)instruction,
                        (l, r) => new ComparisonSymbolicValue(ComparisonKind.Less, r, l));
                    break;
                case SyntaxKind.GreaterThanOrEqualExpression:
                    newProgramState = VisitComparisonBinaryOperator(newProgramState,
                        (BinaryExpressionSyntax)instruction,
                        (l, r) => new ComparisonSymbolicValue(ComparisonKind.LessOrEqual, r, l));
                    break;

                case SyntaxKind.SubtractExpression:
                case SyntaxKind.AddExpression:
                case SyntaxKind.DivideExpression:
                case SyntaxKind.MultiplyExpression:
                case SyntaxKind.ModuloExpression:
                case SyntaxKind.LeftShiftExpression:
                case SyntaxKind.RightShiftExpression:
                    newProgramState = newProgramState
                        .PopValues(2)
                        .PushValue(new SymbolicValue());
                    break;

                case SyntaxKind.EqualsExpression:
                    var binary = (BinaryExpressionSyntax)instruction;
                    newProgramState = IsOperatorOnObject(instruction)
                        ? VisitReferenceEquals(binary, newProgramState)
                        : VisitValueEquals(newProgramState);

                    break;

                case SyntaxKind.NotEqualsExpression:
                    newProgramState = IsOperatorOnObject(instruction)
                        ? VisitBinaryOperator(newProgramState, (l, r) => new ReferenceNotEqualsSymbolicValue(l, r))
                        : VisitBinaryOperator(newProgramState, (l, r) => new ValueNotEqualsSymbolicValue(l, r));
                    break;

                case SyntaxKind.BitwiseNotExpression:
                case SyntaxKind.UnaryMinusExpression:
                case SyntaxKind.UnaryPlusExpression:
                case SyntaxKind.AddressOfExpression:
                case SyntaxKind.PointerIndirectionExpression:
                case SyntaxKind.MakeRefExpression:
                case SyntaxKind.RefTypeExpression:
                case SyntaxKind.RefValueExpression:
                case SyntaxKind.MemberBindingExpression:
                case SyntaxKind.AwaitExpression:
                case SyntaxKind.AsExpression:
                case SyntaxKind.IsExpression:
                    newProgramState = newProgramState
                        .PopValue()
                        .PushValue(new SymbolicValue());
                    break;

                case SyntaxKind.SimpleMemberAccessExpression:
                case SyntaxKind.PointerMemberAccessExpression:
                    newProgramState = VisitMemberAccess((MemberAccessExpressionSyntax)instruction, newProgramState);
                    break;


                case SyntaxKind.LogicalNotExpression:
                    {
                        SymbolicValue sv;
                        newProgramState = newProgramState
                            .PopValue(out sv)
                            .PushValue(new LogicalNotSymbolicValue(sv));
                    }
                    break;

                case SyntaxKind.TrueLiteralExpression:
                    newProgramState = newProgramState.PushValue(SymbolicValue.True);
                    break;
                case SyntaxKind.FalseLiteralExpression:
                    newProgramState = newProgramState.PushValue(SymbolicValue.False);
                    break;
                case SyntaxKind.NullLiteralExpression:
                    newProgramState = newProgramState.PushValue(SymbolicValue.Null);
                    break;

                case SyntaxKind.ThisExpression:
                    newProgramState = newProgramState.PushValue(SymbolicValue.This);
                    break;
                case SyntaxKind.BaseExpression:
                    newProgramState = newProgramState.PushValue(SymbolicValue.Base);
                    break;

                case SyntaxKind.GenericName:
                case SyntaxKind.AliasQualifiedName:
                case SyntaxKind.QualifiedName:
                case SyntaxKind.PredefinedType:
                case SyntaxKind.NullableType:
                case SyntaxKind.OmittedArraySizeExpression:
                case SyntaxKind.AnonymousMethodExpression:
                case SyntaxKind.ParenthesizedLambdaExpression:
                case SyntaxKind.SimpleLambdaExpression:
                case SyntaxKind.QueryExpression:
                case SyntaxKind.ArgListExpression:
                case SyntaxKind.CharacterLiteralExpression:
                case SyntaxKind.StringLiteralExpression:
                case SyntaxKind.NumericLiteralExpression:
                case SyntaxKind.SizeOfExpression:
                case SyntaxKind.TypeOfExpression:
                case SyntaxKind.ArrayCreationExpression:
                case SyntaxKind.ImplicitArrayCreationExpression:
                case SyntaxKind.StackAllocArrayCreationExpression:
                case SyntaxKind.DefaultExpression:
                    newProgramState = newProgramState.PushValue(new SymbolicValue());
                    break;

                case SyntaxKind.AnonymousObjectCreationExpression:
                    newProgramState = newProgramState
                        .PopValues(((AnonymousObjectCreationExpressionSyntax)instruction).Initializers.Count)
                        .PushValue(new SymbolicValue());
                    break;

                case SyntaxKind.InterpolatedStringExpression:
                    newProgramState = newProgramState
                        .PopValues(((InterpolatedStringExpressionSyntax)instruction).Contents.OfType<InterpolationSyntax>().Count())
                        .PushValue(new SymbolicValue());
                    break;

                case SyntaxKind.ObjectCreationExpression:
                    newProgramState = VisitObjectCreation((ObjectCreationExpressionSyntax)instruction, newProgramState);
                    break;

                case SyntaxKind.ElementAccessExpression:
                    newProgramState = newProgramState
                        .PopValues((((ElementAccessExpressionSyntax)instruction).ArgumentList?.Arguments.Count ?? 0) + 1)
                        .PushValue(new SymbolicValue());
                    break;

                case SyntaxKind.ImplicitElementAccess:
                    newProgramState = newProgramState
                        .PopValues(((ImplicitElementAccessSyntax)instruction).ArgumentList?.Arguments.Count ?? 0)
                        .PushValue(new SymbolicValue());
                    break;

                case SyntaxKind.ObjectInitializerExpression:
                case SyntaxKind.ArrayInitializerExpression:
                case SyntaxKind.CollectionInitializerExpression:
                case SyntaxKind.ComplexElementInitializerExpression:
                    newProgramState = VisitInitializer(instruction, parenthesizedExpression, newProgramState);
                    break;

                case SyntaxKind.ArrayType:
                    newProgramState = newProgramState
                        .PopValues(((ArrayTypeSyntax)instruction).RankSpecifiers.SelectMany(rs => rs.Sizes).Count());
                    break;

                case SyntaxKind.ElementBindingExpression:
                    newProgramState = newProgramState
                        .PopValues(((ElementBindingExpressionSyntax)instruction).ArgumentList?.Arguments.Count ?? 0)
                        .PushValue(new SymbolicValue());
                    break;

                case SyntaxKind.InvocationExpression:
                    {
                        var invocation = (InvocationExpressionSyntax)instruction;
                        var invocationVisitor = new InvocationVisitor(invocation, SemanticModel, newProgramState);
                        newProgramState = invocationVisitor.ProcessInvocation();

                        if (invocation.Expression.IsOnThis() && !invocation.IsNameof(SemanticModel))
                        {
                            newProgramState = newProgramState.RemoveSymbols(IsFieldSymbol);
                        }
                    }
                    break;

                default:
                    throw new NotImplementedException($"{instruction.Kind()}");
            }

            newProgramState = ConstraintDecorators.Aggregate(newProgramState,
                (ps, decorator) => decorator.PostProcessInstruction(node, ps));

            newProgramState = EnsureStackState(parenthesizedExpression, newProgramState);

            OnInstructionProcessed(instruction, node.ProgramPoint, newProgramState);
            EnqueueNewNode(newProgramPoint, newProgramState);
        }

        private ProgramState EnsureStackState(ExpressionSyntax parenthesizedExpression, ProgramState programState)
        {
            if (ShouldConsumeValue(parenthesizedExpression))
            {
                var newProgramState = programState.PopValue();
                System.Diagnostics.Debug.Assert(!newProgramState.HasValue);

                return newProgramState;
            }

            return programState;
        }

        #region Handle VisitBinaryBranch cases

        private void VisitForeachBinaryBranch(BinaryBranchBlock binaryBranchBlock, ProgramState programState)
        {
            // foreach variable is not a VariableDeclarator, so we need to assign a value to it
            var foreachVariableSymbol = SemanticModel.GetDeclaredSymbol(binaryBranchBlock.BranchingNode);
            var sv = new SymbolicValue();
            var newProgramState = StoreSymbolicValueIfSymbolIsTracked(foreachVariableSymbol, sv, programState);

            EnqueueAllSuccessors(binaryBranchBlock, newProgramState);
        }

        private void VisitCoalesceExpressionBinaryBranch(BinaryBranchBlock binaryBranchBlock, ProgramState programState)
        {
            SymbolicValue sv;
            var ps = programState.PopValue(out sv);

            foreach (var newProgramState in sv.TrySetConstraint(ObjectConstraint.Null, ps))
            {
                EnqueueNewNode(new ProgramPoint(binaryBranchBlock.TrueSuccessorBlock), newProgramState);
            }

            foreach (var newProgramState in sv.TrySetConstraint(ObjectConstraint.NotNull, ps))
            {
                var nps = newProgramState;

                if (!ShouldConsumeValue((BinaryExpressionSyntax)binaryBranchBlock.BranchingNode))
                {
                    nps = nps.PushValue(sv);
                }
                EnqueueNewNode(new ProgramPoint(binaryBranchBlock.FalseSuccessorBlock), nps);
            }
        }

        private void VisitConditionalAccessBinaryBranch(BinaryBranchBlock binaryBranchBlock,
            ProgramState programState)
        {
            SymbolicValue sv;
            var ps = programState.PopValue(out sv);

            foreach (var newProgramState in sv.TrySetConstraint(ObjectConstraint.Null, ps))
            {
                var nps = newProgramState;

                if (!ShouldConsumeValue((ConditionalAccessExpressionSyntax)binaryBranchBlock.BranchingNode))
                {
                    nps = nps.PushValue(SymbolicValue.Null);
                }
                EnqueueNewNode(new ProgramPoint(binaryBranchBlock.TrueSuccessorBlock), nps);
            }

            foreach (var newProgramState in sv.TrySetConstraint(ObjectConstraint.NotNull, ps))
            {
                EnqueueNewNode(new ProgramPoint(binaryBranchBlock.FalseSuccessorBlock), newProgramState);
            }
        }

        private void VisitBinaryBranch(BinaryBranchBlock binaryBranchBlock, ExplodedGraphNode node,
            SyntaxNode instruction)
        {
            var ps = node.ProgramState;
            SymbolicValue sv;

            var forStatement = binaryBranchBlock.BranchingNode as ForStatementSyntax;
            if (forStatement != null)
            {
                if (forStatement.Condition == null)
                {
                    ps = ps.PushValue(SymbolicValue.True);
                }
                ps = ps.PopValue(out sv);
                ps = ps.PopValues(forStatement.Incrementors.Count);
            }
            else
            {
                ps = ps.PopValue(out sv);
            }

            foreach (var newProgramState in sv.TrySetConstraint(BoolConstraint.True, ps))
            {
                OnConditionEvaluated(instruction, evaluationValue: true);

                var nps = binaryBranchBlock.BranchingNode.IsKind(SyntaxKind.LogicalOrExpression)
                    ? newProgramState.PushValue(SymbolicValue.True)
                    : newProgramState;

                EnqueueNewNode(new ProgramPoint(binaryBranchBlock.TrueSuccessorBlock),
                    CleanStateAfterBlock(nps, node.ProgramPoint.Block));
            }

            foreach (var newProgramState in sv.TrySetConstraint(BoolConstraint.False, ps))
            {
                OnConditionEvaluated(instruction, evaluationValue: false);

                var nps = binaryBranchBlock.BranchingNode.IsKind(SyntaxKind.LogicalAndExpression)
                    ? newProgramState.PushValue(SymbolicValue.False)
                    : newProgramState;

                EnqueueNewNode(new ProgramPoint(binaryBranchBlock.FalseSuccessorBlock),
                    CleanStateAfterBlock(nps, node.ProgramPoint.Block));
            }
        }

        #endregion

        #region VisitExpression

        private ProgramState VisitMemberAccess(MemberAccessExpressionSyntax memberAccess, ProgramState programState)
        {
            SymbolicValue memberExpression;
            var newProgramState = programState.PopValue(out memberExpression);
            SymbolicValue sv = null;
            var identifier = memberAccess.Name as IdentifierNameSyntax;
            if (identifier != null)
            {
                var symbol = SemanticModel.GetSymbolInfo(identifier).Symbol;
                var fieldSymbol = symbol as IFieldSymbol;
                if (fieldSymbol != null && (memberAccess.IsOnThis() || fieldSymbol.IsConst))
                {
                    sv = newProgramState.GetSymbolValue(symbol);
                    if (sv == null)
                    {
                        newProgramState = CreateAndStoreFieldSymbolicValue(newProgramState, fieldSymbol, out sv);
                    }
                }
            }
            if (sv == null)
            {
                sv = new MemberAccessSymbolicValue(memberExpression, memberAccess.Name.Identifier.ValueText);
            }

            return newProgramState.PushValue(sv);
        }

        private bool IsOperatorOnObject(SyntaxNode instruction)
        {
            var operatorSymbol = SemanticModel.GetSymbolInfo(instruction).Symbol as IMethodSymbol;
            return operatorSymbol != null &&
                operatorSymbol.ContainingType.Is(KnownType.System_Object);
        }

        private static ProgramState VisitValueEquals(ProgramState programState)
        {
            SymbolicValue leftSymbol;
            SymbolicValue rightSymbol;

            var newProgramState = programState
                .PopValue(out rightSymbol)
                .PopValue(out leftSymbol);

            var equals = new ValueEqualsSymbolicValue(leftSymbol, rightSymbol);
            newProgramState = newProgramState.PushValue(equals);
            return InvocationVisitor.SetConstraintOnValueEquals(equals, newProgramState);
        }

        private ProgramState VisitReferenceEquals(BinaryExpressionSyntax equals, ProgramState programState)
        {
            SymbolicValue leftSymbol;
            SymbolicValue rightSymbol;

            var newProgramState = programState
                .PopValue(out rightSymbol)
                .PopValue(out leftSymbol);

            return new InvocationVisitor.ReferenceEqualsConstraintHandler(leftSymbol, rightSymbol,
                equals.Left, equals.Right, newProgramState, SemanticModel).PushWithConstraint();
        }

        private ProgramState VisitComparisonBinaryOperator(ProgramState programState, BinaryExpressionSyntax comparison,
            Func<SymbolicValue, SymbolicValue, SymbolicValue> svFactory)
        {
            SymbolicValue leftSymbol;
            SymbolicValue rightSymbol;

            var newProgramState = programState
                .PopValue(out rightSymbol)
                .PopValue(out leftSymbol);

            var op = SemanticModel.GetSymbolInfo(comparison).Symbol as IMethodSymbol;

            var isValueTypeOperator = op?.ContainingType?.IsValueType ?? false;

            var isLiftedOperator = isValueTypeOperator &&
                (leftSymbol.IsNull(programState) || rightSymbol.IsNull(programState));

            var comparisonValue = isLiftedOperator ? SymbolicValue.False : svFactory(leftSymbol, rightSymbol);

            return newProgramState.PushValue(comparisonValue);
        }

        private static ProgramState VisitBinaryOperator(ProgramState programState,
            Func<SymbolicValue, SymbolicValue, SymbolicValue> svFactory)
        {
            SymbolicValue leftSymbol;
            SymbolicValue rightSymbol;

            return programState
                .PopValue(out rightSymbol)
                .PopValue(out leftSymbol)
                .PushValue(svFactory(leftSymbol, rightSymbol));
        }

        private ProgramState VisitBooleanBinaryOpAssignment(ProgramState programState,
            AssignmentExpressionSyntax assignment,
            Func<SymbolicValue, SymbolicValue, SymbolicValue> symbolicValueFactory)
        {
            var symbol = SemanticModel.GetSymbolInfo(assignment.Left).Symbol;

            SymbolicValue leftSymbol;
            SymbolicValue rightSymbol;

            var newProgramState = programState
                .PopValue(out rightSymbol)
                .PopValue(out leftSymbol);

            var sv = symbolicValueFactory(leftSymbol, rightSymbol);
            newProgramState = newProgramState.PushValue(sv);

            return StoreSymbolicValueIfSymbolIsTracked(symbol, sv, newProgramState);
        }

        private ProgramState VisitObjectCreation(ObjectCreationExpressionSyntax ctor, ProgramState programState)
        {
            return programState
                .PopValues(ctor.ArgumentList?.Arguments.Count ?? 0)
                .PushValue(new SymbolicValue());
        }

        private static ProgramState VisitInitializer(SyntaxNode instruction, ExpressionSyntax parenthesizedExpression,
            ProgramState programState)
        {
            var init = (InitializerExpressionSyntax)instruction;
            var newProgramState = programState.PopValues(init.Expressions.Count);

            if (!(parenthesizedExpression.Parent is ObjectCreationExpressionSyntax) &&
                !(parenthesizedExpression.Parent is ArrayCreationExpressionSyntax) &&
                !(parenthesizedExpression.Parent is AnonymousObjectCreationExpressionSyntax) &&
                !(parenthesizedExpression.Parent is ImplicitArrayCreationExpressionSyntax))
            {
                newProgramState = newProgramState.PushValue(new SymbolicValue());
            }

            return newProgramState;
        }

        private ProgramState VisitIdentifier(IdentifierNameSyntax identifier, ProgramState programState)
        {
            var newProgramState = programState;
            var symbol = SemanticModel.GetSymbolInfo(identifier).Symbol;
            var typeSymbol = SemanticModel.GetTypeInfo(identifier).Type;
            var sv = newProgramState.GetSymbolValue(symbol);

            if (sv == null)
            {
                var fieldSymbol = symbol as IFieldSymbol;
                if (fieldSymbol != null) // TODO: Fix me when implementing SLVS-1130
                {
                    newProgramState = CreateAndStoreFieldSymbolicValue(newProgramState, fieldSymbol, out sv);
                }
                else
                {
                    sv = new SymbolicValue();
                }
            }
            newProgramState = newProgramState.PushValue(sv);

            var parenthesized = identifier.GetSelfOrTopParenthesizedExpression();
            var argument = parenthesized.Parent as ArgumentSyntax;
            if (argument == null ||
                argument.RefOrOutKeyword.IsKind(SyntaxKind.None))
            {
                return newProgramState;
            }

            sv = new SymbolicValue();
            newProgramState = newProgramState
                .PopValue()
                .PushValue(sv);
            return StoreSymbolicValueIfSymbolIsTracked(symbol, sv, newProgramState);
        }

        private ProgramState VisitPostfixIncrement(PostfixUnaryExpressionSyntax unary, ProgramState programState)
        {
            var symbol = SemanticModel.GetSymbolInfo(unary.Operand).Symbol;
            var sv = new SymbolicValue();

            return StoreSymbolicValueIfSymbolIsTracked(symbol, sv, programState);
        }

        private ProgramState VisitPrefixIncrement(PrefixUnaryExpressionSyntax unary, ProgramState programState)
        {
            var symbol = SemanticModel.GetSymbolInfo(unary.Operand).Symbol;
            var sv = new SymbolicValue();
            var newProgramState = programState
                .PopValue()
                .PushValue(sv);

            return StoreSymbolicValueIfSymbolIsTracked(symbol, sv, newProgramState);
        }

        private ProgramState VisitOpAssignment(AssignmentExpressionSyntax assignment, ProgramState programState)
        {
            var sv = new SymbolicValue();
            var newProgramState = programState
                .PopValues(2)
                .PushValue(sv);
            var leftSymbol = SemanticModel.GetSymbolInfo(assignment.Left).Symbol;

            return StoreSymbolicValueIfSymbolIsTracked(leftSymbol, sv, newProgramState);
        }

        private ProgramState VisitSimpleAssignment(AssignmentExpressionSyntax assignment, ProgramState programState)
        {
            SymbolicValue sv;
            var newProgramState = programState.PopValue(out sv);
            if (!ControlFlowGraphBuilder.IsAssignmentWithSimpleLeftSide(assignment))
            {
                newProgramState = newProgramState.PopValue();
            }

            var leftSymbol = SemanticModel.GetSymbolInfo(assignment.Left).Symbol;
            if (leftSymbol.IsNullable())
            {
                sv = new SymbolicValue(sv);
            }

            newProgramState = newProgramState.PushValue(sv);

            return StoreSymbolicValueIfSymbolIsTracked(leftSymbol, sv, newProgramState);
        }

        private ProgramState VisitVariableDeclarator(VariableDeclaratorSyntax declarator, ProgramState programState)
        {
            if (declarator.Initializer?.Value == null)
            {
                return programState;
            }

            SymbolicValue sv;
            var newProgramState = programState.PopValue(out sv);

            var leftSymbol = SemanticModel.GetDeclaredSymbol(declarator);
            var rightSymbol = SemanticModel.GetSymbolInfo(declarator.Initializer.Value).Symbol;

            if (leftSymbol == null ||
                (rightSymbol == null && sv == null) ||
                !IsSymbolTracked(leftSymbol))
            {
                return programState;
            }

            if (leftSymbol.IsNullable() &&
                (!rightSymbol.IsNullable() || (rightSymbol == null && sv != null)))
            {
                sv = new SymbolicValue(sv);
            }

            return newProgramState.StoreSymbolicValue(leftSymbol, sv);
        }

        #endregion

        protected override bool IsValueConsumingStatement(SyntaxNode jumpNode)
        {
            if (jumpNode.IsKind(SyntaxKind.LockStatement))
            {
                return true;
            }

            var usingStatement = jumpNode as UsingStatementSyntax;
            if (usingStatement != null)
            {
                return usingStatement.Expression != null;
            }

            var throwStatement = jumpNode as ThrowStatementSyntax;
            if (throwStatement != null)
            {
                return throwStatement.Expression != null;
            }

            var returnStatement = jumpNode as ReturnStatementSyntax;
            if (returnStatement != null)
            {
                return returnStatement.Expression != null;
            }

            var switchStatement = jumpNode as SwitchStatementSyntax;
            if (switchStatement != null)
            {
                return switchStatement.Expression != null;
            }

            // goto is not putting the expression to the CFG

            return false;
        }

        private static bool ShouldConsumeValue(ExpressionSyntax expression)
        {
            if (expression == null)
            {
                return false;
            }

            var parent = expression.Parent;
            var conditionalAccess = parent as ConditionalAccessExpressionSyntax;
            if (conditionalAccess != null &&
                conditionalAccess.WhenNotNull == expression)
            {
                return ShouldConsumeValue(conditionalAccess.GetSelfOrTopParenthesizedExpression());
            }

            return parent is ExpressionStatementSyntax ||
                parent is YieldStatementSyntax;
        }

        public static ProgramState CreateAndStoreFieldSymbolicValue(ProgramState programState, IFieldSymbol fieldSymbol,
            out SymbolicValue symbolicValue)
        {
            if (!fieldSymbol.IsConst ||
                !fieldSymbol.HasConstantValue)
            {
                // TODO: handle readonly initialized inline with null
                symbolicValue = new SymbolicValue();
            }
            else
            {
                var boolValue = fieldSymbol.ConstantValue as bool?;
                if (boolValue.HasValue)
                {
                    symbolicValue = boolValue.Value
                        ? SymbolicValue.True
                        : SymbolicValue.False;
                }
                else
                {
                    symbolicValue = fieldSymbol.ConstantValue == null
                        ? SymbolicValue.Null
                        : new SymbolicValue();
                }
            }

            return programState.StoreSymbolicValue(fieldSymbol, symbolicValue);
        }

        #endregion
    }
}