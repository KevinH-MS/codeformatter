// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;

namespace Microsoft.DotNet.CodeFormatting.Rules
{
    [GlobalSemanticRule(FieldNamingRule.Name, FieldNamingRule.Description, GlobalSemanticRuleOrder.FieldNamingRule, isDefaultRule: true)]
    internal partial class FieldNamingRule : IGlobalSemanticFormattingRule
    {
        internal const string Name = "FieldNames";
        internal const string Description = "Prefix private fields with _ and Pascal case const fields";

        #region CommonRule

        private abstract class CommonRule
        {
            protected abstract SyntaxNode AddFieldAnnotations(SyntaxNode syntaxNode, out int count);

            /// <summary>
            /// This method exists to work around DevDiv 1086632 in Roslyn.  The Rename action is 
            /// leaving a set of annotations in the tree.  These annotations slow down further processing
            /// and eventually make the rename operation unusable.  As a temporary work around we manually
            /// remove these from the tree.
            /// </summary>
            protected abstract SyntaxNode RemoveRenameAnnotations(SyntaxNode syntaxNode);

            public async Task<Solution> ProcessAsync(Document document, SyntaxNode syntaxRoot, CancellationToken cancellationToken)
            {
                int count;
                var newSyntaxRoot = AddFieldAnnotations(syntaxRoot, out count);

                if (count == 0)
                {
                    return document.Project.Solution;
                }

                var documentId = document.Id;
                var solution = document.Project.Solution;
                solution = solution.WithDocumentSyntaxRoot(documentId, newSyntaxRoot);
                solution = await RenameFields(solution, documentId, count, cancellationToken);
                return solution;
            }

            private async Task<Solution> RenameFields(Solution solution, DocumentId documentId, int count, CancellationToken cancellationToken)
            {
                Solution oldSolution = null;
                for (int i = 0; i < count; i++)
                {
                    oldSolution = solution;

                    var semanticModel = await solution.GetDocument(documentId).GetSemanticModelAsync(cancellationToken);
                    var root = await semanticModel.SyntaxTree.GetRootAsync(cancellationToken);
                    var declaration = root.GetAnnotatedNodes(s_markerAnnotation).ElementAt(i);

                    // Make note, VB represents "fields" marked as "WithEvents" as properties, so don't be
                    // tempted to treat this as a IFieldSymbol. We only need the name, so ISymbol is enough.
                    var fieldSymbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                    var newName = GetNewFieldName(fieldSymbol, fieldSymbol.DeclaredAccessibility == Accessibility.Public || HasConstantValue(fieldSymbol));

                    // Can happen with pathologically bad field names like _
                    if (newName == fieldSymbol.Name)
                    {
                        continue;
                    }

                    solution = await Renamer.RenameSymbolAsync(solution, fieldSymbol, newName, solution.Workspace.Options, cancellationToken).ConfigureAwait(false);
                    solution = await CleanSolutionAsync(solution, oldSolution, cancellationToken);
                }

                return solution;
            }

            private static bool HasConstantValue(ISymbol symbol)
            {
                var field = symbol as IFieldSymbol;

                return field != null ? field.HasConstantValue : false;
            }

            private static string GetNewFieldName(ISymbol fieldSymbol, bool isPublicOrHasConstantValue)
            {
                var name = fieldSymbol.Name.Trim('_');
                if (name.Length > 2 && char.IsLetter(name[0]) && name[1] == '_')
                {
                    name = name.Substring(2);
                }

                // Some .NET code uses "ts_" prefix for thread static
                if (name.Length > 3 && name.StartsWith("ts_", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(3);
                }

                if (name.Length == 0)
                {
                    return fieldSymbol.Name;
                }

                if (isPublicOrHasConstantValue)
                {
                    return ToPascalCase(name);
                }

                if (name.Length > 2 && char.IsUpper(name[0]) && char.IsLower(name[1]))
                {
                    name = char.ToLower(name[0]) + name.Substring(1);
                }

                return "_" + name;
            }

            private static string ToPascalCase(string name)
            {
                var parts = name.Split('_');

                if (parts.Length == 0)
                {
                    return name;
                }

                var builder = new StringBuilder();
                foreach (var part in parts)
                {
                    if (part.Length < 1)
                    {
                        builder.Append(part);
                    }
                    else
                    {
                        builder.Append(char.ToUpper(part[0]));

                        if (part.Length > 1)
                        {
                            var rest = part.Substring(1);

                            if (rest.All(c => char.IsUpper(c)))
                            {
                                rest = rest.ToLower();
                            }
                            
                            builder.Append(rest);
                        }
                    }
                }

                return builder.ToString();
            }

            private async Task<Solution> CleanSolutionAsync(Solution newSolution, Solution oldSolution, CancellationToken cancellationToken)
            {
                var solution = newSolution;

                foreach (var projectChange in newSolution.GetChanges(oldSolution).GetProjectChanges())
                {
                    foreach (var documentId in projectChange.GetChangedDocuments())
                    {
                        solution = await CleanSolutionDocument(solution, documentId, cancellationToken);
                    }
                }

                return solution;
            }

            private async Task<Solution> CleanSolutionDocument(Solution solution, DocumentId documentId, CancellationToken cancellationToken)
            {
                var document = solution.GetDocument(documentId);
                var syntaxNode = await document.GetSyntaxRootAsync(cancellationToken);
                if (syntaxNode == null)
                {
                    return solution;
                }

                var newNode = RemoveRenameAnnotations(syntaxNode);
                return solution.WithDocumentSyntaxRoot(documentId, newNode);
            }
        }

        #endregion

        private const string s_renameAnnotationName = "Rename";

        private readonly static SyntaxAnnotation s_markerAnnotation = new SyntaxAnnotation("FieldToRename");

        // Used to avoid the array allocation on calls to WithAdditionalAnnotations
        private readonly static SyntaxAnnotation[] s_markerAnnotationArray;

        static FieldNamingRule()
        {
            s_markerAnnotationArray = new[] { s_markerAnnotation };
        }

        private readonly CSharpRule _csharpRule = new CSharpRule();
        private readonly VisualBasicRule _visualBasicRule = new VisualBasicRule();

        public bool SupportsLanguage(string languageName)
        {
            return
                languageName == LanguageNames.CSharp ||
                languageName == LanguageNames.VisualBasic;
        }

        public Task<Solution> ProcessAsync(Document document, SyntaxNode syntaxRoot, CancellationToken cancellationToken)
        {
            switch (document.Project.Language)
            {
                case LanguageNames.CSharp:
                    return _csharpRule.ProcessAsync(document, syntaxRoot, cancellationToken);
                case LanguageNames.VisualBasic:
                    return _visualBasicRule.ProcessAsync(document, syntaxRoot, cancellationToken);
                default:
                    throw new NotSupportedException();
            }
        }

        private static bool IsGoodFieldName(string name, bool isPublicOrConst)
        {
            if (name.Length < 1)
            {
                return false;
            }

            if (!isPublicOrConst)
            {
                return name[0] == '_';
            }

            if (char.IsLower(name[0]) || name.Contains('_'))
            {
                return false;
            }

            return name.Length > 1 ? name.Any(c => char.IsLower(c)) : true;
        }
    }
}
