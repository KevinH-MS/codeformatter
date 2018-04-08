// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.DotNet.CodeFormatting.Rules
{
    internal partial class FieldNamingRule
    {
        private sealed class CSharpRule : CommonRule
        {
            protected override SyntaxNode AddFieldAnnotations(SyntaxNode syntaxNode, out int count)
            {
                return CSharpFieldAnnotationsRewriter.AddAnnotations(syntaxNode, out count);
            }

            protected override SyntaxNode RemoveRenameAnnotations(SyntaxNode syntaxNode)
            {
                var rewriter = new CSharpRemoveRenameAnnotationsRewriter();
                return rewriter.Visit(syntaxNode);
            }
        }

        /// <summary>
        /// This will add an annotation to any private field that needs to be renamed.
        /// </summary>
        internal sealed class CSharpFieldAnnotationsRewriter : CSharpSyntaxRewriter
        {
            private int _count;

            internal static SyntaxNode AddAnnotations(SyntaxNode node, out int count)
            {
                var rewriter = new CSharpFieldAnnotationsRewriter();
                var newNode = rewriter.Visit(node);
                count = rewriter._count;
                return newNode;
            }

            public override SyntaxNode VisitFieldDeclaration(FieldDeclarationSyntax node)
            {
                bool isPublicOrConst;
                if (NeedsRewrite(node, out isPublicOrConst))
                {
                    var list = new List<VariableDeclaratorSyntax>(node.Declaration.Variables.Count);
                    foreach (var v in node.Declaration.Variables)
                    {
                        if (IsGoodFieldName(v.Identifier.Text, isPublicOrConst))
                        {
                            list.Add(v);
                        }
                        else
                        {
                            list.Add(v.WithAdditionalAnnotations(s_markerAnnotation));
                            _count++;
                        }
                    }

                    var declaration = node.Declaration.WithVariables(SyntaxFactory.SeparatedList(list));
                    node = node.WithDeclaration(declaration);

                    return node;
                }

                return node;
            }

            private static bool NeedsRewrite(FieldDeclarationSyntax fieldSyntax, out bool isPublicOrConst)
            {
                isPublicOrConst = fieldSyntax.Modifiers.Any(m => m.Kind() == SyntaxKind.PublicKeyword || m.Kind() == SyntaxKind.ConstKeyword);

                foreach (var v in fieldSyntax.Declaration.Variables)
                {
                    if (!IsGoodFieldName(v.Identifier.ValueText, isPublicOrConst))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// This rewriter exists to work around DevDiv 1086632 in Roslyn.  The Rename action is 
        /// leaving a set of annotations in the tree.  These annotations slow down further processing
        /// and eventually make the rename operation unusable.  As a temporary work around we manually
        /// remove these from the tree.
        /// </summary>
        private sealed class CSharpRemoveRenameAnnotationsRewriter : CSharpSyntaxRewriter
        {
            public override SyntaxNode Visit(SyntaxNode node)
            {
                node = base.Visit(node);
                if (node != null && node.ContainsAnnotations && node.GetAnnotations(s_renameAnnotationName).Any())
                {
                    node = node.WithoutAnnotations(s_renameAnnotationName);
                }

                return node;
            }

            public override SyntaxToken VisitToken(SyntaxToken token)
            {
                token = base.VisitToken(token);
                if (token.ContainsAnnotations && token.GetAnnotations(s_renameAnnotationName).Any())
                {
                    token = token.WithoutAnnotations(s_renameAnnotationName);
                }

                return token;
            }
        }
    }
}
