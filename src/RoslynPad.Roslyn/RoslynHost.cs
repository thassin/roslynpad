﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using RoslynPad.Roslyn.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Linq;
using System.Reflection;
using AnalyzerReference = Microsoft.CodeAnalysis.Diagnostics.AnalyzerReference;
using AnalyzerFileReference = Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference;

namespace RoslynPad.Roslyn
{
    public class RoslynHost : IRoslynHost
    {
        internal static readonly ImmutableArray<string> PreprocessorSymbols =
            //ImmutableArray.CreateRange(new[] { "TRACE", "DEBUG" });
            ImmutableArray.CreateRange(new[] { "TRACE", "DEBUG", "AVALONIA" }); // TOMMIH

        internal static readonly ImmutableArray<Assembly> DefaultCompositionAssemblies =
            ImmutableArray.Create(
                // Microsoft.CodeAnalysis.Workspaces
                typeof(WorkspacesResources).Assembly,
                // Microsoft.CodeAnalysis.CSharp.Workspaces
                typeof(CSharpWorkspaceResources).Assembly,
                // Microsoft.CodeAnalysis.Features
                typeof(FeaturesResources).Assembly,
                // Microsoft.CodeAnalysis.CSharp.Features
                typeof(CSharpFeaturesResources).Assembly,
                // RoslynPad.Roslyn
                typeof(RoslynHost).Assembly);

        private readonly ConcurrentDictionary<DocumentId, RoslynWorkspace> _workspaces;
        private readonly ConcurrentDictionary<DocumentId, Action<DiagnosticsUpdatedArgs>> _diagnosticsUpdatedNotifiers;
        private readonly IDocumentationProviderService _documentationProviderService;
        private readonly CompositionHost _compositionContext;

        public HostServices HostServices { get; }
        public ParseOptions ParseOptions { get; }
        public ImmutableArray<MetadataReference> DefaultReferences { get; }
        public ImmutableArray<string> DefaultImports { get; }
        public ImmutableArray<string> DisabledDiagnostics { get; }

        public LanguageVersion LangVersion { get; private set; }

        public RoslynHost(LanguageVersion langVersion,
            IEnumerable<Assembly>? additionalAssemblies = null,
            RoslynHostReferences? references = null,
            ImmutableArray<string>? disabledDiagnostics = null)
        {
            LangVersion = langVersion;
            if (references == null) references = RoslynHostReferences.Empty;

            _workspaces = new ConcurrentDictionary<DocumentId, RoslynWorkspace>();
            _diagnosticsUpdatedNotifiers = new ConcurrentDictionary<DocumentId, Action<DiagnosticsUpdatedArgs>>();

            var assemblies = GetDefaultCompositionAssemblies();

            if (additionalAssemblies != null)
            {
                assemblies = assemblies.Concat(additionalAssemblies);
            }

            var partTypes = assemblies
                .SelectMany(x => x.DefinedTypes)
                .Select(x => x.AsType());

            _compositionContext = new ContainerConfiguration()
                .WithParts(partTypes)
                .CreateContainer();

            HostServices = MefHostServices.Create(_compositionContext);

            ParseOptions = CreateDefaultParseOptions();

            _documentationProviderService = GetService<IDocumentationProviderService>();

            DefaultReferences = references.GetReferences(DocumentationProviderFactory);
            DefaultImports = references.Imports;

            DisabledDiagnostics = disabledDiagnostics ?? ImmutableArray<string>.Empty;
            GetService<IDiagnosticService>().DiagnosticsUpdated += OnDiagnosticsUpdated;
        }

        public Func<string, DocumentationProvider> DocumentationProviderFactory => _documentationProviderService.GetDocumentationProvider;

        protected virtual IEnumerable<Assembly> GetDefaultCompositionAssemblies() =>
            DefaultCompositionAssemblies;

        protected virtual ParseOptions CreateDefaultParseOptions() => new CSharpParseOptions(
            preprocessorSymbols: PreprocessorSymbols,
            languageVersion: LangVersion);

        public MetadataReference CreateMetadataReference(string location) => MetadataReference.CreateFromFile(location,
            documentation: _documentationProviderService.GetDocumentationProvider(location));

