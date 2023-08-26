namespace MediatorRegistrations.Generator
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Text;

    class Program
    {
        static void PrintHelp()
        {
            Console.WriteLine("Custom Mediator Generator Tool");
            Console.WriteLine("Usage: CustomMediatorGeneratorTool <inputFolderPath> <outputFolderPath> <namespace>");
            Console.WriteLine("Arguments:");
            Console.WriteLine("  <inputFolderPath>    Path to the input folder containing .cs files.");
            Console.WriteLine("  <outputFolderPath>   Path to the output folder for generated registrations.");
            Console.WriteLine("  <namespace>          Namespace for the generated code.");
        }

        static void Main(string[] args)
        {

            if (args.Length < 3 || args[0] == "--help" || args[0] == "-h")
            {
                PrintHelp();
                return;
            }

            var inputFolderPath = args[0]; // Path to the input folder
            var outputFolderPath = args[1]; // Path to the output folder
            var @namespace = args[2]; // Namespace for the generated code

            var outputFilePath = Path.Combine(outputFolderPath, "GeneratedMediatorRegistrations.cs");
            if (File.Exists(outputFilePath))
            {
                File.Delete(outputFilePath);
            }

            var csFiles = Directory.GetFiles(inputFolderPath, "*.cs", SearchOption.AllDirectories);

            var compilation = CreateCompilation(csFiles);
            var generator = new VContainerCustomMediatorRegistrationGenerator(@namespace);

            var driver = CSharpGeneratorDriver.Create(generator);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

            var generatedSyntaxTrees = outputCompilation.SyntaxTrees;

            // Save the generated syntax trees to files or display them
            foreach (var syntaxTree in generatedSyntaxTrees)
            {
                var generatedCode = syntaxTree.ToString();
                if (generatedCode.IndexOf("public static class VContainerCustomMediatorRegistration") > -1)
                {

                    File.WriteAllText(outputFilePath, generatedCode);
                    Console.WriteLine($"Generated registrations saved to {outputFilePath}");
                    break;
                }
            }
        }

        static Compilation CreateCompilation(string[] filePaths)
        {
            var syntaxTrees = filePaths.Select(filePath => CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)));
            var references = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => MetadataReference.CreateFromFile(a.Location));
            var compilation = CSharpCompilation.Create("GeneratedMediatorRegistrations")
                .AddReferences(references)
                .AddSyntaxTrees(syntaxTrees);

            return compilation;
        }
    }


    public class VContainerCustomMediatorRegistrationGenerator : ISourceGenerator
    {

        private readonly string _ns;

        public VContainerCustomMediatorRegistrationGenerator(string ns)
        {
            _ns = ns;
        }

        public void Initialize(GeneratorInitializationContext context)  {   }

        public void Execute(GeneratorExecutionContext context)
        {
            var registrationCode = GenerateRegistrationCode(context.Compilation);

            // Add the generated source to the compilation
            var sourceText = SourceText.From(registrationCode, Encoding.UTF8);
            var sourceHintName = "GeneratedCustomMediatorRegistrations";
            context.AddSource(sourceHintName, sourceText);
        }

        private string GenerateRegistrationCode(Compilation compilation)
        {
            var handlerClasses = FindHandlerClasses(compilation);
            var usings = GetUsingsFromHandlerClasses(handlerClasses);
            var registrations = GenerateRegistrationBlock(handlerClasses, usings, compilation);

            return @$" // *** GENERATED ***
using VContainer;
using Mediator.Interfaces;
{usings}

namespace {_ns}
{{
    public static class VContainerCustomMediatorRegistration
    {{
        public static void RegisterMediatorHandlers(this IContainerBuilder builder)
        {{
{registrations}
        }}
    }}
}}
";
        }

        private IEnumerable<ClassDeclarationSyntax> FindHandlerClasses(Compilation compilation)
        {
            return compilation.SyntaxTrees
                .SelectMany(syntaxTree => syntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                .Where(classDeclaration =>
                    InheritsFromAbstractRequestHandler(classDeclaration, compilation));
        }

        private static bool InheritsFromAbstractRequestHandler(ClassDeclarationSyntax classDeclaration, Compilation compilation)
        {
            var symbol = compilation.GetSemanticModel(classDeclaration.SyntaxTree).GetDeclaredSymbol(classDeclaration);

            if (symbol == null)
                return false;

            var baseTypeSymbol = symbol.BaseType;

            while (baseTypeSymbol != null)
            {
                var baseTypeName = baseTypeSymbol.OriginalDefinition.ToDisplayString();
                if (baseTypeName.Contains("IQueryHandler<") || baseTypeName.Contains("ICommandHandler<"))
                {
                    return true;
                }

                baseTypeSymbol = baseTypeSymbol.BaseType;
            }

            return false;
        }

        private static string GetUsingsFromHandlerClasses(IEnumerable<ClassDeclarationSyntax> handlerClasses)
        {
            var usings = new HashSet<string>();

            foreach (var handlerClass in handlerClasses)
            {
                var syntaxTree = handlerClass.SyntaxTree;
                var root = syntaxTree.GetRoot();
                var usingDirectives = root.DescendantNodes().OfType<UsingDirectiveSyntax>();

                foreach (var usingDirective in usingDirectives)
                {
                    usings.Add(usingDirective.ToFullString().Trim());
                }
            }

            return string.Join(Environment.NewLine, usings);
        }

        private static string GenerateRegistrationBlock(IEnumerable<ClassDeclarationSyntax> handlerClasses, string usings, Compilation compilation)
        {
            var registrations = new StringBuilder();

            foreach (var handlerClass in handlerClasses)
            {
                var handlerName = handlerClass.Identifier.Text;

                var semanticModel = compilation.GetSemanticModel(handlerClass.SyntaxTree);
                var baseTypeSymbol = semanticModel.GetDeclaredSymbol(handlerClass)?.BaseType;

                if (baseTypeSymbol != null)
                {
                    if (baseTypeSymbol.ToString().Contains("IQueryHandler<"))
                    {
                        {
                            var typeArguments = baseTypeSymbol.TypeArguments;
                            if (typeArguments.Length == 2)
                            {
                                var queryTypeName = typeArguments[0].ToDisplayString();
                                var returnType = typeArguments[1].ToDisplayString();
                                var registrationLine = $"           builder{Environment.NewLine}" +
                                    $"              .Register<{queryTypeName}.{handlerName}>(Lifetime.Transient){Environment.NewLine}" +
                                    $"              .As(typeof(IQueryHandler<{queryTypeName}, {returnType}>));";
                                registrations.AppendLine(registrationLine);
                            }
                        }
                    }
                    else if(baseTypeSymbol.ToString().Contains("ICommandHandler<"))
                    {
                        var typeArguments = baseTypeSymbol.TypeArguments;
                        if (typeArguments.Length == 1)
                        {
                            var commandTypeName = typeArguments[0].ToDisplayString();
                            var registrationLine = $"           builder{Environment.NewLine}" +
                                $"              .Register<{commandTypeName}.{handlerName}>(Lifetime.Transient){Environment.NewLine}" +
                                $"              .As(typeof(ICommandHandler<{commandTypeName}>));";
                            registrations.AppendLine(registrationLine);
                        }
                    }
                }
                
                
            }

            return registrations.ToString();
        }
    }

}