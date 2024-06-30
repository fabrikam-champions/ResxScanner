using CommandLine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Resources.NetStandard;
using System.Text.Json;
using System.Threading.Tasks;

namespace ResxScanner
{
    public class Scanner
    {
        public static async Task ScanAsync(Options args)
        {
            try
            {
                using var workspace = MSBuildWorkspace.Create();
                var solution = workspace.OpenSolutionAsync(args.Source).GetAwaiter().GetResult();
                var keyDefinitions = GetKeyDefinitions(solution);
                var keyUsages = GetKeyUsages(solution).ToBlockingEnumerable();

                var allKeys = FullJoin(
                    keyUsages,
                    keyDefinitions,
                    u => new { u.ResourceName, u.Key },
                    d => new { d.ResourceName, d.Key },
                    (u, d) => new { ResourceName = d.ResourceName ?? u.ResourceName, Key = d.Key ?? u.Key, d.Value, d.Comment, d.Culture, u.Paths });
                var groupedKeys = from k in allKeys
                                  group k by new { k.ResourceName, k.Key } into g
                                  select new
                                  {
                                      Key = $"{g.Key.ResourceName}.{g.Key.Key}",
                                      En = g.Where(x => string.IsNullOrEmpty(x.Culture) || x.Culture.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).Max(),
                                      Ar = g.Where(x => !string.IsNullOrEmpty(x.Culture) && x.Culture.StartsWith("ar", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).Max(),
                                      Desc = string.Join(",", g.Select(x => x.Comment).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()),
                                      Usage = new
                                      {
                                          Count = g.SelectMany(x => x.Paths ?? []).Distinct().Count(),
                                          Paths = g.SelectMany(x => x.Paths ?? []).Distinct().Order().Take(args.MaxPathCount)
                                      }
                                  };
                var obj = groupedKeys.ToImmutableSortedDictionary(x => x.Key, x => new { x.En, x.Ar, x.Desc, x.Usage });
                var options = new JsonSerializerOptions { WriteIndented = false };
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(obj, options);
                await File.WriteAllBytesAsync(args.Destination, json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }
        private static IEnumerable<(string ResourceName, string Key, string Value, string Comment, string Culture)> GetKeyDefinitions(Solution solution)
        {
            foreach (var project in solution.Projects)
            {
                string[] resxFiles = Directory.GetFiles(Path.GetDirectoryName(project.FilePath), "*.resx", SearchOption.AllDirectories);
                var keys = resxFiles.SelectMany(resxFilePath =>
                {
                    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(resxFilePath).Split(Path.DirectorySeparatorChar).LastOrDefault();
                    string culture = null;
                    if (fileNameWithoutExtension.IndexOf('.') >= 0)
                    {
                        try
                        {
                            var c = fileNameWithoutExtension.Split('.').Last();
                            System.Globalization.CultureInfo.GetCultureInfo(c);
                            culture = c;
                        }
                        catch (System.Globalization.CultureNotFoundException)
                        {
                        }
                    }
                    var fileNameWithoutExtensionAndCulture = fileNameWithoutExtension.Substring(0, fileNameWithoutExtension.Length - (string.IsNullOrEmpty(culture) ? 0 : $".{culture}".Length));
                    var directoryPath = Directory.GetParent(resxFilePath).FullName;
                    var directoryNamespace = GetNamespaceForDirectory(project, directoryPath);
                    var resourceName = $"{directoryNamespace}.{fileNameWithoutExtensionAndCulture}";
                    using ResXResourceReader reader = new(resxFilePath) { UseResXDataNodes = true };
                    return reader.Cast<DictionaryEntry>().Select(node =>
                    {
                        var key = node.Key.ToString();
                        var value = ((ResXDataNode)node.Value).GetValue((ITypeResolutionService)null).ToString();
                        var comment = ((ResXDataNode)node.Value).Comment;
                        return (resourceName, key, value, comment, culture);
                    });
                });
                foreach (var key in keys)
                    yield return key;
            }
        }

        private static async IAsyncEnumerable<(string ResourceName, string Key, IEnumerable<string> Paths)> GetKeyUsages(Solution solution)
        {
            string solutionDirectory = Path.GetDirectoryName(solution.FilePath);

            foreach (var project in solution.Projects)
            {
                var docs = project.Documents.Where(document => Path.GetFileName(document.FilePath).EndsWith(".cs"));
                if (!docs.Any())
                    continue;

                foreach (var doc in docs)
                {
                    var root = await doc.GetSyntaxRootAsync();
                    var semanticModel = await doc.GetSemanticModelAsync();
                    var elements = root.DescendantNodesAndSelf().OfType<ElementAccessExpressionSyntax>().Where(element =>
                    {
                        var firstIdentifierName = element.DescendantNodes()?.FirstOrDefault();
                        if (firstIdentifierName != null && firstIdentifierName is IdentifierNameSyntax)
                        {
                            var symbol = semanticModel.GetSymbolInfo(firstIdentifierName).Symbol ?? semanticModel.GetSymbolInfo(firstIdentifierName).CandidateSymbols.FirstOrDefault();
                            var typeInfo = semanticModel.GetTypeInfo(firstIdentifierName);
                            var iStringLocalizerType = semanticModel.Compilation.GetTypeByMetadataName("Microsoft.Extensions.Localization.IStringLocalizer");
                            return semanticModel.Compilation.HasImplicitConversion(typeInfo.Type, iStringLocalizerType) || (symbol is IPropertySymbol && semanticModel.Compilation.HasImplicitConversion((symbol as IPropertySymbol)?.Type, iStringLocalizerType));
                        }
                        return false;
                    }).Select(element =>
                    {
                        var expression = element.DescendantNodes()?.OfType<ArgumentSyntax>().FirstOrDefault()?.Expression;
                        if (expression != null)
                        {
                            var key = semanticModel.GetConstantValue(expression).Value?.ToString();
                            string resourceName = null;
                            //Get the generic type if exists
                            var firstDescendingNode = element.DescendantNodes()?.FirstOrDefault();
                            if (firstDescendingNode != null)
                            {
                                var nodeType = semanticModel.GetTypeInfo(firstDescendingNode).Type as INamedTypeSymbol;
                                if (nodeType != null && nodeType.IsGenericType)
                                {
                                    resourceName = nodeType.TypeArguments.FirstOrDefault().ToDisplayString();
                                }
                            }
                            if (resourceName == null)
                            {
                                var classDeclarationSyntax = element.Ancestors()?.OfType<ClassDeclarationSyntax>().FirstOrDefault();
                                if (classDeclarationSyntax != null)
                                {
                                    resourceName = semanticModel.GetDeclaredSymbol(classDeclarationSyntax)?.ToDisplayString();
                                }
                            }
                            IEnumerable<string> paths = [];
                            SyntaxNode callerPropertyOrMethod = null;
                            foreach (var ancestor in element.Ancestors())
                            {
                                if (ancestor is PropertyDeclarationSyntax || ancestor is MethodDeclarationSyntax)
                                {
                                    callerPropertyOrMethod = ancestor;
                                    break;
                                }
                            }
                            if (callerPropertyOrMethod != null)
                            {
                                var callerPropertyOrMethodDeclaredSymbol = semanticModel.GetDeclaredSymbol(callerPropertyOrMethod);
                                if (callerPropertyOrMethodDeclaredSymbol != null)
                                {
                                    var references = SymbolFinder.FindReferencesAsync(callerPropertyOrMethodDeclaredSymbol, solution).GetAwaiter().GetResult();
                                    paths = references.SelectMany(r => r.Locations.Select(l=>l.Document.FilePath)).Union(references.SelectMany(r => r.Definition.Locations.Select(l => l.SourceTree.FilePath))).Where(path=> callerPropertyOrMethod is MethodDeclarationSyntax || path != doc.FilePath).Distinct().Select(path => Path.GetRelativePath(solutionDirectory, path));
                                }
                            }
                            return (resourceName, key, paths);
                        }
                        return (null, null, null);
                    }).Where(element => element.resourceName != null && element.key != null);
                    foreach (var element in elements)
                        yield return element;
                }
            }
        }
        private static IEnumerable<TResult> FullJoin<TOuter, TInner, TKey, TResult>(
            IEnumerable<TOuter> outer,
            IEnumerable<TInner> inner,
            Func<TOuter, TKey> outerKeySelector,
            Func<TInner, TKey> innerKeySelector,
            Func<TOuter, TInner, TResult> resultSelector)
        {
            var outerLookup = outer.ToLookup(outerKeySelector);
            var innerLookup = inner.ToLookup(innerKeySelector);

            var keys = new HashSet<TKey>(outerLookup.Select(g => g.Key)
                                                    .Concat(innerLookup.Select(g => g.Key)));

            foreach (var key in keys)
            {
                foreach (var outerItem in outerLookup[key].DefaultIfEmpty())
                {
                    foreach (var innerItem in innerLookup[key].DefaultIfEmpty())
                    {
                        yield return resultSelector(outerItem, innerItem);
                    }
                }
            }
        }

        static string GetNamespaceForDirectory(Project project, string directoryPath)
        {
            var projectDirectory = Path.GetDirectoryName(project.FilePath);
            if (!directoryPath.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
                return null;

            var relativePath = directoryPath.Substring(projectDirectory.Length).TrimStart(Path.DirectorySeparatorChar);
            var namespaceParts = relativePath.Split(Path.DirectorySeparatorChar)
                                             .Where(part => !string.IsNullOrWhiteSpace(part))
                                             .Select(part => part.Replace(" ", "_")); // To handle any spaces

            var rootNamespace = project.DefaultNamespace ?? GetRootNamespaceFromAssemblyName(project);
            var directoryNamespace = string.Join(".", new[] { rootNamespace }.Concat(namespaceParts));

            return directoryNamespace;
        }

        static string GetRootNamespaceFromAssemblyName(Project project)
        {
            return project.AssemblyName; // Fallback to assembly name if DefaultNamespace is not available
        }
    }
}