        private void OnDiagnosticsUpdated(object? sender, DiagnosticsUpdatedArgs diagnosticsUpdatedArgs)
        {
            var documentId = diagnosticsUpdatedArgs.DocumentId;
            if (documentId == null) return;

            if (_diagnosticsUpdatedNotifiers.TryGetValue(documentId, out var notifier))
            {
                if (diagnosticsUpdatedArgs.Kind == DiagnosticsUpdatedKind.DiagnosticsCreated)
                {
                    var remove = diagnosticsUpdatedArgs.Diagnostics.RemoveAll(d => DisabledDiagnostics.Contains(d.Id));
                    if (remove.Length != diagnosticsUpdatedArgs.Diagnostics.Length)
                    {
                        diagnosticsUpdatedArgs = diagnosticsUpdatedArgs.WithDiagnostics(remove);
                    }
                }

                notifier(diagnosticsUpdatedArgs);
            }
        }

        public TService GetService<TService>() => _compositionContext.GetExport<TService>();

        protected internal virtual void AddMetadataReference(ProjectId projectId, AssemblyIdentity assemblyIdentity)
        {
            // TODO
        }

        public void CloseWorkspace(RoslynWorkspace workspace)
        {
            if (workspace == null) throw new ArgumentNullException(nameof(workspace));

            foreach (var documentId in workspace.CurrentSolution.Projects.SelectMany(p => p.DocumentIds))
            {
                _workspaces.TryRemove(documentId, out _);
                _diagnosticsUpdatedNotifiers.TryRemove(documentId, out _);
            }

            using (workspace) { }
        }

        public virtual RoslynWorkspace CreateWorkspace() => new(HostServices, roslynHost: this);

        public void CloseDocument(DocumentId documentId)
        {
            if (documentId == null) throw new ArgumentNullException(nameof(documentId));

            if (_workspaces.TryGetValue(documentId, out var workspace))
            {
                workspace.CloseDocument(documentId);

                var document = workspace.CurrentSolution.GetDocument(documentId);

                if (document != null)
                {
                    var solution = document.Project.RemoveDocument(documentId).Solution;

                    if (!solution.Projects.SelectMany(d => d.DocumentIds).Any())
                    {
                        _workspaces.TryRemove(documentId, out workspace);

                        using (workspace) { }
                    }
                    else
                    {
                        workspace.SetCurrentSolution(solution);
                    }
                }
            }

            _diagnosticsUpdatedNotifiers.TryRemove(documentId, out _);
        }

        public Document? GetDocument(DocumentId documentId)
        {
            if (documentId == null) throw new ArgumentNullException(nameof(documentId));

            return _workspaces.TryGetValue(documentId, out var workspace)
                ? workspace.CurrentSolution.GetDocument(documentId)
                : null;
        }

        public DocumentId AddDocument(DocumentCreationArgs args)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));

            return AddDocument(CreateWorkspace(), args);
        }

        public DocumentId AddRelatedDocument(DocumentId relatedDocumentId, DocumentCreationArgs args, bool addProjectReference = true)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));

            if (!_workspaces.TryGetValue(relatedDocumentId, out var workspace))
            {
                throw new ArgumentException("Unable to locate the document's workspace", nameof(relatedDocumentId));
            }

            var documentId = AddDocument(workspace, args,
                addProjectReference ? workspace.CurrentSolution.GetDocument(relatedDocumentId) : null);

            return documentId;
        }

        private DocumentId AddDocument(RoslynWorkspace workspace, DocumentCreationArgs args, Document? previousDocument = null)
        {
            var solution = workspace.CurrentSolution.AddAnalyzerReferences(GetSolutionAnalyzerReferences());
            var project = CreateProject(solution, args,
                CreateCompilationOptions(args, previousDocument == null), previousDocument?.Project);
            var document = CreateDocument(project, args);
            var documentId = document.Id;

            workspace.SetCurrentSolution(document.Project.Solution);
            workspace.OpenDocument(documentId, args.SourceTextContainer);

            _workspaces.TryAdd(documentId, workspace);

            if (args.OnDiagnosticsUpdated != null)
            {
                _diagnosticsUpdatedNotifiers.TryAdd(documentId, args.OnDiagnosticsUpdated);
            }

            var onTextUpdated = args.OnTextUpdated;
            if (onTextUpdated != null)
            {
                workspace.ApplyingTextChange += (d, s) =>
                {
                    if (documentId == d) onTextUpdated(s);
                };
            }

            return documentId;
        }

        protected virtual IEnumerable<AnalyzerReference> GetSolutionAnalyzerReferences()
        {
            var loader = GetService<IAnalyzerAssemblyLoader>();
            yield return new AnalyzerFileReference(typeof(Compilation).Assembly.Location, loader);
            yield return new AnalyzerFileReference(typeof(CSharpResources).Assembly.Location, loader);
            yield return new AnalyzerFileReference(typeof(FeaturesResources).Assembly.Location, loader);
            yield return new AnalyzerFileReference(typeof(CSharpFeaturesResources).Assembly.Location, loader);
        }

        public void UpdateDocument(Document document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            if (!_workspaces.TryGetValue(document.Id, out var workspace))
            {
                return;
            }

            workspace.TryApplyChanges(document.Project.Solution);
        }

        protected virtual CompilationOptions CreateCompilationOptions(DocumentCreationArgs args, bool addDefaultImports)
        {
            var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                usings: addDefaultImports ? DefaultImports : ImmutableArray<string>.Empty,
                allowUnsafe: true,
                sourceReferenceResolver: new SourceFileResolver(ImmutableArray<string>.Empty, args.WorkingDirectory),
                // all #r references are resolved by the editor/msbuild
                metadataReferenceResolver: DummyScriptMetadataResolver.Instance,
                nullableContextOptions: NullableContextOptions.Enable);
            return compilationOptions;
        }

        protected virtual Document CreateDocument(Project project, DocumentCreationArgs args)
        {
            var id = DocumentId.CreateNewId(project.Id);
            var solution = project.Solution.AddDocument(id, args.Name ?? project.Name, args.SourceTextContainer.CurrentText);
            return solution.GetDocument(id)!;
        }

        protected virtual Project CreateProject(Solution solution, DocumentCreationArgs args, CompilationOptions compilationOptions, Project? previousProject = null)
        {
            var name = args.Name ?? "New";
            var id = ProjectId.CreateNewId(name);

            var parseOptions = ParseOptions.WithKind(args.SourceCodeKind);
            var isScript = args.SourceCodeKind == SourceCodeKind.Script;

            if (isScript)
            {
                compilationOptions = compilationOptions.WithScriptClassName(name);
            }

            solution = solution.AddProject(ProjectInfo.Create(
                id,
                VersionStamp.Create(),
                name,
                name,
                LanguageNames.CSharp,
                isSubmission: isScript,
                parseOptions: parseOptions,
                compilationOptions: compilationOptions,
                metadataReferences: previousProject != null ? ImmutableArray<MetadataReference>.Empty : DefaultReferences,
                projectReferences: previousProject != null ? new[] { new ProjectReference(previousProject.Id) } : null));

            var project = solution.GetProject(id)!;

            if (!isScript && GetUsings(project) is { Length: > 0 } usings)
            {
                project = project.AddDocument("RoslynPadGeneratedUsings", usings).Project;
            }

            return project;

            static string GetUsings(Project project)
            {
                if (project.CompilationOptions is CSharpCompilationOptions options)
                {
                    return string.Join(" ", options.Usings.Select(i => $"global using {i};"));
                }

                return string.Empty;
            }
        }

        // new experimental stuff...
        // new experimental stuff...
        // new experimental stuff...

        // ...see src/CsEdit.Avalonia + samples/RoslynHostSample.

        // the methods below allow project setup with multiple source files in the same project.

        public void AddAnalyzerReferences(RoslynWorkspace workspace,ref Solution solution)
        {
            solution = workspace.CurrentSolution.AddAnalyzerReferences(GetSolutionAnalyzerReferences());
        }

        public virtual Project CreateProject_alt(RoslynWorkspace workspace, ref Solution solution, string name, SourceCodeKind kind, CompilationOptions compilationOptions, List<ProjectReference> projectReferences)
        {
            var id = ProjectId.CreateNewId(name);

            var parseOptions = ParseOptions.WithKind(kind);
            var isScript = kind == SourceCodeKind.Script;

            if (isScript)
            {
                compilationOptions = compilationOptions.WithScriptClassName(name);
            }

            Console.WriteLine( "CreateProject_alt : DefaultReferences.Length = " + DefaultReferences.Length );
            foreach ( var x in DefaultReferences ) Console.WriteLine( "    " + x.ToString() );

            solution = solution.AddProject(ProjectInfo.Create(
                id,
                VersionStamp.Create(),
                name,
                name,
                LanguageNames.CSharp,
                isSubmission: isScript,
                parseOptions: parseOptions,
                compilationOptions: compilationOptions,

                //metadataReferences: ImmutableArray<MetadataReference>.Empty, // WATCH OUT not working...
                metadataReferences: DefaultReferences,

                projectReferences: projectReferences.Count > 0 ? projectReferences : null

                ));

            workspace.SetCurrentSolution( solution );

            var project = solution.GetProject(id)!;

            if (!isScript && LangVersion >= LanguageVersion.CSharp10 && GetUsings(project) is { Length: > 0 } usings)
            {
                Document doc = project.AddDocument("RoslynPadGeneratedUsings", usings);
                project = doc.Project;
                solution = doc.Project.Solution; // TODO need this as well???

                Console.WriteLine("ADDED global usings:");
                Console.WriteLine(usings);
            }

            return project;

            static string GetUsings(Project project)
            {
                if (project.CompilationOptions is CSharpCompilationOptions options)
                {
                    return string.Join(" ", options.Usings.Select(i => $"global using {i};"));
                }

                return string.Empty;
            }
        }

        public virtual CompilationOptions CreateCompilationOptions_alt( bool enableNullableReferenceTypes )
        {
            // 20220809 these are not needed?
            const bool addDefaultImports = false;

            //Console.WriteLine( "CreateCompilationOptions_alt() : DefaultImports.Length = " + DefaultImports.Length );
            //foreach ( var x in DefaultImports ) Console.WriteLine( "    " + x.ToString() );

            var compilationOptions = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary,

                usings: addDefaultImports ? DefaultImports : ImmutableArray<string>.Empty,
                allowUnsafe: true,
                sourceReferenceResolver: null, // new SourceFileResolver(ImmutableArray<string>.Empty, workingDirectory),

                // all #r references are resolved by the editor/msbuild
                metadataReferenceResolver: null, // DummyScriptMetadataResolver.Instance, // not needed?

                // TODO nullableContextOptions is only for languageversion 8 and above.
                nullableContextOptions: enableNullableReferenceTypes ? NullableContextOptions.Enable : NullableContextOptions.Disable );

            return compilationOptions;
        }

        public DocumentId AddDocument_alt(RoslynWorkspace workspace, ref Solution solution, ref Project project, DocumentCreationArgs args, string name, string initialText)
        {
            var document = CreateDocument_alt(ref solution, ref project, args, name, initialText);
            var documentId = document.Id;

            workspace.SetCurrentSolution(solution);
            _workspaces.TryAdd(documentId, workspace);

            return documentId;
        }

        protected virtual Document CreateDocument_alt(ref Solution solution, ref Project project, DocumentCreationArgs args, string name, string initialText)
        {
            Console.WriteLine( "CreateDocument_alt() : name='" + name + "'" );

            Document doc = project.AddDocument( name, initialText );

            project = doc.Project;
            solution = doc.Project.Solution;
            return doc;
        }

        public void OpenDocument_alt( RoslynWorkspace workspace, DocumentId documentId, SourceTextContainer sourceTextContainer, Action<DiagnosticsUpdatedArgs>? onDiagnosticsUpdated, Action<SourceText>? onTextUpdated ) {

            workspace.OpenDocument(documentId, sourceTextContainer);

            if (onDiagnosticsUpdated != null)
            {
                _diagnosticsUpdatedNotifiers.TryAdd(documentId, onDiagnosticsUpdated);
            }

            if (onTextUpdated != null)
            {
                workspace.ApplyingTextChange += (d, s) =>
                {
Console.WriteLine( "ApplyingTextChange-lambda-called (never happens?)" );
                    if (documentId == d) onTextUpdated(s);
                    else Console.WriteLine( "ApplyingTextChange failed - documentid mismatch" );
                };
            }
        }

        public void CloseDocument_alt( RoslynWorkspace workspace, DocumentId documentId )
        {
Console.WriteLine();
Console.WriteLine("CLOSING DOCUMENT :: "+ documentId.ToString());
Console.WriteLine();
// https://docs.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.workspace?view=roslyn-dotnet-4.2.0 
            workspace.CloseDocument_fixme(documentId);
            _diagnosticsUpdatedNotifiers.TryRemove( documentId, out _ );
        }

        public void AddMetadataReference_alt(RoslynWorkspace workspace, ref Solution solution, ref Project project, MetadataReference mdr) {

            project = project.AddMetadataReference( mdr );

            solution = project.Solution;
        }
    }
}
